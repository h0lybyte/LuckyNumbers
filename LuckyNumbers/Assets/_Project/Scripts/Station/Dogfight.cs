using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Space combat round the station (content/dogfight.txtpb; OrbitPresenter drives it). Bolts fly and hit for real,
    /// so aim is what lands a shot; one that connects rolls d20 + the shooter's bonus against the target's evasion for
    /// its grade (Roll: graze, hit, strong, crit) and the gun's dice (Planet/Dice.Damage). Damage takes the shield
    /// first (it recharges after a few quiet seconds), then armour cuts what's left (flat, per hit), then the hull.
    ///
    /// Rogue ships are copies of the fighter in their own skins: each chases you, leads its shots, breaks off when it
    /// gets too close and comes round again. A satellite job (StartUplink) puts a relay out from the station: inside its
    /// range the upload runs (and pauses outside), and the rogues come in waves as it goes. Your hull at 0: Destroyed.
    /// </summary>
    public sealed class Dogfight : IDisposable, IBoltSink
    {
        sealed class Rogue
        {
            public RogueShip Type;
            public ShipView View;
            public Collider Hitbox;
            public RogueBrain Brain;
            public RogueFlight Flight;
            public Vector3 Position, Velocity, Home;
            public Quaternion Rotation;
            // Where it was at the last fixed step (drawn between that and now), and the velocity its pilot wants,
            // eased so a change of mind turns into a curve rather than a snap.
            public Vector3 PrevPosition, Wanted;
            public Quaternion PrevRotation;
            public float Pace;
            public float Hull, Shield, Ready, HitAt = -99f, BumpAt = -99f;
            public int Barrel;
            public bool Dead, Waiting;   // Waiting: lying in wait at the satellite (the ambush is sprung once it moves)
        }

        static DogfightConfig C => GameContent.Dogfight;

        readonly ShipView _player;
        readonly GameObject _template;
        readonly DogfightState _state;
        readonly PlayerJob _job;
        readonly PlayerVoyage _voyage;
        readonly IPublisher<CombatRoll> _rolls;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly IReadOnlyList<Bounds> _boxes;   // the station's hull blocks (solid: ships bounce off them)
        readonly List<Rogue> _rogues = new();
        readonly Dictionary<Collider, Rogue> _byCollider = new();
        readonly BoltField _field;   // every rogue's bolts
        readonly GameObject _playerBox;
        readonly Collider _playerCollider;
        Random _rng;

        float _hull, _hullMax, _shield, _shieldMax, _hitAt = -99f, _shieldHitAt = -99f, _hullHitAt = -99f, _stateAt;
        readonly int _armor, _evasion, _gunnery;
        readonly string _gunneryMods;
        Rogue _target;
        float _targetAt;
        Vector3 _playerPos, _prevPlayerPos, _kick;
        float _simAcc, _simTime, _jolt;
        bool _hasPrevPlayer;
        readonly float _length;   // your ship's, for keeping the rogues off it
        readonly SpaceNav _nav;   // round the station's hull
        readonly AttackTokens _tokens = new();

        // A satellite job.
        bool _uplink, _forJob, _done;
        Vector3 _satellite;
        Transform _satelliteView;
        float _radius, _seconds, _uploaded;
        int _waves, _perWave, _wave, _danger;
        UplinkTier _tier;
        bool _ambushed;
        float _alertUntil;

        /// <summary>Your hull is gone (set once; OrbitPresenter plays the memory restore).</summary>
        public bool Destroyed { get; private set; }

        public float Hull => _hull;

        public Dogfight(ShipView player, GameObject rogueTemplate, ShipStats stats, CharacterSheet sheet, uint luck,
                        float length, DogfightState state, PlayerJob job, PlayerVoyage voyage, IPublisher<CombatRoll> rolls,
                        IPublisher<CharacterXpGained> xp, uint seed, IReadOnlyList<Bounds> obstacles)
        {
            _nav = new SpaceNav(obstacles);
            _tokens.Max = math.max(1, C?.Attackers ?? 2);
            _player = player;
            _template = rogueTemplate;
            _state = state;
            _job = job;
            _voyage = voyage;
            _rolls = rolls;
            _xp = xp;
            _boxes = obstacles;
            _simTime = Time.time;
            _rng = new Random(seed | 1u);

            var d = C?.Defense ?? new ShipDefense();
            _hullMax = math.max(1f, d.Hull * math.max(0.1f, stats.Toughness));
            _shieldMax = math.max(0f, d.Shield * math.max(0f, stats.Shield));
            _armor = (int)math.round(d.Armor * math.max(0f, stats.Armor));
            _evasion = d.Evasion + (int)math.round(d.EvasionPerAgility * (stats.Agility - 1f));
            _hull = _hullMax;
            _shield = _shieldMax;

            // Gunnery: the sheet's skill (Piloting), your lucky number's share and the cyborg targeting implant.
            var g = C?.Gunnery ?? new Gunnery();
            var skill = SkillBonus(sheet, g.Skill);
            var lucky = g.LuckMax > 0 ? (int)(luck % (uint)(g.LuckMax + 1)) : 0;
            _gunnery = skill + lucky + g.Implant;
            _gunneryMods = Mods(("PIL", skill), ("luck", lucky), ("implant", g.Implant));

            // You can be hit: a sphere round your hull (your own bolts skip it: they ignore your pivot).
            _playerBox = new GameObject("Hitbox (dogfight)");
            _playerBox.transform.SetParent(player.Pivot, false);
            var sphere = _playerBox.AddComponent<SphereCollider>();
            sphere.radius = length * 0.42f / math.max(1e-4f, player.Pivot.lossyScale.x);
            _playerCollider = sphere;

            var prewarm = 0;
            if (C != null) foreach (var r in C.Rogues) prewarm += r.Bolt.PrewarmOr(6);
            _field = new BoltField("RogueLaser", math.max(8, prewarm)) { Layer = rogueTemplate.layer };
            _length = length;

            _state.Active.Value = true;
            _state.Destroyed.Value = false;
            _state.Evasion.Value = _evasion;
            _state.Gunnery.Value = _gunnery;
            _state.Armor.Value = _armor;
            _state.UplinkJob.Value = false;
            _state.UplinkDone.Value = false;
            _state.Uplink.Value = 0f;
            Publish();
            Debug.Log($"[Dogfight] hull {_hullMax:0}, shield {_shieldMax:0}, armour {_armor}, evasion {_evasion}, " +
                      $"gunnery +{_gunnery} ({_gunneryMods})");
        }

        /// <summary>Your guns' hits come here (ShipGuns.Hit): a rogue they hit takes a rolled shot.</summary>
        public void Arm(ShipGuns guns) => guns.Hit = OnPlayerBolt;

        /// <summary>
        /// A satellite job: the relay at `position` (its view, to spin), the upload `seconds`, `waves` of `perWave`
        /// rogues as it goes (danger picks which kinds). `forJob`: done sets the job done (false: the dev fight).
        /// </summary>
        public void StartUplink(Vector3 position, Transform view, float radius, float seconds, int waves, int perWave,
                                int danger, bool forJob, UplinkTier tier = null)
        {
            _tier = tier;
            _ambushed = false;
            if (tier is { WaveSize: > 0 }) perWave = math.min(perWave, tier.WaveSize);
            _uplink = true;
            _forJob = forJob;
            _satellite = position;
            _satelliteView = view;
            _radius = math.max(10f, radius);
            _seconds = math.max(5f, seconds);
            _waves = math.max(1, waves);
            _perWave = math.max(1, perWave);
            _danger = math.max(1, danger);
            _uploaded = 0f;
            _wave = 0;
            _done = false;
            _state.UplinkJob.Value = true;
            _state.Waves.Value = _waves;
            _state.Wave.Value = 0;
            _state.Tier.Value = tier?.Name ?? "";
            // Lying in wait: circling the satellite, until you come close (or shoot one).
            if (tier is { Waiting: > 0 })
            {
                var range = math.max(2f, tier.AmbushRange) * _length;
                foreach (var r in SpawnAt(tier.Waiting, danger, position, radius * 0.6f, radius * 1.4f))
                {
                    r.Waiting = true;
                    r.Brain.AggroOverride = range;
                }
            }
            Debug.Log($"[Dogfight] tier {tier?.Name ?? "-"} (waiting {tier?.Waiting ?? 0}, ambush {tier?.Ambush ?? 0} at {tier?.AmbushAt ?? 0:0.##}); satellite {Vector3.Distance(position, _playerPos):0} m out: {_seconds:0} s upload, " +
                      $"{_waves} wave(s) of {_perWave}, danger {_danger}");
        }

        /// <summary>Dev: the upload's done at once (the job too, if it's for one).</summary>
        public void FinishUplink()
        {
            if (!_uplink || _done) return;
            _uploaded = _seconds;
            _done = true;
            if (_forJob && _job.Active.Value) _job.Done.Value = true;
            Debug.Log($"[Dogfight] dev: uplink done (job: {_forJob})");
        }

        /// <summary>`count` rogues (kinds by `danger`) `near`..`far` metres out from `around`, turned to face you.</summary>
        public void SpawnWave(int count, int danger, Vector3 around, float near = 300f, float far = 420f)
        {
            SpawnAt(count, danger, around, near, far);
            Debug.Log($"[Dogfight] wave: {count} rogue(s) at danger {danger}");
        }

        List<Rogue> SpawnAt(int count, int danger, Vector3 around, float near, float far)
        {
            var spawned = new List<Rogue>();
            if (C == null || C.Rogues.Count == 0) return spawned;
            var kinds = new List<RogueShip>();
            foreach (var r in C.Rogues) if (r.DangerMin <= danger) kinds.Add(r);
            if (kinds.Count == 0) kinds.Add(C.Rogues[0]);
            for (var i = 0; i < count; i++)
            {
                var type = kinds[_rng.NextInt(kinds.Count)];
                var dir = (Vector3)_rng.NextFloat3Direction();
                dir.y *= 0.35f;   // mostly round the horizon, not from straight above
                var at = around + dir.normalized * _rng.NextFloat(near, math.max(near, far));
                spawned.Add(Spawn(type, at));
            }
            _state.Hostiles.Value = Alive();
            return spawned;
        }

        Rogue Spawn(RogueShip type, Vector3 at)
        {
            var go = Object.Instantiate(_template);
            go.name = $"Rogue {type.Name}";
            go.SetActive(true);
            var view = go.GetComponent<ShipView>();
            var length = type.Length > 0f ? type.Length : 24f;
            view.Show(type.Skin, length);
            Layers.Set(go, _template.layer);   // its flames and gear too (built by Show)
            var box = new GameObject("Hitbox").AddComponent<SphereCollider>();
            box.transform.SetParent(view.Pivot, false);
            box.radius = length * 0.45f / math.max(1e-4f, view.Pivot.lossyScale.x);
            var r = new Rogue
            {
                Type = type, View = view, Hitbox = box, Position = at, Home = at,
                Brain = new RogueBrain(type, _tokens, ref _rng),
                Rotation = Quaternion.LookRotation(_playerPos - at, Vector3.up),
                Hull = math.max(1f, type.Hull), Shield = math.max(0f, type.Shield),
                Ready = Time.time + _rng.NextFloat(1f, 2.5f),   // a moment before the first shot
            };
            r.Velocity = r.Rotation * Vector3.forward * type.Speed;
            r.Wanted = r.Velocity;
            r.PrevPosition = r.Position;
            r.PrevRotation = r.Rotation;
            r.Flight.Speed = type.Speed;
            view.Pose(r.Position, r.Rotation);
            view.SetHeat(0f, 1.2f);
            _rogues.Add(r);
            _byCollider[box] = r;
            return r;
        }

        /// <summary>A warning for the page to flash for a few seconds.</summary>
        void Alert(string text)
        {
            _state.Alert.Value = text;
            _alertUntil = Time.time + 3.5f;
            Debug.Log($"[Dogfight] alert: {text}");
        }

        /// <summary>One frame: the rogues fly and shoot, shields recharge, the upload runs; then the page's state.</summary>
        public void Tick(float dt, Vector3 playerPos, Vector3 playerVel)
        {
            if (!_hasPrevPlayer) { _prevPlayerPos = playerPos; _hasPrevPlayer = true; }
            _playerPos = playerPos;
            if (!Destroyed)
            {
                var d = C?.Defense;
                if (d != null && Time.time - _hitAt > d.ShieldDelay && _shield < _shieldMax)
                    _shield = math.min(_shieldMax, _shield + d.ShieldRegen * dt);
            }
            // The rogues fly at a fixed rate (the same flight however the frame rate wobbles, which matters most on a
            // slow web page) and are drawn between their last two steps. You moved once this frame: each step sees you
            // part of the way along.
            var h = 1f / math.clamp(C?.SimRate is > 0f ? C.SimRate : 60f, 20f, 240f);
            _simAcc = math.min(_simAcc + dt, h * 5f);
            var steps = (int)(_simAcc / h);
            for (var k = 0; k < steps; k++)
            {
                _simAcc -= h;
                _simTime += h;
                var at = Vector3.Lerp(_prevPlayerPos, playerPos, (k + 1f) / steps);
                foreach (var r in _rogues)
                {
                    if (r.Dead) continue;
                    r.PrevPosition = r.Position;
                    r.PrevRotation = r.Rotation;
                }
                for (var i = _rogues.Count - 1; i >= 0; i--)
                {
                    var r = _rogues[i];
                    if (r.Dead) continue;
                    Fly(r, h, at, playerVel + _kick);
                }
                Collide(at, playerVel);
            }
            _prevPlayerPos = playerPos;
            var alpha = math.saturate(_simAcc / h);
            foreach (var r in _rogues)
            {
                if (r.Dead) continue;
                r.View.Pose(Vector3.Lerp(r.PrevPosition, r.Position, alpha), Quaternion.Slerp(r.PrevRotation, r.Rotation, alpha));
                var spin = r.Flight.Spin / math.max(10f, r.Type.TurnRate);
                r.View.SetThrust(new Vector3(0f, 0.2f, math.min(1.4f, r.Pace)),
                                 new Vector3(math.clamp(spin.x, -1f, 1f), math.clamp(spin.y, -1f, 1f), math.clamp(spin.z * 0.4f, -1f, 1f)),
                                 r.Pace > 1.1f ? 1.4f : 1f);
            }
            // The ones lying in wait: once one moves on you, the ambush is sprung.
            var sprung = false;
            foreach (var r in _rogues)
                if (r.Waiting && !r.Dead && r.Brain.State != RogueState.Patrol) { r.Waiting = false; sprung = true; }
            if (sprung) Alert("AMBUSH: rogues at the satellite!");
            if (_alertUntil > 0f && Time.time > _alertUntil) { _alertUntil = 0f; _state.Alert.Value = ""; }
            if (_uplink) Upload(dt, playerPos);
            _field.Tick(dt);
            if (_satelliteView != null) _satelliteView.Rotate(Vector3.up, 6f * dt, UnityEngine.Space.World);

            _stateAt -= dt;
            if (_stateAt <= 0f)
            {
                _stateAt = 1f / 15f;
                Publish();
            }
        }

        /// <summary>
        /// A rogue's flying: its pilot (RogueBrain) picks a goal, the velocity it wants and whether it may shoot; SpaceNav
        /// routes the goal round the station (a detour point while the hull's in the way) and pushes it off the hull just
        /// ahead; ships it's on course to pass too close to (closest approach, Steering.AvoidShip) take priority while
        /// they're near; RogueFlight flies the result, and it fires when you're in its cone, in range and in sight.
        /// </summary>
        void Fly(Rogue r, float dt, Vector3 playerPos, Vector3 playerVel)
        {
            var t = r.Type;
            var to = playerPos - r.Position;
            var dist = to.magnitude;
            var boltSpeed = math.max(1f, t.Bolt?.Speed ?? 140f);
            var aim = playerPos + playerVel * (dist / boltSpeed);
            var fwd = r.Rotation * Vector3.forward;
            var senses = new RogueSenses
            {
                Position = r.Position, Forward = fwd, Velocity = r.Velocity, Home = r.Home,
                Player = playerPos, PlayerVelocity = playerVel, PlayerForward = _player.Pivot.forward, Aim = aim,
                Distance = dist, HullShare = r.Hull / math.max(1f, t.Hull), Time = _simTime,
                PlayerAlive = !Destroyed, ShieldDown = r.Shield <= 0.5f, ShieldFull = r.Shield >= t.Shield - 0.5f,
                Hit = Time.time - r.HitAt < 0.5f,
            };
            var (goal, asked, mayFire) = r.Brain.Think(senses, ref _rng);
            // Eased (direction and speed together, ~0.2 s), so a change of plan becomes a curve, not a snap.
            r.Wanted = Vector3.Slerp(r.Wanted, asked, 1f - math.exp(-5f * dt));
            var wanted = r.Wanted;

            // Getting there: round the station (keeping its speed while it detours), and off the hull just ahead.
            var radius = t.Length * 0.5f + math.max(0f, C?.Clearance ?? 18f);
            var waypoint = _nav.Waypoint(r.Position, goal, radius);
            var desired = wanted;
            if ((waypoint - goal).sqrMagnitude > 1f)
            {
                var detour = waypoint - r.Position;
                desired = (detour.sqrMagnitude > 1e-3f ? detour.normalized : fwd) * math.max(wanted.magnitude, t.Speed * 0.9f);
            }
            // Other ships: where it'll pass closest to each in the next second and a half. A near miss coming takes over
            // from the goal while it lasts (fully for a dead-on one right now); on a run, only a
            // last-moment swerve keeps it off you.
            var ships = Vector3.zero;
            const float horizon = 1.5f;
            foreach (var o in _rogues)
            {
                if (o == r || o.Dead) continue;
                ships += Steering.AvoidShip(r.Position - o.Position, r.Velocity - o.Velocity, (t.Length + o.Type.Length) * 1.5f, horizon);
            }
            if (!mayFire && !Destroyed)
                ships += Steering.AvoidShip(-to, r.Velocity - playerVel, (t.Length + _length) * 2f, horizon);
            else if (!Destroyed)   // on a run: it passes close, but a pilot doesn't ram (a short look, a tight gap)
                ships += Steering.AvoidShip(-to, r.Velocity - playerVel, (t.Length + _length) * 1.1f, 0.9f);
            var urgency = math.saturate(ships.magnitude);
            if (urgency > 0f)
                desired = Vector3.Lerp(desired, ships.normalized * math.max(desired.magnitude, t.Speed), urgency);
            var hull = _nav.Avoid(r.Position, r.Velocity.sqrMagnitude > 1f ? r.Velocity : fwd * t.Speed, radius, C?.Lookahead ?? 1.6f);
            desired += hull * (t.Speed * 2f);
            if (desired.sqrMagnitude < 1e-4f) desired = fwd * t.Speed;
            r.Pace = desired.magnitude / math.max(1f, t.Speed);

            // Flying it like a fighter: bank and pull, eased rates, speed bled in hard turns (RogueFlight).
            r.Flight.Step(ref r.Rotation, ref r.Velocity, desired, t.Speed, t.TurnRate, t.Speed * 1.3f, dt);
            fwd = r.Rotation * Vector3.forward;
            r.Position += r.Velocity * dt;
            var crash = Impacts.OutOfBoxes(_boxes, ref r.Position, ref r.Velocity, t.Length * 0.5f, Restitution, Scrape, out var face);
            if (crash > 0f && Time.time > r.BumpAt)
            {
                r.BumpAt = Time.time + Cooldown;
                var hurt = Impacts.Damage(crash, MinSpeed, PerSpeed, C?.Collisions?.HullSize ?? 1.5f);
                if (hurt > 0) Bumped(r, hurt, r.Position - face * (t.Length * 0.5f));
            }

            // Rogue shields recharge too (a fifth of the way a second, after the same quiet spell).
            var d = C?.Defense;
            if (d != null && Time.time - r.HitAt > d.ShieldDelay && r.Shield < t.Shield)
                r.Shield = math.min(t.Shield, r.Shield + t.Shield * 0.2f * dt);

            if (Destroyed || !mayFire || Time.time < r.Ready || dist > t.FireRange) return;
            var lead = aim - r.Position;
            if (Vector3.Angle(fwd, lead) > math.max(1f, t.FireCone)) return;
            if (_nav.Blocked(r.Position, playerPos, 0f)) return;   // the station's in the way: no shooting through it
            r.Ready = Time.time + math.max(0.1f, t.Cooldown) * _rng.NextFloat(0.85f, 1.2f);
            // Not a perfect shot: off the lead by up to a few degrees (more at range), so dodging pays.
            var spread = math.lerp(1f, 4f, math.saturate(dist / math.max(1f, t.FireRange))) * math.lerp(1.6f, 0.6f, r.Brain.Skill);
            var off = Quaternion.AngleAxis(_rng.NextFloat(0f, spread), Vector3.Cross(lead, (Vector3)_rng.NextFloat3Direction()));
            Fire(r, off * lead.normalized);
        }

        static Collisions K => C?.Collisions;
        static float MinSpeed => K?.MinSpeed ?? 10f;
        static float PerSpeed => K?.DamagePerSpeed ?? 0.35f;
        static float Restitution => K?.Restitution ?? 0.35f;
        static float Scrape => K?.Scrape ?? 0.85f;
        static float Cooldown => math.max(0.05f, K?.Cooldown ?? 0.4f);
        static float Mass(float length) => math.max(1f, length * length);

        /// <summary>
        /// Ships don't pass through each other: touching ships are pushed apart and bounce (Impacts.Bounce: the heavier
        /// moves less), and a hard enough crash hurts both, by closing speed and size (once per `cooldown` per ship).
        /// Your share of the bounce builds up in TakeKick for OrbitPresenter to put on your flight.
        /// </summary>
        void Collide(Vector3 playerPos, Vector3 playerVel)
        {
            for (var i = 0; i < _rogues.Count; i++)
            {
                var a = _rogues[i];
                if (a.Dead) continue;
                if (!Destroyed)
                {
                    var room = (a.Type.Length + _length) * 0.5f;
                    var away = a.Position - playerPos;
                    var d = away.magnitude;
                    if (d < room)
                    {
                        var n = d > 1e-3f ? away / d : a.Rotation * Vector3.back;
                        a.Position = playerPos + n * room;
                        var before = playerVel + _kick;
                        var you = before;
                        var closing = Impacts.Bounce(ref a.Velocity, ref you, n, Mass(a.Type.Length), Mass(_length), Restitution, Scrape);
                        _kick += you - before;
                        if (closing > 0f && Time.time > a.BumpAt)
                        {
                            a.BumpAt = Time.time + Cooldown;
                            var contact = playerPos + n * (_length * 0.5f);
                            Crash(Impacts.Damage(closing, MinSpeed, PerSpeed, a.Type.Length / math.max(1f, _length)), a.Type.Name, closing);
                            var hurt = Impacts.Damage(closing, MinSpeed, PerSpeed, _length / math.max(1f, a.Type.Length));
                            if (hurt > 0) Bumped(a, hurt, contact);
                            else _field.Flash(contact, a.Type.Length * 0.4f, 0.2f, new Color(2.5f, 2.2f, 1.6f));
                            if (a.Dead) continue;
                        }
                    }
                }
                for (var j = i + 1; j < _rogues.Count; j++)
                {
                    var b = _rogues[j];
                    if (b.Dead) continue;
                    var room = (a.Type.Length + b.Type.Length) * 0.5f;
                    var gap = a.Position - b.Position;
                    var d = gap.magnitude;
                    if (d >= room) continue;
                    var n = d > 1e-3f ? gap / d : Vector3.up;
                    var ma = Mass(a.Type.Length);
                    var mb = Mass(b.Type.Length);
                    var push = room - d;
                    a.Position += n * (push * mb / (ma + mb));
                    b.Position -= n * (push * ma / (ma + mb));
                    var closing = Impacts.Bounce(ref a.Velocity, ref b.Velocity, n, ma, mb, Restitution, Scrape);
                    if (closing <= 0f || Time.time < a.BumpAt || Time.time < b.BumpAt) continue;
                    a.BumpAt = b.BumpAt = Time.time + Cooldown;
                    var contact = b.Position + n * (b.Type.Length * 0.5f);
                    var toA = Impacts.Damage(closing, MinSpeed, PerSpeed, b.Type.Length / math.max(1f, a.Type.Length));
                    var toB = Impacts.Damage(closing, MinSpeed, PerSpeed, a.Type.Length / math.max(1f, b.Type.Length));
                    if (toA > 0) Bumped(a, toA, contact);
                    if (toB > 0) Bumped(b, toB, contact);
                    if (a.Dead) break;
                }
            }
        }

        /// <summary>A rogue's crash damage (no roll: physics), with sparks; a crash that kills it still pays its bounty.</summary>
        void Bumped(Rogue r, int damage, Vector3 at)
        {
            Take(ref r.Shield, ref r.Hull, r.Type.Armor, damage, out _);
            r.HitAt = Time.time;
            _field.Flash(at, r.Type.Length * 0.6f, 0.3f, new Color(3.2f, 2.4f, 1.2f));
            Jolt(0.25f, at, 250f);
            Debug.Log($"[Dogfight] {r.Type.Name} crashed ({r.Brain.State}): {damage} damage");
            if (r.Hull <= 0f) Kill(r);
        }

        /// <summary>
        /// You crashed (into a rogue, or the hull: OrbitPresenter calls this) at `closing` m/s for `damage`: through
        /// your shield, armour and hull like a bolt, and a jolt for the camera however soft.
        /// </summary>
        public void Crash(int damage, string into, float closing)
        {
            if (Destroyed) return;
            _jolt += math.saturate(0.15f + closing / 60f);
            if (damage <= 0) return;
            var shieldBefore = _shield;
            Take(ref _shield, ref _hull, _armor, damage, out var toHull);
            _hitAt = Time.time;
            if (_shield < shieldBefore) _shieldHitAt = Time.time;
            if (toHull > 0) _hullHitAt = Time.time;
            Debug.Log($"[Dogfight] crashed into {into} at {closing:0} m/s: {damage} damage ({toHull} to the hull)");
            if (_hull <= 0f) Die(into);
        }

        /// <summary>Camera trauma from something at `at`, fading out by `reach` m from you.</summary>
        void Jolt(float amount, Vector3 at, float reach) =>
            _jolt += amount * math.saturate(1f - Vector3.Distance(at, _playerPos) / math.max(1f, reach));

        /// <summary>Camera trauma built up since the last call (hits, crashes, blasts nearby), 0..1-ish.</summary>
        public float TakeJolt()
        {
            var j = _jolt;
            _jolt = 0f;
            return j;
        }

        /// <summary>The change to your velocity from bounces since the last call (OrbitPresenter adds it to your flight).</summary>
        public Vector3 TakeKick()
        {
            var k = _kick;
            _kick = Vector3.zero;
            return k;
        }

        void Fire(Rogue r, Vector3 dir)
        {
            var p = r.Type.Bolt ?? new Projectile { Speed = 140f, Range = 350f, Length = 7f, Width = 0.6f };
            var from = r.View.Muzzle(r.Barrel % math.max(1, r.View.MuzzleCount));
            r.Barrel++;
            var color = p.Tint(new Color(3f, 1.2f, 0.2f));
            var (impact, impactLife) = p.Impact.Of(p.Width, 10f, 0.25f);
            _field.Fire(new BoltShot
            {
                From = from, Direction = dir, Speed = p.Speed + math.max(0f, Vector3.Dot(r.Velocity, dir)), Range = p.Range,
                Length = p.Length, Width = p.Width, Color = color,
                ImpactSize = impact, ImpactLife = impactLife, ImpactLift = 0.05f, Impulse = 0f,
                Source = "rogue", Ignore = r.View.Pivot, Sink = this, Tag = r,
            });
            var (size, life) = p.Muzzle.Of(p.Width, 4f, 0.07f);
            _field.Flash(from, size, life, color);
        }

        /// <summary>The upload: runs inside the range (pauses outside); the waves come as it goes; done, the job's done.</summary>
        void Upload(float dt, Vector3 playerPos)
        {
            var s = C?.Satellite;
            var dist = Vector3.Distance(playerPos, _satellite);
            var inRange = dist <= _radius && !Destroyed;
            _state.InRange.Value = inRange;
            _state.SatelliteDistance.Value = dist;
            if (!_done && inRange)
            {
                _uploaded += dt;
                // The waves spread over the upload: the first `first_wave` seconds in, the rest evenly after it.
                var first = math.min(s?.FirstWave ?? 4f, _seconds * 0.5f);
                var gap = math.min(s != null && s.WaveSeconds > 0f ? s.WaveSeconds : 16f, (_seconds - first) / _waves);
                while (_wave < _waves && _uploaded >= first + _wave * gap)
                {
                    _wave++;
                    _state.Wave.Value = _wave;
                    // A ramp: the first wave is one ship, each after it one more, up to the job's size.
                    SpawnWave(math.min(_wave, _perWave), _danger, _satellite);
                }
                // The tier's ambush: partway through, rogues come at you from behind.
                if (!_ambushed && _tier is { Ambush: > 0 } && _uploaded >= _seconds * math.saturate(_tier.AmbushAt))
                {
                    _ambushed = true;
                    var behind = playerPos - _player.Pivot.forward * 260f;
                    SpawnAt(_tier.Ambush, _danger, behind, 0f, 60f);
                    Alert("AMBUSH: behind you!");
                }
                if (_uploaded >= _seconds)
                {
                    _done = true;
                    if (_forJob && _job.Active.Value) _job.Done.Value = true;
                    Debug.Log($"[Dogfight] uplink done ({_rogues.Count - Alive()} rogue(s) down, {Alive()} still up)");
                }
            }
            _state.Uplink.Value = math.saturate(_uploaded / _seconds);
            _state.UplinkDone.Value = _done;
            var line = s == null ? "" : _done ? s.Done : inRange ? s.Uplinking : _uploaded > 0f ? s.OutOfRange : s.Approach;
            // Not a job (?ln-dogfight): say so, so nobody waits on pay that isn't coming (bounties still pay).
            _state.Objective.Value = _forJob ? line : _done ? "PRACTICE done: no job, no pay (bounties still count)" : "PRACTICE (no job): " + line;
        }

        // ---- hits ------------------------------------------------------------------------------------------------

        /// <summary>Your bolt hit something: a rogue takes the rolled shot (d20 + gunnery vs its evasion).</summary>
        void OnPlayerBolt(ShipGun gun, RaycastHit hit, IDamageable target)
        {
            if (Destroyed || !_byCollider.TryGetValue(hit.collider, out var r) || r.Dead) return;
            var dice = !string.IsNullOrEmpty(gun.Damage) ? gun.Damage : C?.Gunnery?.DefaultDamage ?? "1d4";
            var roll = Roll(_gunnery, r.Type.Evasion, dice);
            Take(ref r.Shield, ref r.Hull, r.Type.Armor, roll.Damage, out _);
            r.HitAt = Time.time;
            _target = r;
            _targetAt = Time.time;
            _rolls.Publish(new CombatRoll("You", r.Type.Name, roll.D20, _gunnery, roll.Total, r.Type.Evasion, true, roll.Crit,
                                          false, roll.Damage, dice, true, gun.Name ?? gun.Id, roll.Graze, roll.Strong, _gunneryMods));
            if (r.Hull <= 0f) Kill(r);
        }

        /// <summary>A rogue's bolt hit something: if it's you, its gunner rolls against your evasion.</summary>
        void IBoltSink.OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled)
        {
            if (Destroyed || hit.collider != _playerCollider || shot.Tag is not Rogue r) return;
            var t = r.Type;
            var dice = string.IsNullOrEmpty(t.Damage) ? "1d4" : t.Damage;
            var roll = Roll(t.Attack, _evasion, dice);
            var shieldBefore = _shield;
            Take(ref _shield, ref _hull, _armor, roll.Damage, out var toHull);
            _hitAt = Time.time;
            if (_shield < shieldBefore) _shieldHitAt = Time.time;
            if (toHull > 0) _hullHitAt = Time.time;
            _rolls.Publish(new CombatRoll(t.Name, "You", roll.D20, t.Attack, roll.Total, _evasion, true, roll.Crit, false,
                                          roll.Damage, dice, false, t.Name, roll.Graze, roll.Strong, ""));
            _jolt += math.saturate(0.2f + roll.Damage / math.max(1f, _hullMax) * 2f);
            if (_hull <= 0f) Die(t.Name);
        }

        struct Shot
        {
            public int D20, Total, Damage;
            public bool Crit, Strong, Graze;
        }

        /// <summary>
        /// A bolt that connected: d20 + bonus against evasion. Natural 1, or more than 2 under, grazes (half); under or a
        /// little over hits; 5 or more over is strong (an extra die); a natural 20 crits (the dice's best plus a roll).
        /// </summary>
        Shot Roll(int bonus, int evasion, string dice)
        {
            var d20 = _rng.NextInt(1, 21);
            var total = d20 + bonus;
            var crit = d20 == 20;
            var graze = !crit && (d20 == 1 || total < evasion - 2);
            var strong = !crit && !graze && total >= evasion + 5;
            return new Shot { D20 = d20, Total = total, Crit = crit, Strong = strong, Graze = graze,
                              Damage = Dice.Damage(dice, ref _rng, crit, strong, graze) };
        }

        /// <summary>Damage through the layers: the shield soaks what it can; armour cuts the rest; the hull takes that.</summary>
        static void Take(ref float shield, ref float hull, int armor, int damage, out int toHull)
        {
            var soaked = math.min(math.max(0f, shield), damage);
            shield -= soaked;
            var rest = damage - (int)math.ceil(soaked);
            toHull = rest > 0 ? math.max(C?.Defense?.ArmorFloor ?? 0, rest - math.max(0, armor)) : 0;
            hull -= toHull;
        }

        void Kill(Rogue r)
        {
            r.Dead = true;
            r.Brain.Release();
            _byCollider.Remove(r.Hitbox);
            var at = r.Position;
            _field.Flash(at, r.Type.Length * 2.2f, 0.7f, new Color(3.5f, 1.6f, 0.4f));
            _field.Flash(at, r.Type.Length * 1.2f, 0.35f, new Color(4f, 3.5f, 2.5f));
            Jolt(0.45f, at, 350f);
            Object.Destroy(r.View.gameObject);
            if (r.Type.Xp > 0) _xp.Publish(new CharacterXpGained(r.Type.Xp, r.Type.Name));
            if (r.Type.Bounty > 0)
            {
                _voyage.Credits.Value += r.Type.Bounty;
                _voyage.LastPayout.Value = r.Type.Bounty;
            }
            if (_target == r) _target = null;
            _state.Hostiles.Value = Alive();
            Debug.Log($"[Dogfight] {r.Type.Name} destroyed: +{r.Type.Xp} XP, +{r.Type.Bounty} cr ({Alive()} left)");
        }

        void Die(string by)
        {
            Destroyed = true;
            _hull = 0f;
            var at = _player.Pivot.position;
            _field.Flash(at, 70f, 0.9f, new Color(3.5f, 1.4f, 0.3f));
            _field.Flash(at, 35f, 0.4f, new Color(4f, 3.6f, 2.8f));
            _state.Destroyed.Value = true;
            _state.DestroyedText.Value = C?.Restore?.DestroyedText ?? "";
            Publish();
            Debug.Log($"[Dogfight] destroyed by {by}");
        }

        int Alive()
        {
            var n = 0;
            foreach (var r in _rogues) if (!r.Dead) n++;
            return n;
        }

        /// <summary>The page's HUD: your layers, the target (the last you hit, else the nearest ahead of you).</summary>
        void Publish()
        {
            _state.Hull.Value = math.max(0f, _hull);
            _state.HullMax.Value = _hullMax;
            _state.Shield.Value = math.max(0f, _shield);
            _state.ShieldMax.Value = _shieldMax;
            _state.ShieldHit.Value = Time.time - _shieldHitAt < 0.25f;
            _state.HullHit.Value = Time.time - _hullHitAt < 0.35f;
            _state.Hostiles.Value = Alive();
            var target = _target != null && !_target.Dead && Time.time - _targetAt < 4f ? _target : Ahead();
            _state.Target.Value = target == null ? ""
                : $"{target.Type.Name}|{math.max(0f, target.Hull):0}|{target.Type.Hull:0}|{math.max(0f, target.Shield):0}|{target.Type.Shield:0}|" +
                  $"{Vector3.Distance(target.Position, _playerPos):0}|{target.Brain.State}";
        }

        /// <summary>The live rogue nearest your nose (inside 25 degrees and 700 m), or null.</summary>
        Rogue Ahead()
        {
            Rogue best = null;
            var bestScore = float.MaxValue;
            var fwd = _player.Pivot.forward;
            foreach (var r in _rogues)
            {
                if (r.Dead) continue;
                var to = r.Position - _playerPos;
                var dist = to.magnitude;
                var angle = Vector3.Angle(fwd, to);
                if (dist > 700f || angle > 25f) continue;
                var score = angle * 10f + dist;
                if (score < bestScore) { bestScore = score; best = r; }
            }
            return best;
        }

        static int SkillBonus(CharacterSheet sheet, string id)
        {
            if (sheet == null || string.IsNullOrEmpty(id)) return 0;
            foreach (var s in sheet.Skills)
            {
                var p = (s ?? "").Split('|');   // "id|name|abbr|bonus|trained"
                if (p.Length >= 4 && p[0] == id && int.TryParse(p[3], out var bonus)) return bonus;
            }
            return 0;
        }

        static string Mods(params (string label, int value)[] parts)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (label, value) in parts)
            {
                if (value == 0) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(label).Append(value > 0 ? " +" : " −").Append(math.abs(value));
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            foreach (var r in _rogues) if (!r.Dead && r.View != null) Object.Destroy(r.View.gameObject);
            _rogues.Clear();
            _byCollider.Clear();
            if (_playerBox != null) Object.Destroy(_playerBox);
            _field.Dispose();
            _state.Active.Value = false;
            _state.UplinkJob.Value = false;
            _state.Target.Value = "";
            _state.Hostiles.Value = 0;
            _state.Alert.Value = "";
            _state.Tier.Value = "";
        }
    }
}
