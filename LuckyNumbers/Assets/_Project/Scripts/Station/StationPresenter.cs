using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The player's station: builds it from content/station.txtpb, puts the player in their cabin with first-person
    /// control, and hangs it in their universe (two scales, two cameras: the universe is seen from a fixed point in
    /// orbit, turned with the player's head every frame; the station and arms draw over it, so windows look out at
    /// the real system). Their ship waits in the docking bay: E beside it boards and goes on to StationScene.next.
    ///
    /// Walking into the call's room rings it (E, or the page's Answer, picks up); Dispatch's lines play in the dialogue
    /// box, then the objective points at the ship. Consoles (E): lights on / off / emergency, the PS1 look. Doors slide
    /// open as you come near. The page can switch lights, the PS1 look and rebuild the whole plan (layout editor).
    ///
    /// Back from the planet (PlayerVoyage.returning): you step off beside your ship and the museum calls at once
    /// (StationScene.homecoming), buying every relic in your pack for its value (credits) and XP for each.
    ///
    /// Jobs (content/jobs.txtpb, PlayerJob): the first call is the first job (the freighter's mayday, then the intro).
    /// Back from a job, Dispatch says whether it's done (paid) and rings again, and sends a few seeded jobs to your
    /// ship. You pick one when you're about to leave: E at the ship opens the board (JobBoard; the page picks one,
    /// JobAcceptRequested) and taking a job flies you straight down to its planet. Esc or walking away closes it. The
    /// board is there even if you skipped the call.
    ///
    /// Flight School: a console in the Docking Bay (STATION_ACTION_FLIGHT_SCHOOL) takes you to the training deck
    /// (GameScene.FlightSchool) to test-fly your ship; back from it (PlayerVoyage.training) you step out beside it.
    /// The Docking Bay's launch console takes your ship out round the station (GameScene.Orbit); docked again
    /// (PlayerVoyage.docked) you step off beside it.
    ///
    /// The Docking Bay's outfitter console (STATION_ACTION_OUTFITTER) opens the outfitter (Outfitter state; the page's
    /// panel, OutfitterPresenter sells): ship upgrades for your credits. E again, Esc or walking away closes it. Upgrades
    /// that raise what you're paid (ShipUpgrades.Payout) scale the homecoming's payout and the job's pay.
    /// </summary>
    [Presenter]
    public sealed class StationPresenter : IStartable, IPostLateTickable, IDisposable
    {
        const float UseDistance = 1.7f;   // metres from a console to use it

        readonly StationScene _config;
        readonly StationView _station;
        readonly PlayerView _player;
        readonly IFirstPerson _firstPerson;
        readonly ISpaceDirector _space;
        readonly PlanetView _ui;
        readonly ISceneFlow _flow;
        readonly StationState _state;
        readonly ShipView _ship;
        readonly PlayerShip _playerShip;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly ISubscriber<StationLightsRequested> _lightRequests;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly ISubscriber<StationCallAnswered> _answers;
        readonly ISubscriber<StationPlanRequested> _plans;
        readonly ISubscriber<PsxModeRequested> _psxRequests;
        readonly PlayerVoyage _voyage;
        readonly IInventory _pack;
        readonly IPublisher<CharacterXpGained> _xp;
        StationArrival _arrival;   // where you came in from (PlayerVoyage.arrival, spent as the station loads)
        // Jobs: which call is ringing / playing, the board's offers, the body the station orbits.
        enum CallKind { Dispatch, Homecoming, Board }
        CallKind _ringKind, _activeKind;
        readonly PlayerJob _job;
        readonly JobBoard _board;
        readonly LandingSite _landing;
        readonly ISubscriber<JobAcceptRequested> _accepts;
        readonly RangeRoom _range;                              // the shooting range room (StationScene.range)
        readonly ISubscriber<RangeResetRequested> _rangeResets;
        readonly Outfitter _outfitter;                          // the outfitter panel (OutfitterPresenter)
        Vector3 _outfitterAt;                                   // its console: walking away closes it
        List<Jobs.Offer> _offers = new();
        string _orbitBody = "";
        static JobConfig J => GameContent.Jobs;
        // The first trip plays the intro cutscene (StationScene.next); later ones fly straight down.
        GameScene NextScene => _voyage.Trips.Value == 0 ? _config.Next : GameScene.Planet;
        // Later trips need a job from the board first.
        bool NeedsJob => !_job.Active.Value && _voyage.Trips.Value > 0;
        /// <summary>Dev (the page's URL): ?ln-board starts you beside the ship between jobs, to try the job board.</summary>
        static bool DevBoard => (Application.absoluteURL ?? "").Contains("ln-board");
        /// <summary>Dev: ?ln-outfitter starts you at the outfitter, open, with some credits to spend if you have none.</summary>
        static bool DevOutfitter => (Application.absoluteURL ?? "").Contains("ln-outfitter");
        // The call playing (Dispatch's, or the museum's on your return), and what the museum paid.
        List<StationCallLine> _callLines;
        GameScene _next;
        float _callCps;
        string _callObjective, _caller = "", _relics = "", _payout = "0";
        int _relicCount;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        uint _layoutSeed;   // the lucky number the generated layout was built from (0: content's plan)
        readonly CancellationTokenSource _cts = new();
        readonly List<IDisposable> _subscriptions = new();
        static readonly int Psx = Shader.PropertyToID("_LnPsx");

        StationLayout _layout;   // as built: content's, seeded (generator), or the page's editor
        readonly Dictionary<Vector2Int, string> _roomOf = new();
        /// <summary>Key presses since the last PostLateTick, latched by the input subscription; taken and cleared in one go.</summary>
        [Flags] enum Press : byte { None = 0, Interact = 1 << 0, ToggleMap = 1 << 1, CloseMap = 1 << 2 }

        Press _press;
        bool _inSpace;
        bool Leaving => (_firstPerson.Locks & ControlLock.Leaving) != 0;   // fading out to the next scene
        Vector3 _orbitPosition, _dockPosition;
        Bounds _hull;   // the docked ship's world bounds (boarding is measured from it)
        Quaternion _stationInSpace = Quaternion.identity;
        float _fade = 1f, _hintAlpha;
        string _planet = "the planet", _freighter = "the freighter", _museum = "Museum";

        // The call.
        int _line = -1;
        float _lineTime;

        [Inject]
        public StationPresenter(StationScene config, StationView station, PlayerView player, IFirstPerson firstPerson,
                                ISpaceDirector space, PlanetView ui, ISceneFlow flow, StationState state, ShipView ship,
                                PlayerShip playerShip, IPublisher<SceneChangeRequested> scenes,
                                ISubscriber<StationLightsRequested> lightRequests, ISubscriber<InputActionPerformed> actions,
                                ISubscriber<StationCallAnswered> answers, ISubscriber<StationPlanRequested> plans,
                                ISubscriber<PsxModeRequested> psxRequests, PlayerVoyage voyage, IInventory pack,
                                IPublisher<CharacterXpGained> xp, PlayerJob job, JobBoard board, LandingSite landing,
                                ISubscriber<JobAcceptRequested> accepts, ISubscriber<DevWarpRequested> warps, ArmsView arms,
                                RangeState rangeState, ISubscriber<RangeResetRequested> rangeResets, CharacterSheet sheet,
                                IPublisher<CombatRoll> rolls, WeaponRack rack, IBufferedSubscriber<UniverseSeedChanged> seeds,
                                Outfitter outfitter)
        {
            _outfitter = outfitter;
            _seeds = seeds;
            _rack = rack;
            _range = new RangeRoom(station, player, arms, rangeState, warps, (at, yaw, pitch) => Teleport(at, yaw, pitch), firstPerson,
                                   sheet, rolls, rack);
            _rangeResets = rangeResets;
            _job = job;
            _board = board;
            _landing = landing;
            _accepts = accepts;
            _voyage = voyage;
            _pack = pack;
            _xp = xp;
            _config = config;
            _station = station;
            _player = player;
            _firstPerson = firstPerson;
            _space = space;
            _ui = ui;
            _flow = flow;
            _state = state;
            _ship = ship;
            _playerShip = playerShip;
            _scenes = scenes;
            _lightRequests = lightRequests;
            _actions = actions;
            _answers = answers;
            _plans = plans;
            _psxRequests = psxRequests;
            _warps = warps;
        }

        public void Start()
        {
            // Your station from your lucky number, built once: the roll is buffered (the title made it), so it's here
            // now. No roll yet (the scene run on its own): content's plan, rebuilt once the universe has a seed.
            var seed = 0u;
            using (_seeds.Subscribe(e => seed = e.Seed)) { }
            if (seed != 0 && Generated) Build(LayoutGenerator.Generate(_config, _layoutSeed = seed));
            else Build(StationLayout.FromContent(_config));
            SetLights(StationLightMode.On);
            SetGlow(_config.Glow);
            SetPsx(false);
            _arrival = _voyage.Arrival.Value;
            _voyage.Arrival.Value = StationArrival.None;
            if (_arrival == StationArrival.Planet && _voyage.Trips.Value == 0) _voyage.Trips.Value = 1;   // back from a planet: you've been down
            _state.Call.Value = CallPhase.None;
            _caller = _config.Call?.Caller ?? "";
            _state.Caller.Value = _caller;
            _state.Objective.Value = "";
            _subscriptions.Add(_lightRequests.Subscribe(e => SetLights(e.Mode)));
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _press |= Press.Interact;
                if (e.Action == InputAction.Map) _press |= Press.ToggleMap;      // M: the station map (overlay)
                if (e.Action == InputAction.SideView) _range.CycleSideView();   // V: in the range, the hands side view
                if (e.Action == InputAction.Cancel) _press |= Press.CloseMap;
            }));
            _state.MapOpen.Value = false;
            _subscriptions.Add(_answers.Subscribe(_ => Answer()));
            _subscriptions.Add(_plans.Subscribe(e => Rebuild(e.Plan)));
            _subscriptions.Add(_warps.Subscribe(e => WarpTo(e.To)));
            _subscriptions.Add(_psxRequests.Subscribe(e => SetPsx(e.On)));
            _subscriptions.Add(_accepts.Subscribe(e => Accept(e.Index)));
            _subscriptions.Add(_rangeResets.Subscribe(_ => _range.Reset()));
            _board.Open.Value = false;

            var spawn = StationPlan.Centre(_station.Plan.Spawn);
            Teleport(spawn, _config.SpawnYaw);
            _firstPerson.Unlock(ControlLock.Spawn);
            PlaceWeapon(spawn, _config.SpawnYaw);

            _ui.Bind();
            _ui.Fade(1f);
            _ui.Dialogue(null, null, 0);
            _ui.MapHint(null);
            _hintAlpha = 1f;
            _ui.Hint(_config.Hint, _hintAlpha);
            DockShip();
            if (DevBoard && _voyage.Trips.Value == 0 && !_job.Active.Value)
            {
                _voyage.Trips.Value = 1;   // between jobs: the board at the ship
                Debug.Log("[Station] dev: ?ln-board, between jobs beside the ship");
            }
            PlaceOnArrival();
            if (DevOutfitter) DevOpenOutfitter().Forget();
            _flow.Preload(NextScene);
            PlaceInSpace(_cts.Token).Forget();
        }

        bool Generated => _config.Generator != null && _config.Generator.Enabled;

        void Build(StationLayout layout)
        {
            _layout = layout;
            _station.Build(_config, layout);
            _range.Build(_config.Range, layout);
            _roomOf.Clear();
            var plan = _station.Plan;
            foreach (var cells in plan.Rooms())
            {
                string name = null;
                foreach (var r in layout.Rooms)
                    if (cells.Contains(plan.PlanCell(r.Col, r.Row))) name = r.Name;
                if (name == null) continue;
                foreach (var c in cells) _roomOf[c] = name;
            }
            _state.Plan.Value = string.Join("\n", layout.Plan);
            // For the map: every room ("name|col|row") and the dock.
            _state.Rooms.Clear();
            foreach (var r in layout.Rooms) _state.Rooms.Add($"{r.Name}|{r.Col}|{r.Row}");
            if (layout.Dock != null)
            {
                _state.DockCol.Value = layout.Dock.Col;
                _state.DockRow.Value = layout.Dock.Row;
            }
        }

        /// <summary>Lamp glow: the scene's bloom (a Volume built by SceneBuilder), from content.</summary>
        static void SetGlow(float intensity)
        {
            var volume = UnityEngine.Object.FindAnyObjectByType<Volume>();
            if (volume == null || volume.sharedProfile == null || !volume.sharedProfile.TryGet<Bloom>(out var bloom)) return;
            bloom.active = intensity > 0f;
            bloom.intensity.Override(intensity);
        }

        /// <summary>Which of Prefabs/Weapons lies by the spawn (any of the guns: their hold points are measured).</summary>
        const string TestGun = "rifle_block";

        readonly WeaponRack _rack;
        UniTask<Rigidbody>? _testGun;

        /// <summary>The test gun: dropped on the deck just ahead of where you start (again after a rebuild).</summary>
        void PlaceWeapon(Vector3 spawn, float yaw) => PlaceWeaponAsync(spawn, yaw).Forget();

        async UniTaskVoid PlaceWeaponAsync(Vector3 spawn, float yaw)
        {
            // Its own copy (WeaponRack.Loose), not one of the range's (those lie on its bench) nor the equipped gun's.
            _testGun ??= WeaponRack.Share(_rack.Loose(TestGun, _cts.Token));   // a rebuild can ask again while it loads
            Rigidbody loaded;
            try { loaded = await _testGun.Value; }
            catch (OperationCanceledException) { return; }
            if (loaded == null) return;
            var gun = loaded.transform;
            var facing = Quaternion.Euler(0f, yaw, 0f);
            var at = spawn + facing * new Vector3(0.25f, 0.35f, 1.0f);
            var turned = facing * Quaternion.Euler(0f, 80f, 0f);
            gun.transform.SetPositionAndRotation(at, turned);
            gun.gameObject.SetActive(true);
            if (gun.TryGetComponent<Rigidbody>(out var body))
            {
                // Move the body too: setting only the transform, the next physics step puts it back where the body
                // was (the rifle stayed by the first spawn after the station was rebuilt from your lucky number).
                body.position = at;
                body.rotation = turned;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Dev (the page's inspector): stand 1.6 m from the first prop of piece `piece` (e.g. "cryo_pod"), on the side
        /// of its room's middle, facing it. For looking at things up close quickly.
        /// </summary>
        void WarpTo(string piece)
        {
            if (_layout == null || string.IsNullOrEmpty(piece) || piece.StartsWith("range")) return;   // range: RangeRoom
            var prop = _layout.Props.Find(p => p.Piece == piece);
            if (prop == null) { Debug.Log($"[Station] dev warp: no prop '{piece}'"); return; }
            var at = _station.Plan.PlanToWorld(prop.Col, prop.Row);
            // Towards the nearest room's middle: that's the open side of something against a wall.
            var inside = at + Vector3.forward;
            var best = float.MaxValue;
            foreach (var r in _layout.Rooms)
            {
                var c = _station.Plan.PlanToWorld(r.Col, r.Row);
                var d = Vector3.Distance(c, at);
                if (d < best) { best = d; inside = c; }
            }
            var away = Vector3.ProjectOnPlane(inside - at, Vector3.up);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            var stand = at + away.normalized * 1.6f;
            stand.y = _player.Body.transform.position.y;
            var face = at - stand;
            Teleport(stand, Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg);
            Debug.Log($"[Station] dev warp to {piece}");
        }

        void Teleport(Vector3 position, float yaw, float pitch = 0f)
        {
            var body = _player.Body;
            body.enabled = false;   // a CharacterController ignores transform moves while enabled
            body.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, pitch);
        }

        void DockShip()
        {
            var dock = _layout.Dock;
            if (dock == null || dock.Length <= 0f) return;
            _dockPosition = _station.Plan.PlanToWorld(dock.Col, dock.Row);
            _ship.Show(PlayerShipSkin.Current(_playerShip), dock.Length);
            // Resting on its belly a little above the deck, nose along the dock's heading.
            _ship.Pose(_dockPosition + Vector3.up * (dock.Length * 0.16f), Quaternion.Euler(0f, dock.Yaw, 0f));
            _ship.EnginesOff(instant: true);   // docked: engines cold
            _ship.GearDown(dock.Length * 0.16f, instant: true);   // standing on its legs on the deck
            _hull = _ship.Hull;                 // the hull only, not its (unlit) flames
            if (_hull.size == Vector3.zero) _hull = new Bounds(_dockPosition, Vector3.one * dock.Length);
        }

        /// <summary>Wait for the universe, then park its camera in orbit around the station's body.</summary>
        async UniTaskVoid PlaceInSpace(CancellationToken ct)
        {
            while (_space.Worlds.Count == 0) await UniTask.Yield(ct);
            var worlds = _space.Worlds;
            var target = string.IsNullOrEmpty(_config.Orbit) ? SpacePresenter.CrashSite(worlds, _space.Seed) : _config.Orbit;
            _orbitBody = target;
            float3 at = default;
            var diameter = 1f;
            while (!_space.TryGetBody(target, out at, out diameter)) await UniTask.Yield(ct);
            _space.TakeCamera();

            // Names for the call: the planet the station orbits, the freighter that's going down (cargo manifest).
            var index = 0;
            for (var i = 0; i < worlds.Count; i++) if (worlds[i] == target) index = i;
            _planet = BodyNames.Planet(_space.Seed, index);
            if (Generated && (_layoutSeed == 0 || _layoutSeed != _space.Seed))
            {
                // Your station, from your lucky number (Start had no roll): built while the screen is still black.
                Build(LayoutGenerator.Generate(_config, _layoutSeed = _space.Seed));
                SetLights(_state.Lights.Value);
                Teleport(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);
                PlaceWeapon(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);   // the new spawn's
                DockShip();
                PlaceOnArrival();
            }
            // The freighter of the job you're on (or were on, coming back): its site's own cargo manifest.
            var manifest = LuckyNumbers.Cargo.Manifest.Generate(GameContent.Cargo, Jobs.SiteSeed(_space.Seed, _job.Seed.Value));
            _freighter = manifest.Ship;
            _museum = manifest.Museum;

            // Sunward side (lit), a little above the orbit plane; the station's north faces the body.
            var toSun = _space.TryGetBody("sun", out var sun, out _) ? math.normalizesafe(sun - at, new float3(1, 0, 0)) : new float3(1, 0, 0);
            var side = math.normalizesafe(math.cross(new float3(0, 1, 0), toSun), new float3(0, 0, 1));
            var away = math.normalize(toSun * 0.6f + side * 0.75f + new float3(0, 0.2f, 0));
            _orbitPosition = (Vector3)(at + away * diameter * math.max(0.6f, _config.OrbitDiameters));
            _stationInSpace = Quaternion.LookRotation((Vector3)(at - (float3)_orbitPosition), Vector3.up);
            _space.Camera.nearClipPlane = math.max(0.001f, diameter * 0.01f);
            _inSpace = true;
            if (_arrival == StationArrival.Planet) Homecoming();
            Debug.Log($"[Station] ready: orbiting {target} \"{_planet}\" (seed {_space.Seed})"); // marker for ln.py webshot
        }

        public void PostLateTick()
        {
            _range.Tick();
            var dt = Time.deltaTime;
            var eyes = _player.Camera;
            if (_inSpace)
            {
                // The universe seen from the station's point in orbit, turned as the player's head turns.
                var cam = _space.Camera;
                cam.transform.SetPositionAndRotation(_orbitPosition, _stationInSpace * eyes.transform.rotation);
                cam.fieldOfView = eyes.fieldOfView;
                if (!Leaving) _fade = Mathf.MoveTowards(_fade, 0f, dt / 0.8f);
            }
            if (Leaving) _fade = Mathf.MoveTowards(_fade, 1f, dt / 0.6f);
            _ui.Fade(_fade);

            var position = _player.Body.transform.position;

            // The map: M toggles it, Esc closes it; it follows you (plan coordinates, compass heading).
            var press = _press;
            _press = Press.None;
            if ((press & Press.CloseMap) != 0 && _outfitter.Open.Value) { CloseOutfitter(); press &= ~Press.CloseMap; }   // Esc: the outfitter first
            if ((press & Press.CloseMap) != 0 && _board.Open.Value) { CloseBoard(); press &= ~Press.CloseMap; }   // then the board
            if ((press & Press.ToggleMap) != 0) _state.MapOpen.Value = !_state.MapOpen.Value;
            else if ((press & Press.CloseMap) != 0 && _state.MapOpen.Value) _state.MapOpen.Value = false;
            var plan = _station.Plan;
            var col = position.x / StationPlan.Cell - 0.5f;
            var row = plan.Depth - 1 - (position.z / StationPlan.Cell - 0.5f);
            var yaw = eyes.transform.eulerAngles.y;
            if (Mathf.Abs(col - _state.PlayerCol.Value) > 0.05f || Mathf.Abs(row - _state.PlayerRow.Value) > 0.05f)
            {
                _state.PlayerCol.Value = col;
                _state.PlayerRow.Value = row;
            }
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, _state.PlayerYaw.Value)) > 3f) _state.PlayerYaw.Value = yaw;

            _station.UpdateDoors(position, _config.DoorOpenDistance > 0f ? _config.DoorOpenDistance : 2.6f, dt);

            // Room you're in, and the call it may set off.
            var cell = StationPlan.CellOf(position);
            var room = _roomOf.TryGetValue(cell, out var r) ? r : "";
            if (_state.Room.Value != room) _state.Room.Value = room;
            var call = _config.Call;
            if (_state.Call.Value == CallPhase.None && room != "" && room == _layout.CallRoom)
            {
                // The first time: the mayday. Later, without a job: Dispatch with the board.
                if (_voyage.Trips.Value == 0 && !_job.Active.Value && call != null) Ring(CallKind.Dispatch);
                else if (NeedsJob && _inSpace) Ring(CallKind.Board);
            }
            TickCall(dt);

            // What E would do here: answer a ringing call, use the nearest console, or board the ship.
            var prompt = "";
            StationSwitch console = null;
            if (_state.Call.Value == CallPhase.Ringing) prompt = Fill(_ringKind == CallKind.Board ? J.RingPrompt : call?.RingPrompt);
            else
            {
                var best = UseDistance;
                foreach (var sw in _layout.Switches)
                {
                    if (sw.Action == StationAction.Answer) continue;   // only while ringing
                    var at = _station.Plan.PlanToWorld(sw.Col, sw.Row);
                    var d = Vector2.Distance(new Vector2(at.x, at.z), new Vector2(position.x, position.z));
                    if (d < best) { best = d; console = sw; }
                }
                if (console != null) prompt = console.Prompt;
                else if (NearShip(position)) prompt = NeedsJob ? (_board.Open.Value ? "" : J.NoJobPrompt) : _layout.Dock.BoardPrompt;
            }
            if (_board.Open.Value && !NearShip(position)) CloseBoard();   // walked away from the ship
            if (_outfitter.Open.Value && Vector2.Distance(new Vector2(_outfitterAt.x, _outfitterAt.z), new Vector2(position.x, position.z)) > UseDistance * 1.6f)
                CloseOutfitter();   // walked away from its console
            if (_state.Prompt.Value != prompt) _state.Prompt.Value = prompt;
            _firstPerson.InteractElsewhere = prompt != "";   // E answers / uses / boards: the gun stays put

            if ((press & Press.Interact) != 0 && !Leaving)
            {
                if (_state.Call.Value == CallPhase.Ringing) Answer();
                else if (console != null) Use(console);
                else if (NearShip(position) && NeedsJob && !_board.Open.Value) OpenBoard();   // pick the job here
                else if (NearShip(position) && !NeedsJob) Board();
            }

            // Hint line: the E prompt, else the objective, else the controls (fading out).
            _hintAlpha = Mathf.MoveTowards(_hintAlpha, 0f, dt / 8f);
            if (prompt != "") _ui.Hint(prompt, 1f);
            else if (_state.Objective.Value != "") _ui.Hint(_state.Objective.Value, 0.8f);
            else _ui.Hint(_config.Hint, Mathf.Clamp01(_hintAlpha * 2f));
        }

        bool NearShip(Vector3 position)
        {
            var dock = _layout.Dock;
            if (dock == null || dock.Length <= 0f) return false;
            // From the hull (its bounds, on the deck plane), not the middle: a 9 m ship can be boarded from any side.
            var near = _hull.ClosestPoint(new Vector3(position.x, _hull.center.y, position.z));
            var d = Vector2.Distance(new Vector2(near.x, near.z), new Vector2(position.x, position.z));
            return d < Mathf.Max(1f, dock.BoardDistance);
        }

        void Use(StationSwitch console)
        {
            switch (console.Action)
            {
                case StationAction.Outfitter:
                    if (_outfitter.Open.Value) CloseOutfitter();
                    else OpenOutfitter(_station.Plan.PlanToWorld(console.Col, console.Row));
                    break;
                case StationAction.Lights:
                    var next = _state.Lights.Value switch
                    {
                        StationLightMode.On => StationLightMode.Off,
                        StationLightMode.Off => StationLightMode.Emergency,
                        _ => StationLightMode.On,
                    };
                    SetLights(next);
                    break;
                case StationAction.Psx:
                    SetPsx(!_state.Psx.Value);
                    break;
                case StationAction.Answer:
                    Answer();
                    break;
                case StationAction.FlightSchool:
                    ToFlightSchool();
                    break;
                case StationAction.Undock:
                    Undock();
                    break;
            }
        }

        void Ring(CallKind kind)
        {
            _ringKind = kind;
            _caller = kind == CallKind.Board ? "Dispatch" : _config.Call?.Caller ?? "Dispatch";
            _state.Caller.Value = _caller;
            _state.Call.Value = CallPhase.Ringing;
            Debug.Log($"[Station] call from {_caller} ({kind})");
        }

        void Answer()
        {
            if (_state.Call.Value != CallPhase.Ringing) return;
            if (_ringKind == CallKind.Board) StartCall(J.BoardLines, J.CharsPerSecond, J.BoardObjective, CallKind.Board);
            else StartCall(_config.Call.Lines, _config.Call.CharsPerSecond, _config.Call.Objective, CallKind.Dispatch);
        }

        void StartCall(IEnumerable<StationCallLine> lines, float cps, string objective, CallKind kind)
        {
            _activeKind = kind;
            _callLines = new List<StationCallLine>(lines);
            _callCps = cps > 0f ? cps : 36f;
            _callObjective = objective;
            _state.Call.Value = CallPhase.Active;
            _line = 0;
            _lineTime = 0f;
        }

        /// <summary>
        /// Back from the planet: the museum buys what you carried back (each relic's value in credits, XP each) and
        /// calls at once to say so. Dispatch's call doesn't ring (the call phase is past None).
        /// </summary>
        void Homecoming()
        {
            var home = _config.Homecoming;
            if (home == null) return;
            var sold = _pack.TakeRelics();
            var total = 0;
            var names = new List<string>();
            foreach (var (name, value) in sold)
            {
                total += Mathf.Max(0, value);
                names.Add(name);
                if (home.XpPerRelic > 0) _xp.Publish(new CharacterXpGained(home.XpPerRelic, name));
            }
            Jobs.Remember(_job.RecentTargets, names);   // the next board won't send you after these again straight away
            _relicCount = sold.Count;
            _relics = names.Count switch
            {
                0 => "",
                1 => names[0],
                _ => string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[^1],
            };
            var mult = ShipUpgrades.Payout(_playerShip);   // the broker's licence, a gold hull...
            if (mult != 1f) total = Mathf.RoundToInt(total * mult);
            _payout = total.ToString("N0");
            _voyage.Credits.Value += total;
            _voyage.LastPayout.Value = total;
            _caller = Fill(home.Caller);
            _state.Caller.Value = _caller;
            Debug.Log($"[Station] back from the planet: {sold.Count} relic(s) sold for {total} cr ({_voyage.Credits.Value} cr total)");
            var lines = new List<StationCallLine>(sold.Count > 0 ? home.PaidLines : home.EmptyLines);
            if (_voyage.Rescued.Value)
            {
                // The rescue drone brought you back (content/combat.txtpb rescue): Dispatch, not the museum.
                _voyage.Rescued.Value = false;
                lines = new List<StationCallLine>(GameContent.Combat?.Rescue?.Lines ?? new List<StationCallLine>());
                _caller = "Dispatch";
                _state.Caller.Value = _caller;
                Debug.Log("[Station] back by rescue drone");
            }
            if (_job.Active.Value)
            {
                // The job: done if its goal was met (Clear counts kills on the planet), paid on top of the relics.
                var done = _job.Done.Value || _job.Kind.Value switch
                {
                    JobKind.Salvage => sold.Count > 0,
                    JobKind.Retrieve => names.Contains(_job.Target.Value),
                    _ => false,
                };
                if (done)
                {
                    _job.Completed.Value++;
                    if (_job.Pay.Value > 0)
                    {
                        if (mult != 1f) _job.Pay.Value = Mathf.RoundToInt(_job.Pay.Value * mult);   // and the lines say so
                        _voyage.Credits.Value += _job.Pay.Value;
                        _voyage.LastPayout.Value += _job.Pay.Value;
                        lines.AddRange(J.DoneLines);
                    }
                }
                else lines.AddRange(J.FailedLines);
                Debug.Log($"[Station] job \"{_job.Title.Value}\" {(done ? $"done: +{_job.Pay.Value} cr" : "not done")} ({_job.Completed.Value} completed)");
                _job.Active.Value = false;
            }
            StartCall(lines, home.CharsPerSecond, home.Objective, CallKind.Homecoming);
            foreach (var line in _callLines) Debug.Log($"[Station] {Fill(line.Speaker)}: {Fill(line.Text)}");
        }

        /// <summary>To the Flight School's training deck (fade out); it sends you back here, beside the console.</summary>
        void ToFlightSchool()
        {
            _next = GameScene.FlightSchool;
            _firstPerson.Lock(ControlLock.Leaving);
            _state.Prompt.Value = "";
            Debug.Log("[Station] to the Flight School");
            Leave(_cts.Token).Forget();
        }

        /// <summary>Launch: out of the bay into space round the station (GameScene.Orbit); docking brings you back beside the ship.</summary>
        void Undock()
        {
            _next = GameScene.Orbit;
            _firstPerson.Lock(ControlLock.Leaving);
            _state.Prompt.Value = "";
            Launch();
            Debug.Log("[Station] launch: out of the bay");
            Leave(_cts.Token).Forget();
        }

        /// <summary>Beside the first console that does `action` (on an open cell next to it), facing it.</summary>
        void TeleportBesideConsole(StationAction action)
        {
            var sw = _layout.Switches.Find(x => x.Action == action);
            if (sw == null) return;
            var at = _station.Plan.PlanToWorld(sw.Col, sw.Row);
            var steps = new[] { Vector3.back, Vector3.forward, Vector3.left, Vector3.right };
            foreach (var step in steps)
            {
                var spot = at + step * 1.3f;
                if (!StationPlan.Walkable(_station.Plan.At(StationPlan.CellOf(spot)))) continue;
                Teleport(new Vector3(spot.x, 0f, spot.z), Quaternion.LookRotation(-step, Vector3.up).eulerAngles.y);
                Debug.Log($"[Station] beside the {action} console ({sw.Col:0.#}, {sw.Row:0.#})");
                return;
            }
        }

        /// <summary>Off the ship: on the deck beside it (the first open side of its hull), facing it.</summary>
        /// <summary>
        /// Stand where you came in: by the Flight School console, beside your ship (or there for ?ln-board), or the
        /// spawn (left as is).
        /// </summary>
        void PlaceOnArrival()
        {
            if (_arrival == StationArrival.FlightSchool) TeleportBesideConsole(StationAction.FlightSchool);
            else if (_arrival != StationArrival.None || DevBoard) TeleportBesideShip();
        }

        void TeleportBesideShip()
        {
            var c = _hull.center;
            var e = _hull.extents;
            var sides = new[] { Vector3.right * (e.x + 1.4f), Vector3.left * (e.x + 1.4f), Vector3.back * (e.z + 1.4f), Vector3.forward * (e.z + 1.4f) };
            foreach (var side in sides)
            {
                var at = new Vector3(c.x + side.x, 0f, c.z + side.z);
                if (!StationPlan.Walkable(_station.Plan.At(StationPlan.CellOf(at)))) continue;
                var face = Quaternion.LookRotation(-side.normalized, Vector3.up).eulerAngles.y;
                Teleport(at, face);
                return;
            }
        }

        void TickCall(float dt)
        {
            if (_callLines == null || _state.Call.Value != CallPhase.Active) return;
            if (_line >= _callLines.Count)
            {
                _ui.Dialogue(null, null, 0);
                _state.Call.Value = CallPhase.Ended;
                _state.Objective.Value = Fill(_callObjective);
                CallEnded(_activeKind);
                return;
            }
            var line = _callLines[_line];
            var text = Fill(line.Text);
            _lineTime += dt;
            var cps = _callCps;
            _ui.Dialogue(Fill(line.Speaker), text, Mathf.FloorToInt(_lineTime * cps));
            var hold = Mathf.Max(line.Seconds, text.Length / cps + 1.2f);
            if (_lineTime >= hold)
            {
                _line++;
                _lineTime = 0f;
            }
        }

        // {planet} is the job's (if you've had one), else the world the station orbits; then the job's own fields.
        string Fill(string s) => Jobs.Fill((s ?? "").Replace("{caller}", _caller)
            .Replace("{planet}", string.IsNullOrEmpty(_job.Planet.Value) ? _planet : _job.Planet.Value)
            .Replace("{ship}", _freighter).Replace("{museum}", _museum)
            .Replace("{relics}", _relics).Replace("{count}", _relicCount.ToString()).Replace("{credits}", _payout), _job);

        void CallEnded(CallKind kind)
        {
            switch (kind)
            {
                case CallKind.Dispatch: TakeFirstJob(); break;
                case CallKind.Homecoming: if (NeedsJob) Ring(CallKind.Board); break;   // Dispatch, with the board
                case CallKind.Board: SendOffers(); break;   // to your ship: picked when you board
            }
        }

        /// <summary>The mayday is your first job: salvage on the planet the station orbits (the intro's crash site).</summary>
        void TakeFirstJob()
        {
            if (_job.Active.Value) return;
            _job.Kind.Value = JobKind.Salvage;
            _job.Title.Value = J.FirstTitle;
            _job.Objective.Value = J.FirstObjective;
            _job.Client.Value = _museum;
            _job.Body.Value = _orbitBody;
            _job.Planet.Value = _planet;
            _job.Danger.Value = 1;
            _job.Pay.Value = J.FirstPay;
            _job.Seed.Value = 0;
            _job.Target.Value = "";
            _job.Count.Value = _job.Goal.Value = 0;
            _job.Done.Value = false;
            _job.Active.Value = true;
            Debug.Log($"[Station] job: {_job.Title.Value} on {_job.Planet.Value}");
        }

        /// <summary>Dispatch's offers for this visit (seeded by the trip), rolled once: the call and the ship share them.</summary>
        void SendOffers()
        {
            if (_offers.Count > 0) return;
            _offers = Jobs.Roll(J, GameContent.Cargo, _space.Seed, _voyage.Trips.Value, _space.Worlds, _job.RecentTargets);
            _board.Offers.Clear();
            foreach (var o in _offers) _board.Offers.Add(Jobs.Encode(o));
            if (NeedsJob) _state.Objective.Value = Fill(J.BoardObjective);
            foreach (var o in _offers) Debug.Log($"[Station] offer: {o.Title} ({o.Type.Kind}, {o.Planet}, danger {o.Danger}, {o.Pay} cr)");
        }

        /// <summary>At the ship: the board, to pick the job you're leaving on.</summary>
        void OpenBoard()
        {
            SendOffers();
            _board.Open.Value = _offers.Count > 0;
            if (_board.Open.Value) Debug.Log("[Station] job board open at the ship");
        }

        void OpenOutfitter(Vector3 at)
        {
            if (_board.Open.Value) CloseBoard();
            _outfitterAt = at;
            _outfitter.Open.Value = true;
            Debug.Log("[Station] outfitter open");
        }

        async UniTaskVoid DevOpenOutfitter()
        {
            TeleportBesideConsole(StationAction.Outfitter);
            await UniTask.Delay(TimeSpan.FromSeconds(1.5), cancellationToken: _cts.Token);   // the page is up, then pay
            if (_voyage.Credits.Value == 0) _voyage.Credits.Value = 12500;
            var sw = _layout.Switches.Find(x => x.Action == StationAction.Outfitter);
            if (sw != null) OpenOutfitter(_station.Plan.PlanToWorld(sw.Col, sw.Row));
            Debug.Log($"[Station] dev: ?ln-outfitter, {_voyage.Credits.Value} cr");   // marker for ln.py webshot
        }

        void CloseOutfitter()
        {
            _outfitter.Open.Value = false;
            Debug.Log("[Station] outfitter closed");
        }

        void CloseBoard()
        {
            _board.Open.Value = false;
            Debug.Log("[Station] job board closed");
        }

        /// <summary>A job off the board: take it and leave on it.</summary>
        void Accept(int index)
        {
            if (!_board.Open.Value || index < 0 || index >= _offers.Count || Leaving) return;
            Jobs.Take(_job, _offers[index]);
            _board.Open.Value = false;
            _state.Objective.Value = Fill(J.TakenObjective);
            Debug.Log($"[Station] took job: {_job.Title.Value} on {_job.Planet.Value} ({_job.Body.Value}), {_job.Pay.Value} cr");
            Board();
        }

        void Board()
        {
            if (_voyage.Trips.Value == 0) TakeFirstJob();   // boarded without taking the call: it's still the mayday
            // Where the job is: the Planet scene builds that body's site (the intro sets it itself on the first trip).
            if (!string.IsNullOrEmpty(_job.Body.Value))
            {
                _landing.Body.Value = _job.Body.Value;
                _landing.Valid.Value = true;
            }
            _next = NextScene;
            _voyage.Trips.Value++;
            Launch();
            Debug.Log($"[Station] refuelled: {_playerShip.Fuel.Value:0} units");
            _firstPerson.Lock(ControlLock.Leaving);
            _state.Prompt.Value = "";
            Leave(_cts.Token).Forget();
        }

        /// <summary>Out of the bay: the dice drive rolls again (if you have one), and the station fills the tank.</summary>
        void Launch()
        {
            _outfitter.Open.Value = false;
            if (ShipUpgrades.HasDiceDrive(_playerShip))
            {
                ShipUpgrades.Reroll(_playerShip);
                var s = PlayerShipStats.Of(_playerShip);
                Debug.Log($"[Station] dice drive rolled {_playerShip.Dice.Value}: speed x{s.Speed:0.00}, agility x{s.Agility:0.00}, " +
                          $"fuel x{s.Fuel:0.00}, efficiency x{s.Efficiency:0.00}, toughness x{s.Toughness:0.00}, firepower x{s.Firepower:0.00}");
            }
            PlayerShipStats.Refuel(_playerShip);   // docked: the station fills the tank
        }

        async UniTaskVoid Leave(CancellationToken ct)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.6), cancellationToken: ct);
            Debug.Log($"[Station] to {_next} ({_job.Title.Value})");
            _scenes.Publish(new SceneChangeRequested(_next));
        }

        /// <summary>The page's layout editor: rebuild from a new plan; back on the old one if it fails.</summary>
        void Rebuild(string planText)
        {
            if (string.IsNullOrWhiteSpace(planText)) return;
            var rows = new List<string>(planText.Replace("\r", "").Split('\n'));
            rows.RemoveAll(string.IsNullOrEmpty);
            var previous = _layout;
            try
            {
                Build(_layout.WithPlan(rows));
                SetLights(_state.Lights.Value);
                var here = StationPlan.CellOf(_player.Body.transform.position);
                if (!StationPlan.Walkable(_station.Plan.At(here))) Teleport(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);
                Debug.Log($"[Station] rebuilt from the page: {rows.Count} rows");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Station] plan rejected ({e.Message}); keeping the old one");
                Build(previous);
            }
        }

        void SetLights(StationLightMode mode)
        {
            if (mode == StationLightMode.Unspecified) mode = StationLightMode.On;
            _station.SetLights(mode);
            _state.Lights.Value = mode;
        }

        void SetPsx(bool on)
        {
            Shader.SetGlobalFloat(Psx, on ? 1f : 0f);
            _state.Psx.Value = on;
        }

        public void Dispose()
        {
            _range.Dispose();
            Shader.SetGlobalFloat(Psx, 0f);
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
        }
    }
}
