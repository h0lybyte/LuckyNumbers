using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>What the title's slot picker came back with.</summary>
    public readonly struct SlotPick
    {
        /// <summary>The slot (-1: none: no page, so the run isn't saved).</summary>
        public readonly int Index;
        /// <summary>Back to the title (the picker was closed).</summary>
        public readonly bool Back;
        /// <summary>The slot holds a save: continue it (else a new character goes there).</summary>
        public readonly bool Continue;

        public SlotPick(int index, bool back, bool cont)
        {
            Index = index;
            Back = back;
            Continue = cont;
        }
    }

    /// <summary>Save slots, for the title's Play.</summary>
    public interface ISaveSlots
    {
        /// <summary>Opens the page's picker until a slot (or Back) is picked; at once with no page (Index -1).</summary>
        UniTask<SlotPick> PickAsync(CancellationToken ct);

        /// <summary>Put slot `index`'s run back (universe, character, pack, ship, credits, job); it saves there from now on.</summary>
        void Load(int index);

        /// <summary>A new run saves to slot `index` from now on (saved at once).</summary>
        void Begin(int index);
    }

    /// <summary>
    /// Save slots (SaveSlots state; proto save.proto). A slot is one JSON record in PlayerPrefs (on the web, the
    /// browser's IndexedDB): the lucky number and dice, the character (ICharacterSave: its scores roll again from the
    /// seed), the pack (IPackSave), the ship's skin and fuel, credits and trips, and the job. Play on the title asks
    /// which slot (ISaveSlots); the run then saves to it before each scene change, on arriving, and every
    /// AutosaveSeconds. A continued run wakes at the station: back from a planet (PlayerVoyage.returning, so relics
    /// sell and a finished job pays), beside the Flight School console, or (saved outside) beside your docked ship. Back on the title, nothing saves until a slot
    /// is picked again.
    /// </summary>
    [Presenter]
    public sealed class SavePresenter : ISaveSlots, IStartable, ITickable, IDisposable
    {
        public const int SlotCount = 3;
        const int Version = 1;
        const float AutosaveSeconds = 30f;
        const string KeyPrefix = "ln.save.";

        [Serializable]
        sealed class SaveFile
        {
            public int version;
            public long played;
            public string scene;
            // The universe: the lucky number, its dice roles (0..1, -1 none) and the faces.
            public uint seed;
            public float star, worlds, luck, moons;
            public int[] faces;
            public CharacterSave character;
            public List<SavedItem> pack;
            public string skin;
            public float fuel;
            public int credits, trips;
            public JobSave job;
            public string[] upgrades;   // PlayerShip.upgrades, "id|level" (absent in older saves)
            public uint dice;           // the dice drive's roll
            public string universe;     // UniverseState.id (absent in older saves: the default, home)
        }

        [Serializable]
        sealed class JobSave
        {
            public bool active, done;
            public int kind, danger, pay, count, goal, completed;
            public string title, objective, client, body, planet, target;
            public uint seed;
            public string[] recent;   // PlayerJob.RecentTargets (absent in older saves)
        }

        readonly SaveSlots _state;
        readonly FlowState _flow;
        readonly DiceState _dice;
        readonly PlayerShip _ship;
        readonly PlayerVoyage _voyage;
        readonly PlayerJob _job;
        readonly ICharacterSave _character;
        readonly IPackSave _pack;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly IBufferedPublisher<UniverseSeedChanged> _seedPublisher;
        readonly UniverseState _universeState;
        readonly IPublisher<UniverseSelected> _universePublisher;
        readonly ISubscriber<SaveSlotsReady> _ready;
        readonly ISubscriber<SaveSlotPicked> _picked;
        readonly ISubscriber<SaveSlotDeleteRequested> _deletes;
        readonly ISubscriber<SceneChangeRequested> _leaving;
        readonly ISubscriber<SceneEntered> _entered;
        readonly ISubscriber<RunEnded> _runEnded;
        readonly List<IDisposable> _subscriptions = new();
        UniTaskCompletionSource<int> _pick;
        UniverseSeedChanged _universe = new(1u, -1f, -1f, -1f, -1f);
        bool _page;
        float _nextAutosave;

        [Inject]
        public SavePresenter(SaveSlots state, FlowState flow, DiceState dice, PlayerShip ship, PlayerVoyage voyage,
                             PlayerJob job, ICharacterSave character, IPackSave pack,
                             IBufferedSubscriber<UniverseSeedChanged> seeds, IBufferedPublisher<UniverseSeedChanged> seedPublisher,
                             ISubscriber<SaveSlotsReady> ready, ISubscriber<SaveSlotPicked> picked,
                             ISubscriber<SaveSlotDeleteRequested> deletes,
                             ISubscriber<SceneChangeRequested> leaving, ISubscriber<SceneEntered> entered,
                             UniverseState universeState, IPublisher<UniverseSelected> universePublisher,
                             ISubscriber<RunEnded> runEnded)
        {
            _runEnded = runEnded;
            _universeState = universeState;
            _universePublisher = universePublisher;
            _state = state;
            _flow = flow;
            _dice = dice;
            _ship = ship;
            _voyage = voyage;
            _job = job;
            _character = character;
            _pack = pack;
            _seeds = seeds;
            _seedPublisher = seedPublisher;
            _ready = ready;
            _picked = picked;
            _deletes = deletes;
            _leaving = leaving;
            _entered = entered;
        }

        public void Start()
        {
            _state.Active.Value = -1;
            _state.Open.Value = false;
            Refresh();
            _subscriptions.Add(_seeds.Subscribe(e => _universe = e));
            _subscriptions.Add(_ready.Subscribe(e => _page = e.On));
            _subscriptions.Add(_picked.Subscribe(e => _pick?.TrySetResult(e.Index)));
            _subscriptions.Add(_deletes.Subscribe(e => Delete(e.Index)));
            _subscriptions.Add(_runEnded.Subscribe(e => End(e.Reason)));
            // Before a scene goes (the state is still whole), and once the next one is in.
            _subscriptions.Add(_leaving.Subscribe(_ => Save()));
            _subscriptions.Add(_entered.Subscribe(e =>
            {
                if (e.Scene == GameScene.Title) _state.Active.Value = -1;   // a new run unless a slot is picked
                else Save();
            }));
        }

        public void Tick()
        {
            if (_state.Active.Value < 0 || Time.unscaledTime < _nextAutosave) return;
            Save();
        }

        // ---- ISaveSlots ---------------------------------------------------------------------------------------

        public async UniTask<SlotPick> PickAsync(CancellationToken ct)
        {
            if (!_page) return new SlotPick(-1, false, false);
            Refresh();
            _pick?.TrySetCanceled();
            _pick = new UniTaskCompletionSource<int>();
            _state.Open.Value = true;
            int index;
            try
            {
                using (ct.Register(() => _pick?.TrySetCanceled()))
                    index = await _pick.Task;
            }
            finally
            {
                _state.Open.Value = false;
                _pick = null;
            }
            if (index < 0 || index >= SlotCount) return new SlotPick(-1, true, false);
            return new SlotPick(index, false, Read(index) != null);
        }

        public void Load(int index)
        {
            var save = Read(index);
            if (save == null) return;
            // The universe first (the character's scores roll from it), then the character, then its pack.
            if (save.faces != null)
            {
                _dice.Faces.Clear();
                foreach (var f in save.faces) _dice.Faces.Add(f);
            }
            _universePublisher.Publish(new UniverseSelected(save.universe ?? "", 0));   // which universe, before its roll
            _dice.Seed.Value = save.seed;
            _seedPublisher.Publish(new UniverseSeedChanged(save.seed, save.star, save.worlds, save.luck, save.moons));
            _character.Restore(save.character);
            _pack.Restore(save.pack);

            if (!string.IsNullOrEmpty(save.skin)) _ship.Skin.Value = save.skin;
            _ship.Upgrades.Clear();
            if (save.upgrades != null) foreach (var u in save.upgrades) if (!string.IsNullOrEmpty(u)) _ship.Upgrades.Add(u);
            _ship.Dice.Value = save.dice;
            PlayerShipStats.Refuel(_ship);   // the tank's size for that skin
            _ship.Fuel.Value = Mathf.Clamp(save.fuel, 0f, _ship.FuelMax.Value);
            _voyage.Credits.Value = save.credits;
            _voyage.Trips.Value = save.trips;
            _voyage.LastPayout.Value = 0;
            _voyage.Rescued.Value = false;
            // You wake at the station: back from wherever the save was.
            _voyage.Arrival.Value = save.scene switch
            {
                nameof(GameScene.Planet) or nameof(GameScene.Intro) => StationArrival.Planet,
                nameof(GameScene.FlightSchool) => StationArrival.FlightSchool,
                nameof(GameScene.Orbit) => StationArrival.Orbit,   // flying round outside: docked again
                _ => StationArrival.None,
            };
            RestoreJob(save.job);

            _state.Active.Value = index;
            _nextAutosave = Time.unscaledTime + AutosaveSeconds;
            Debug.Log($"[Save] loaded slot {index + 1}: {save.character?.callsign}, lucky number {save.seed}, {save.scene}");
        }

        public void Begin(int index)
        {
            if (index < 0 || index >= SlotCount) return;
            _state.Active.Value = index;
            Save();
        }

        // ---- slots --------------------------------------------------------------------------------------------

        void Save()
        {
            var index = _state.Active.Value;
            if (index < 0) return;
            _nextAutosave = Time.unscaledTime + AutosaveSeconds;
            var scene = _flow.Scene.Value;
            var file = new SaveFile
            {
                version = Version,
                played = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                scene = (scene == GameScene.Title || scene == GameScene.Unspecified ? GameScene.Station : scene).ToString(),
                seed = _universe.Seed,
                star = _universe.Star, worlds = _universe.Worlds, luck = _universe.Luck, moons = _universe.Moons,
                faces = new List<int>(_dice.Faces).ToArray(),
                character = _character.Capture(),
                pack = _pack.Capture(),
                skin = _ship.Skin.Value,
                fuel = _ship.Fuel.Value,
                credits = _voyage.Credits.Value,
                trips = _voyage.Trips.Value,
                job = CaptureJob(),
                upgrades = new List<string>(_ship.Upgrades).ToArray(),
                dice = _ship.Dice.Value,
                universe = _universeState.Id.Value ?? "",
            };
            PlayerPrefs.SetString(KeyPrefix + index, JsonUtility.ToJson(file));
            PlayerPrefs.Save();
            Refresh();
            Debug.Log($"[Save] slot {index + 1}: {file.character?.callsign} at {file.scene}");
        }

        /// <summary>
        /// Game over (RunEnded): the slot it was saving to goes, nothing saves from now on, and the run's money, ship
        /// and job are cleared, so a new run from the title starts clean.
        /// </summary>
        void End(string reason)
        {
            var index = _state.Active.Value;
            _state.Active.Value = -1;
            if (index >= 0 && index < SlotCount)
            {
                PlayerPrefs.DeleteKey(KeyPrefix + index);
                PlayerPrefs.Save();
            }
            _voyage.Credits.Value = 0;
            _voyage.LastPayout.Value = 0;
            _voyage.Trips.Value = 0;
            _voyage.RestoreBill.Value = 0;
            _voyage.Rescued.Value = false;
            _voyage.Arrival.Value = StationArrival.None;
            _ship.Upgrades.Clear();
            _job.Active.Value = false;
            _job.Completed.Value = 0;
            Refresh();
            Debug.Log($"[Save] run over ({reason}): slot {index + 1} deleted");
        }

        void Delete(int index)
        {
            if (index < 0 || index >= SlotCount || index == _state.Active.Value) return;
            PlayerPrefs.DeleteKey(KeyPrefix + index);
            PlayerPrefs.Save();
            Refresh();
            Debug.Log($"[Save] slot {index + 1} deleted");
        }

        static SaveFile Read(int index)
        {
            var json = PlayerPrefs.GetString(KeyPrefix + index, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var file = JsonUtility.FromJson<SaveFile>(json);
                return file != null && file.version > 0 && file.character != null ? file : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Save] slot {index + 1} unreadable: {e.Message}");
                return null;
            }
        }

        /// <summary>The page's list: each slot "callsign|class|level|seed|played|where|credits", "" when empty.</summary>
        void Refresh()
        {
            var rows = new List<string>(SlotCount);
            for (var i = 0; i < SlotCount; i++)
            {
                var f = Read(i);
                if (f == null) { rows.Add(""); continue; }
                var c = f.character;
                var cls = GameContent.Character.Classes.Find(k => k.Id == c.classId)?.Name ?? c.classId;
                var where = f.scene == nameof(GameScene.Planet) && !string.IsNullOrEmpty(f.job?.planet) ? f.job.planet
                    : f.scene == nameof(GameScene.FlightSchool) ? "Flight School"
                    : f.scene == nameof(GameScene.Orbit) ? "Outside the station"
                    : f.scene == nameof(GameScene.Intro) ? "Planet" : f.scene;
                rows.Add($"{c.callsign}|{cls}|{c.level}|{f.seed}|{f.played}|{where}|{f.credits}");
            }
            var same = _state.Slots.Count == rows.Count;
            for (var i = 0; same && i < rows.Count; i++) same = _state.Slots[i] == rows[i];
            if (same) return;
            _state.Slots.Clear();
            foreach (var r in rows) _state.Slots.Add(r);
        }

        JobSave CaptureJob() => new()
        {
            active = _job.Active.Value, done = _job.Done.Value, kind = (int)_job.Kind.Value, danger = _job.Danger.Value,
            pay = _job.Pay.Value, count = _job.Count.Value, goal = _job.Goal.Value, completed = _job.Completed.Value,
            title = _job.Title.Value, objective = _job.Objective.Value, client = _job.Client.Value, body = _job.Body.Value,
            planet = _job.Planet.Value, target = _job.Target.Value, seed = _job.Seed.Value,
            recent = new List<string>(_job.RecentTargets).ToArray(),
        };

        void RestoreJob(JobSave j)
        {
            j ??= new JobSave();
            _job.Kind.Value = (JobKind)j.kind;
            _job.Title.Value = j.title ?? "";
            _job.Objective.Value = j.objective ?? "";
            _job.Client.Value = j.client ?? "";
            _job.Body.Value = j.body ?? "";
            _job.Planet.Value = j.planet ?? "";
            _job.Danger.Value = j.danger;
            _job.Pay.Value = j.pay;
            _job.Seed.Value = j.seed;
            _job.Target.Value = j.target ?? "";
            _job.Count.Value = j.count;
            _job.Goal.Value = j.goal;
            _job.Done.Value = j.done;
            _job.Completed.Value = j.completed;
            _job.RecentTargets.Clear();
            if (j.recent != null) foreach (var r in j.recent) if (!string.IsNullOrEmpty(r)) _job.RecentTargets.Add(r);
            _job.Active.Value = j.active;
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            _pick?.TrySetCanceled();
        }
    }
}
