using System.Collections.Generic;
using System.Diagnostics;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using LuckyNumbers.Rendering;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A Defend job (JobKind.Defend, content/combat.txtpb defend): the on-foot twin of the satellite uplink. A beacon
    /// (a placeholder pillar, PLACEHOLDER_defend_beacon) stands just clear of the wreck, on the landing pad's side; walk
    /// up and E plants it, and it charges, one wave at a time, while a horde comes for it.
    ///
    /// The wave director: each wave adds its budget (DefendWaves.Budget) to a queue, and the queue comes out one
    /// monster every spawn_interval in a ring spawn_radius out round the beacon (just past their sight), never more than
    /// max_alive on the ground at once (max_alive_low_end on the low-end tier): the rest wait their turn. The mix is
    /// weighted by wave and danger (DefendWaves.Pick): Rattleframes and Aberrations rush, Plague Arcanists keep their
    /// band and throw. A wave is charge_seconds of charging, then rest_seconds' breather (the queue keeps coming), then
    /// the next. Every wave charged: the job's done (paid back at the station), `xp`, and the horde scatters. The beacon
    /// at 0 hp: the job's failed (PlayerJob.failed: no pay), and they scatter all the same.
    ///
    /// Horde monsters spawn straight into Chase (MonsterBrain.Horde) and go for whichever is nearer, you or the
    /// beacon (MonsterBrain.TargetBeacon); the brain senses that one, so a thrower's band is round its own target. They
    /// bite the beacon (d20 + attack against beacon_armor) and a flask that bursts within its splash of it rolls against
    /// it too. None of it goes to the roll feed (it'd be nothing else): the HUD shows the beacon's hp, its charge and
    /// the wave (DefendState). A dead horde monster goes back to the spares for the next to come up (no new GameObject
    /// per spawn), and every monster is kept apart from the rest by <see cref="MonsterSeparation"/>.
    ///
    /// Dev (the page's inspector): "defend" plants a beacon at the wreck now (no Defend job needed), "defend:wave:N"
    /// skips to wave N, "defend:stand:N" stands you N m off it, "defend:heal" fills its hp, "defend:watch" has the horde
    /// leave you be (to watch or measure it), "defend:win" / "defend:lose" end it.
    /// </summary>
    public sealed partial class CombatPresenter : IInteractable
    {
        enum DefendPhase { None, Ready, Charging, Rest, Won, Lost }

        /// <summary>Space kept round each monster's body in the crowd (m).</summary>
        const float Spacing = 0.15f;
        /// <summary>The fastest the crowd shoves a monster aside (m/s): faster than any walks, so the ones behind can't squeeze into the front.</summary>
        const float ShoveSpeed = 8f;

        readonly DefendState _defend;
        readonly Dictionary<MonsterType, Stack<Monster>> _spare = new();
        MonsterSeparation _separation;
        DefendPhase _phase;
        Transform _beacon, _beaconCore;
        Renderer _coreRenderer;
        MaterialPropertyBlock _coreBlock;
        Vector3 _beaconAt;            // its foot, on the ground
        float _beaconRadius, _beaconHeight;
        int _beaconHp, _beaconMax;
        int _wave, _waves, _defendDanger;
        float _charge, _restLeft, _spawnClock, _beaconFlash;
        int _waiting, _hordeAlive, _hordeKilled, _waveKilled, _hordeSpawned;
        bool _devWatch;   // dev "defend:watch": the horde leaves you be (only the beacon), to watch or measure it
        // ?ln-perf: the separation's cost, summed between reports.
        long _sepTicks, _sepMaxTicks;
        int _sepFrames, _peakHorde;
        float _perfAt;

        static DefendConfig D => C?.Defend;
        bool OnDefendJob => _job.Active.Value && _job.Kind.Value == JobKind.Defend;
        bool Holding => _phase is DefendPhase.Charging or DefendPhase.Rest;
        Vector3 BeaconMiddle => _beaconAt + Vector3.up * (_beaconHeight * 0.55f);

        static bool? _perf;
        static bool Perf => _perf ??= Application.absoluteURL.Contains("ln-perf");

        void StartDefend()
        {
            _defend.Phase.Value = "";
            _defend.Wave.Value = 0;
            _defend.Waves.Value = 0;
            _defend.Charge.Value = 0f;
            _defend.Rest.Value = 0;
            _defend.Hp.Value = 0;
            _defend.HpMax.Value = 0;
            _defend.Horde.Value = 0;
            _defend.Waiting.Value = 0;
        }

        // ---- The beacon -----------------------------------------------------------------------------------------

        /// <summary>
        /// The beacon, standing ready (unplanted): beacon_offset m clear of the wreck's hull toward the landing pad (a
        /// little round from the relics' line), or ahead of you without a wreck.
        /// </summary>
        void PlaceBeacon()
        {
            if (_beacon != null || D == null) return;
            EnsureBolts();
            var body = _player.Body.transform;
            Vector3 at;
            if (_view.Wreck != null)
            {
                var hull = _view.Wreck.Hull;
                var centre = hull.center;
                var pad = _view.LandingPad != null ? _view.LandingPad.position : Vector3.zero;
                var toward = Vector3.ProjectOnPlane(pad - centre, Vector3.up);
                if (toward.sqrMagnitude < 1f) toward = Vector3.forward;
                var dir = Quaternion.Euler(0f, -30f, 0f) * toward.normalized;
                // Out of the hull's box along `dir`, then the offset.
                var exit = math.min(math.abs(dir.x) > 1e-3f ? hull.extents.x / math.abs(dir.x) : float.MaxValue,
                                    math.abs(dir.z) > 1e-3f ? hull.extents.z / math.abs(dir.z) : float.MaxValue);
                at = centre + dir * (exit + math.max(1f, D.BeaconOffset));
            }
            else at = body.position + Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized * 6f;
            at.y = _ground.SurfaceAt(at);
            BuildBeacon(at);
            _phase = DefendPhase.Ready;
            _defend.Phase.Value = "ready";
            Debug.Log($"[Defend] beacon ready by the wreck at {at} (E within {D.PlantDistance:0.0} m plants it)");
        }

        /// <summary>A dark pillar with a block of light on top (charging: it brightens; hit: it flashes), and a collider: you can't walk through it.</summary>
        void BuildBeacon(Vector3 at)
        {
            var size = new Vector3(D.BeaconSize.X > 0f ? D.BeaconSize.X : 0.8f, D.BeaconSize.Y > 0f ? D.BeaconSize.Y : 2.6f,
                                   D.BeaconSize.Z > 0f ? D.BeaconSize.Z : 0.8f);
            _beaconAt = at;
            _beaconRadius = math.max(size.x, size.z) * 0.5f;
            _beaconHeight = size.y;
            var root = new GameObject("PLACEHOLDER_defend_beacon").transform;
            root.position = at;
            var box = root.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, size.y * 0.5f, 0f);
            box.size = size;
            var pillar = MonsterView.Block("PLACEHOLDER_beacon_pillar", size, new Color(0.32f, 0.34f, 0.4f), _bodyMaterial);
            pillar.SetParent(root, false);
            pillar.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            var core = MonsterView.Block("PLACEHOLDER_beacon_core", new Vector3(size.x * 1.3f, 0.45f, size.z * 1.3f), Color.white, _bodyMaterial);
            core.SetParent(root, false);
            core.localPosition = new Vector3(0f, size.y + 0.25f, 0f);
            _beacon = root;
            _beaconCore = core;
            _coreRenderer = core.GetComponent<Renderer>();
            _coreBlock = new MaterialPropertyBlock();
            Glow(0f);
        }

        /// <summary>The core's light: dim cyan standing ready, brightening to white-hot with the charge; red a moment when hit; dark once lost.</summary>
        void Glow(float dt)
        {
            if (_coreRenderer == null) return;
            _beaconFlash = math.max(0f, _beaconFlash - dt);
            Color c;
            switch (_phase)
            {
                case DefendPhase.Lost: c = new Color(0.08f, 0.06f, 0.06f); break;
                case DefendPhase.Won: c = new Color(1.2f, 3.2f, 2.4f) * (1f + 0.15f * math.sin(Time.time * 3f)); break;
                case DefendPhase.Ready: c = new Color(0.15f, 0.5f, 0.6f) * (0.8f + 0.2f * math.sin(Time.time * 2f)); break;
                default:
                    var t = math.saturate((_wave - 1 + _charge) / math.max(1, _waves));
                    c = Color.Lerp(new Color(0.3f, 1.2f, 1.6f), new Color(2.4f, 3.2f, 3.6f), t) * (0.85f + 0.15f * math.sin(Time.time * 9f));
                    break;
            }
            if (_beaconFlash > 0f) c = Color.Lerp(c, new Color(3f, 0.35f, 0.2f), _beaconFlash / 0.2f);
            MonsterView.Paint(_coreRenderer, _coreBlock, c);
            if (Holding) _beaconCore.localRotation = Quaternion.Euler(0f, Time.time * 90f, 0f);
        }

        /// <summary>Planted: wave 1 starts charging.</summary>
        void Plant()
        {
            if (_beacon == null || _phase != DefendPhase.Ready) return;
            _defendDanger = _job.Active.Value ? math.max(1, _job.Danger.Value) : 3;
            _waves = DefendWaves.Waves(D, OnDefendJob ? _job.Goal.Value : 0, _defendDanger);
            _beaconMax = _beaconHp = DefendWaves.BeaconHp(D, _defendDanger);
            _waiting = 0;
            _hordeKilled = 0;
            _hordeSpawned = 0;
            _defend.Waves.Value = _waves;
            _defend.HpMax.Value = _beaconMax;
            _defend.Hp.Value = _beaconHp;
            Say(D.PlantedText);
            Debug.Log($"[Defend] planted: {_waves} wave(s), beacon {_beaconMax} hp, armour {D.BeaconArmor}, danger {_defendDanger}, " +
                      $"at most {DefendWaves.MaxAlive(D, LowEnd.Active)} at once{(LowEnd.Active ? " (low-end tier)" : "")}");
            StartWave(1, false);
        }

        void StartWave(int wave, bool say = true)
        {
            _wave = math.clamp(wave, 1, _waves);
            _charge = 0f;
            _waveKilled = 0;
            var budget = DefendWaves.Budget(D, _wave, _defendDanger);
            _waiting += budget;
            _spawnClock = 0f;
            _phase = DefendPhase.Charging;
            _defend.Phase.Value = "charging";
            _defend.Wave.Value = _wave;
            _defend.Charge.Value = 0f;
            _defend.Rest.Value = 0;
            _defend.Waiting.Value = _waiting;
            if (say) Say(D.WaveText);
            Debug.Log($"[Defend] wave {_wave}/{_waves}: {budget} coming ({_waiting} waiting in all), {_hordeAlive} on the ground");
        }

        // ---- A tick ---------------------------------------------------------------------------------------------

        void TickDefend(float dt, Vector3 me, bool onFoot)
        {
            if (_beacon == null) return;
            Glow(dt);
            if (!Holding) return;   // standing ready: E plants it (IInteractable, below)
            SpawnFromQueue(dt);
            if (_phase == DefendPhase.Charging)
            {
                _charge = math.min(1f, _charge + dt / math.max(1f, D.ChargeSeconds));
                var shown = math.floor(_charge * 100f) * 0.01f;   // 1% steps: the page isn't sent a new state every frame
                if (shown != _defend.Charge.Value) _defend.Charge.Value = shown;
                if (_charge >= 1f) WaveCharged();
            }
            else
            {
                _restLeft -= dt;
                var left = (int)math.ceil(math.max(0f, _restLeft));
                if (left != _defend.Rest.Value) _defend.Rest.Value = left;
                if (_restLeft <= 0f) StartWave(_wave + 1);
            }
            if (Perf) PerfReport();
        }

        // ---- IInteractable: standing ready, near it on foot, "E: plant the beacon" -------------------------------

        static readonly object Planting = new();

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = default;
            if (_beacon == null || _phase != DefendPhase.Ready || D == null) return false;
            var distance = Vector2.Distance(new Vector2(feet.x, feet.z), new Vector2(_beaconAt.x, _beaconAt.z)) - _beaconRadius;
            if (distance >= math.max(1f, D.PlantDistance)) return false;
            offer = new Interaction(Planting, InteractionPriority.Job, math.max(0f, distance));
            return true;
        }

        public string Prompt(object key) => D?.PlantPrompt ?? "";

        public void Interact(object key) => Plant();

        void WaveCharged()
        {
            if (_job.Active.Value && _job.Kind.Value == JobKind.Defend) _job.Count.Value = _wave;
            Debug.Log($"[Defend] wave {_wave}/{_waves} charged: {_waveKilled} killed this wave ({_hordeKilled} in all), " +
                      $"{_hordeAlive} still on the ground, {_waiting} waiting; beacon {_beaconHp}/{_beaconMax}");
            if (_wave >= _waves) { Win(); return; }
            _phase = DefendPhase.Rest;
            _restLeft = math.max(0f, D.RestSeconds);
            _defend.Phase.Value = "rest";
            _defend.Rest.Value = (int)math.ceil(_restLeft);
        }

        /// <summary>The queue: one at a time, spawn_interval apart, while there's room under the cap.</summary>
        void SpawnFromQueue(float dt)
        {
            _spawnClock -= dt;
            if (_waiting <= 0 || _spawnClock > 0f) return;
            if (_hordeAlive >= DefendWaves.MaxAlive(D, LowEnd.Active)) return;   // full: it waits for a gap
            _spawnClock = math.max(0.02f, D.SpawnInterval);
            var id = DefendWaves.Pick(D.Mix, _wave, _defendDanger, _rng.NextFloat());
            MonsterType type = null;
            foreach (var t in C.Monsters) if (t.Id == id) { type = t; break; }
            if (type == null) { _waiting = 0; Debug.LogWarning($"[Defend] no monster \"{id}\" for wave {_wave}: the queue's dropped"); return; }
            var angle = _rng.NextFloat(math.PI * 2f);
            var radius = math.max(5f, D.SpawnRadius) + _rng.NextFloat(-1f, 1f) * math.max(0f, D.SpawnJitter);
            SpawnHorde(type, _beaconAt + new Vector3(math.sin(angle), 0f, math.cos(angle)) * radius);
            _waiting--;
            _defend.Waiting.Value = _waiting;
            _peakHorde = math.max(_peakHorde, _hordeAlive);
        }

        /// <summary>A horde monster at `at`: one of its type back from the spares if there is one, else a new one.</summary>
        void SpawnHorde(MonsterType type, Vector3 at)
        {
            _hordeSpawned++;
            if (_spare.TryGetValue(type, out var spares) && spares.Count > 0)
            {
                var m = spares.Pop();
                m.Position = m.Home = at;
                m.Hp = math.max(1, type.Hp);
                m.Brain = MonsterBrain.Horde();
                m.Heading = _rng.NextFloat(360f);
                m.Incoming.Clear();
                m.CastUntil = 0f;
                m.DeathFor = 0f;
                m.Crowd = -1;
                m.View.gameObject.SetActive(true);
                m.View.Revive(Size(m));
                Arm(m, type.Ranged);
                Place(m, 0f);
                _monsters.Add(m);
                Recount();
                return;
            }
            var fresh = Spawn(type, at, true);
            fresh.Horde = true;
            fresh.Brain = MonsterBrain.Horde();
            Arm(fresh, type.Ranged);   // again: its first throw staggered as a fresh brain's
            Recount();
        }

        /// <summary>A horde monster gone (sunk out of sight): put away for the next wave.</summary>
        void Stash(Monster m)
        {
            m.View.gameObject.SetActive(false);
            if (!_spare.TryGetValue(m.Type, out var spares)) _spare[m.Type] = spares = new Stack<Monster>();
            spares.Push(m);
        }

        /// <summary>A horde monster's target this tick (MonsterBrain.TargetBeacon): true for the beacon. Not while you're down.</summary>
        bool AfterBeacon(Monster m, Vector3 me, bool onFoot)
        {
            var toYou = Vector2.Distance(new Vector2(me.x, me.z), new Vector2(m.Position.x, m.Position.z));
            var toBeacon = math.max(0f, Vector2.Distance(new Vector2(_beaconAt.x, _beaconAt.z), new Vector2(m.Position.x, m.Position.z)) - _beaconRadius);
            return MonsterBrain.TargetBeacon(ref m.Brain, toYou, onFoot && !_down && !_devWatch, toBeacon);
        }

        // ---- Hurting the beacon ---------------------------------------------------------------------------------

        /// <summary>A bite at the beacon: d20 + its attack against beacon_armor (not on the roll feed: there'd be nothing else).</summary>
        void BiteBeacon(Monster m)
        {
            m.Brain.Cooldown = math.max(0.3f, m.Type.Cooldown);
            m.View.Attack();
            if (!Holding) return;
            var (_, _, hit, crit, _) = Dice.Attack(m.Type.Attack, math.max(1, D.BeaconArmor), ref _rng);
            if (hit) HurtBeacon(Dice.Roll(m.BiteDice, ref _rng, crit));
        }

        /// <summary>A flask burst: on the beacon, or within its splash of it, it rolls against the beacon as a bite would.</summary>
        void SplashBeacon(Monster m, Vector3 point, Collider what)
        {
            if (_beacon == null || !Holding) return;
            var direct = what != null && what.transform.IsChildOf(_beacon);
            var r = m.Ranged;
            if (!direct && FromBeacon(point) > math.max(0.3f, r.SplashRadius)) return;
            var (_, _, hit, crit, _) = Dice.Attack(r.Attack + (direct ? r.DirectBonus : 0), math.max(1, D.BeaconArmor), ref _rng);
            if (hit) HurtBeacon(Dice.Roll(string.IsNullOrEmpty(r.Damage) ? "1d4" : r.Damage, ref _rng, crit));
        }

        /// <summary>How far `point` is from the beacon's pillar (its upright axis, less its half-width).</summary>
        float FromBeacon(Vector3 point)
        {
            var y = math.clamp(point.y, _beaconAt.y, _beaconAt.y + _beaconHeight);
            return math.max(0f, Vector3.Distance(point, new Vector3(_beaconAt.x, y, _beaconAt.z)) - _beaconRadius);
        }

        void HurtBeacon(int damage)
        {
            if (!Holding) return;
            _beaconHp = math.max(0, _beaconHp - damage);
            _beaconFlash = 0.2f;
            _defend.Hp.Value = _beaconHp;
            if (_beaconHp <= 0) Lose();
        }

        // ---- The end ---------------------------------------------------------------------------------------------

        /// <summary>Every wave charged: the job's done (paid at the station), XP, the horde scatters.</summary>
        void Win()
        {
            _phase = DefendPhase.Won;
            _defend.Phase.Value = "won";
            _defend.Charge.Value = 1f;
            Scatter();
            if (OnDefendJob)
            {
                _job.Count.Value = _waves;
                _job.Done.Value = true;
            }
            if (D.Xp > 0) _xp.Publish(new CharacterXpGained(D.Xp, "Beacon charged"));
            Say(D.WonText);
            Debug.Log($"[Defend] won: all {_waves} wave(s) charged, beacon {_beaconHp}/{_beaconMax}, {_hordeKilled} killed, " +
                      $"{_hordeSpawned} spawned, peak {_peakHorde} on the ground{(OnDefendJob ? ": job done" : "")}");
        }

        /// <summary>The beacon destroyed: the job's failed (no pay), the core goes dark, the horde scatters.</summary>
        void Lose()
        {
            _phase = DefendPhase.Lost;
            _defend.Phase.Value = "lost";
            Scatter();
            if (OnDefendJob)
            {
                _job.Done.Value = false;
                _job.Failed.Value = true;
            }
            if (_beacon != null)
            {
                _beacon.rotation = Quaternion.Euler(14f, _beacon.eulerAngles.y + 20f, -9f);   // knocked askew
                _beaconCore.localRotation = Quaternion.identity;
            }
            Say(D.LostText);
            Debug.Log($"[Defend] lost: the beacon fell on wave {_wave}/{_waves} ({_charge * 100f:0}% charged), {_hordeKilled} killed, " +
                      $"{_hordeSpawned} spawned, peak {_peakHorde} on the ground{(OnDefendJob ? ": job failed" : "")}");
        }

        /// <summary>The horde runs off (and is gone, MonsterBrain.FleeFor later); nothing more comes.</summary>
        void Scatter()
        {
            _waiting = 0;
            _defend.Waiting.Value = 0;
            foreach (var m in _monsters) if (m.Horde && m.Brain.Threat) Cue(m, MonsterCue.Scared);
        }

        void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Replace("{wave}", _wave.ToString()).Replace("{waves}", _waves.ToString());
            DialogueLine.Play(new Dialogue { Speaker = "Beacon", Text = text, DurationSeconds = 3.5f, CharsPerSecond = 45f }, _view.Dialogue, _cts.Token).Forget();
        }

        // ---- The crowd ------------------------------------------------------------------------------------------

        /// <summary>
        /// This frame's separation (MonsterSeparation): every live monster (and the beacon, fixed) in; each one's push
        /// out, for Think to apply (<see cref="Shove"/>). Timed for ?ln-perf.
        /// </summary>
        void Separate(float dt)
        {
            if (_monsters.Count < 2) { foreach (var m in _monsters) m.Crowd = -1; return; }
            var start = Stopwatch.GetTimestamp();
            _separation ??= new MonsterSeparation();
            _separation.Begin(_monsters.Count + 1);
            foreach (var m in _monsters)
            {
                var size = Size(m);
                m.Crowd = m.Dead ? -1 : _separation.Add(new float2(m.Position.x, m.Position.z), math.max(size.x, size.z) * 0.5f + Spacing);
            }
            if (_beacon != null) _separation.Add(new float2(_beaconAt.x, _beaconAt.z), _beaconRadius + Spacing, true);
            _separation.Run();
            var ticks = Stopwatch.GetTimestamp() - start;
            _sepTicks += ticks;
            _sepMaxTicks = math.max(_sepMaxTicks, ticks);
            _sepFrames++;
        }

        /// <summary>Its share of the crowd's push, at most ShoveSpeed m/s.</summary>
        void Shove(Monster m, float dt)
        {
            var push = _separation.Push(m.Crowd);
            var len = math.length(push);
            if (len < 1e-4f) return;
            var most = ShoveSpeed * dt;
            if (len > most) push *= most / len;
            m.Position.x += push.x;
            m.Position.z += push.y;
        }

        /// <summary>?ln-perf: every 5 s while the horde's on, how many and what keeping them apart costs.</summary>
        void PerfReport()
        {
            if (Time.unscaledTime < _perfAt) return;
            _perfAt = Time.unscaledTime + 5f;
            var ms = 1000.0 / Stopwatch.Frequency;
            Debug.Log($"[Horde] wave {_wave}/{_waves} {_defend.Phase.Value}: {_hordeAlive} on the ground (peak {_peakHorde}), {_waiting} waiting, " +
                      $"{_monsters.Count} monsters in all; separation {(_sepFrames > 0 ? _sepTicks * ms / _sepFrames : 0):0.000} ms/frame avg, " +
                      $"{_sepMaxTicks * ms:0.000} max; beacon {_beaconHp}/{_beaconMax}");
            _sepTicks = _sepMaxTicks = 0;
            _sepFrames = 0;
        }

        // ---- Dev ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Dev: "defend": a beacon at the wreck, planted now (no Defend job needed: a dev one at danger 3, or the job's),
        /// and you 7 m off it, facing it; "defend:wave:N": that wave now (planting first if need be); "defend:stand:N":
        /// you N m off it, facing it (70: past the ring they come from, so they all go for the beacon: to watch them);
        /// "defend:heal": the beacon's hp full (a long test); "defend:watch": the horde leaves you be, only the beacon
        /// (again: back to you too); "defend:win" / "defend:lose": the end.
        /// </summary>
        void DevDefend(string to)
        {
            if (D == null || !_spawned) { Debug.LogWarning("[Defend] dev: not yet (on foot at the site first)"); return; }
            var placed = _beacon == null;
            if (placed) PlaceBeacon();
            if (_beacon == null) return;
            const string stand = "defend:stand:";
            if (to.StartsWith(stand) && float.TryParse(to.Substring(stand.Length), System.Globalization.NumberStyles.Float,
                                                      System.Globalization.CultureInfo.InvariantCulture, out var metres)) Stand(metres);
            else if (placed) Stand(7f);
            if (to == "defend:watch")
            {
                _devWatch = !_devWatch;
                Debug.Log($"[Defend] dev: the horde {(_devWatch ? "leaves you be" : "comes for you too")}");
            }
            if (_phase == DefendPhase.Ready) Plant();
            if (to == "defend:win" && Holding) { _wave = _waves; Win(); return; }
            if (to == "defend:lose" && Holding) { HurtBeacon(_beaconHp); return; }
            if (to == "defend:heal" && Holding) { _beaconHp = _beaconMax; _defend.Hp.Value = _beaconHp; Debug.Log("[Defend] dev: beacon healed"); return; }
            const string wave = "defend:wave:";
            if (to.StartsWith(wave) && int.TryParse(to.Substring(wave.Length), out var n) && Holding)
            {
                Debug.Log($"[Defend] dev: to wave {n}");
                StartWave(n);
            }
        }

        /// <summary>Dev: you `metres` off the beacon on your side of it, looking at it (on foot only).</summary>
        void Stand(float metres)
        {
            if (!_firstPerson.Active) return;
            var body = _player.Body;
            var away = Vector3.ProjectOnPlane(body.transform.position - _beaconAt, Vector3.up);
            var at = _beaconAt + (away.sqrMagnitude > 1f ? away.normalized : Vector3.back) * math.max(2f, metres);
            at.y = _ground.SurfaceAt(at) + 0.15f;
            var yaw = Mathf.Atan2(_beaconAt.x - at.x, _beaconAt.z - at.z) * Mathf.Rad2Deg;
            body.enabled = false;
            body.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, math.clamp(80f / math.max(2f, metres), 2f, 10f));
            Debug.Log($"[Defend] dev: you stand {metres:0} m off the beacon");
        }

        void DisposeDefend()
        {
            _separation?.Dispose();
            foreach (var spares in _spare.Values)
                foreach (var m in spares)
                    if (m.View != null) Object.Destroy(m.View.gameObject);
            _spare.Clear();
            if (_beacon != null) Object.Destroy(_beacon.gameObject);
            _defend.Phase.Value = "";
        }
    }
}
