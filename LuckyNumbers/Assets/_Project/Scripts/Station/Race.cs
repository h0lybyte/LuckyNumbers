using System;
using System.Collections.Generic;
using System.Text;
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
    /// A circuit race round the station (content/race.txtpb; OrbitPresenter drives it on a race job or ?ln-race). The
    /// course is RaceTrack's seeded loop: gates in order (gate 0 is the start / finish line), rows of mystery cubes,
    /// rocks off to the sides. You and the rivals start on a grid behind the line; after the countdown, fly the gates in
    /// order for the laps.
    ///
    /// Rivals are copies of the fighter in their own skins. Each follows the line a little ahead in its own lane (some
    /// swerve for a cube), keeps off the others (Steering.AvoidShip) and the rocks (SpaceNav), and flies like a rogue
    /// (RogueFlight) at its own pace, a little faster when well behind you and slower when well ahead (rubber band).
    ///
    /// A cube flown through rolls d20 + bonus (RaceDice): the colour's table and the total pick an effect, for whoever
    /// took it. Shield charges soak hostile effects; phase ignores them and ghosts through ships and rocks. Your place
    /// at the line pays the job's pay x the place's share, at the station (Dispatch, the homecoming).
    /// </summary>
    public sealed class Race : IDisposable
    {
        sealed class Racer
        {
            public string Name;
            public bool You;
            public ShipView View;
            public RogueFlight Flight;
            public Vector3 Position, Velocity, PrevPosition;
            public Quaternion Rotation = Quaternion.identity, PrevRotation = Quaternion.identity;
            public float Pace, Lane, Throttle;
            public int Sample, NextGate = 1, Lap = 1, GatesPassed, Shields, Place, Finish;
            public float FinishTime, LapStart;
            public float BoostUntil, BoostPower = 1f, SlowUntil, SlowPower = 1f, PhaseUntil, BumpAt;
            public int SeekRow = -1;     // the cube row it means to take a cube from
            public Vector3 SeekAt;
            public bool Dnf;
        }

        sealed class Cube
        {
            public RaceTrack.CubeSpot Spot;
            public GameObject View;
            public float BackAt;
            public bool Taken;
        }

        sealed class Mine
        {
            public Vector3 Position;
            public Racer Owner;
            public CubeOutcome Effect;
            public float ArmedAt, GoneAt;
            public GameObject View;
        }

        static RaceConfig C => GameContent.Race;

        readonly HoverFlight _you;
        readonly ShipView _player;
        readonly RaceState _state;
        readonly PlayerJob _job;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly IReadOnlyList<Bounds> _hull;
        readonly RaceTrack _track;
        readonly List<Racer> _racers = new();
        readonly List<Racer> _order = new();
        readonly List<Cube> _cubes = new();
        readonly List<Mine> _mines = new();
        readonly List<GameObject> _gates = new();
        readonly List<Bounds> _solid = new();   // rocks and the hull: the rivals steer round them
        readonly SpaceNav _nav;
        readonly GameObject _root;
        readonly BoltField _flashes;
        readonly float _length, _baseTop, _speedScale;
        readonly int _laps;
        readonly uint _luck;
        readonly bool _practice;
        Random _rng;
        Racer _me;

        float _clock = -1f, _countdown, _simAcc, _stateAt, _bannerUntil, _finishedAt = -1f, _winnerAt = -1f, _bestLap;
        int _finishers, _shownGate = -1;
        string _standings = "";
        bool _over;

        /// <summary>Your ship can't move yet (the countdown).</summary>
        public bool Frozen => _clock < 0f;

        /// <summary>Over for you: back to the station now (the finish card's had its time, or E).</summary>
        public bool ReadyToLeave { get; private set; }

        /// <summary>The share of your ship's speed to fly at now: racing trim, boosts, slows.</summary>
        public float SpeedFactor
        {
            get
            {
                var f = _speedScale;
                if (_me == null) return f;
                if (Time.time < _me.BoostUntil) f *= _me.BoostPower;
                if (Time.time < _me.SlowUntil) f *= _me.SlowPower;
                return f;
            }
        }

        /// <summary>Ghosting through things (phase): your crashes into the station's hull don't count either.</summary>
        public bool Phased => _me != null && Time.time < _me.PhaseUntil;

        public Race(HoverFlight you, ShipView player, GameObject template, float length, uint luck, uint seed, int laps, int danger,
                    bool practice, Vector3 start, Vector3 outward, IReadOnlyList<Bounds> hull, RaceState state, PlayerJob job,
                    IPublisher<CharacterXpGained> xp)
        {
            _you = you;
            _player = player;
            _state = state;
            _job = job;
            _xp = xp;
            _hull = hull;
            _length = length;
            _luck = luck;
            _practice = practice;
            _laps = math.max(1, laps);
            _rng = new Random(seed | 1u);
            _speedScale = math.max(0.1f, C.SpeedScale > 0f ? C.SpeedScale : 1f);
            var hover = GameContent.Planet?.Landing?.Hover;
            _baseTop = (hover?.MaxSpeed ?? 32f) * math.max(1f, hover?.Boost ?? 1.5f) * _speedScale;

            var weights = new List<float>();
            foreach (var c in C.Cubes) weights.Add(c.Weight);
            _track = RaceTrack.Generate(C.Track, seed, start, outward, weights);
            _solid.AddRange(_track.Hazards);
            if (hull != null) _solid.AddRange(hull);
            _nav = new SpaceNav(_solid);

            _root = new GameObject("Race course");
            BuildCourse();
            _flashes = new BoltField("RaceFlashes", 8) { Layer = Layers.Local };
            Grid(template, danger);
            Layers.Set(_root, Layers.Local);

            _countdown = math.max(0.5f, C.Countdown);
            Place();
            Banner(Mathf.CeilToInt(_countdown).ToString(), 0.9f);
            _state.Active.Value = true;
            _state.Practice.Value = practice;
            _state.Laps.Value = _laps;
            _state.Lap.Value = 1;
            _state.Gates.Value = _track.Gates.Count;
            _state.Gate.Value = 0;
            _state.Racers.Value = _racers.Count;
            _state.Finished.Value = false;
            _state.Result.Value = "";
            _state.Payout.Value = 0;
            _state.Time.Value = 0f;
            _state.BestLap.Value = 0f;
            _state.Shields.Value = 0;
            _state.Effects.Value = "";
            _state.Roll.Value = "";
            _state.Feed.Clear();
            _state.Standings.Clear();
            Debug.Log($"[Race] {(practice ? "practice" : "job")}: {_laps} lap(s), {_track.Gates.Count} gates, " +
                      $"{_track.Length:0} m a lap, {_cubes.Count} cubes, {_track.Hazards.Count} rocks, {_racers.Count} racers, danger {danger}");
        }

        // ---- the course ------------------------------------------------------------------------------------------

        static readonly Color GateIdle = new(0.25f, 0.55f, 0.8f);
        static readonly Color GateNext = new(0.6f, 2.6f, 3.2f);
        static readonly Color GateLine = new(3f, 2.6f, 1.6f);

        void BuildCourse()
        {
            var size = _track.GateSize;
            var bar = math.max(1.5f, size * 0.035f);
            for (var g = 0; g < _track.Gates.Count; g++)
            {
                var gate = _track.Gates[g];
                var root = new GameObject($"Gate {g}").transform;
                root.SetParent(_root.transform, false);
                root.SetPositionAndRotation(gate.Position, Quaternion.LookRotation(gate.Forward, gate.Up));
                var color = g == 0 ? GateLine : GateIdle;
                void Bar(string name, Vector3 at, Vector3 scale)
                {
                    var b = Placeholders.Block($"race_gate_{g}_{name}", scale, color, collider: false);
                    b.transform.SetParent(root, false);
                    b.transform.localPosition = at;
                }
                var half = size * 0.5f;
                Bar("top", new Vector3(0f, half, 0f), new Vector3(size + bar, bar, bar));
                Bar("bottom", new Vector3(0f, -half, 0f), new Vector3(size + bar, bar, bar));
                Bar("left", new Vector3(-half, 0f, 0f), new Vector3(bar, size, bar));
                Bar("right", new Vector3(half, 0f, 0f), new Vector3(bar, size, bar));
                _gates.Add(root.gameObject);
            }
            var cubeSize = math.max(2f, C.Track?.CubeSize ?? 9f);
            foreach (var spot in _track.Cubes)
            {
                var kind = Kind(spot.Kind);
                var color = Col(kind);
                var go = Placeholders.Block($"mystery_cube_{kind?.Id ?? "?"}", Vector3.one * cubeSize, color, collider: false);
                go.transform.SetParent(_root.transform, false);
                go.transform.position = spot.Position;
                go.transform.rotation = Quaternion.Euler(45f, 0f, 45f);
                _cubes.Add(new Cube { Spot = spot, View = go });
            }
            for (var i = 0; i < _track.Hazards.Count; i++)
            {
                var b = _track.Hazards[i];
                var go = Placeholders.Block($"race_rock_{i}", b.size, new Color(0.22f, 0.2f, 0.19f), collider: false);
                go.transform.SetParent(_root.transform, false);
                go.transform.position = b.center;
            }
        }

        static Color Col(MysteryCube k) => k == null ? Color.white : new Color(k.Color.R, k.Color.G, k.Color.B, 1f);

        static MysteryCube Kind(int i) => C.Cubes.Count == 0 ? null : C.Cubes[math.clamp(i, 0, C.Cubes.Count - 1)];

        static void Tint(GameObject root, Color color)
        {
            if (root == null) return;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            foreach (var r in root.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(block);
        }

        /// <summary>
        /// The grid: two abreast behind the line, you in the middle of the pack. Rivals get a name, a skin, a pace (rolled
        /// in the config's range, faster by danger) and a lane.
        /// </summary>
        void Grid(GameObject template, int danger)
        {
            var ai = C.Ai ?? new RaceAi();
            var rivals = math.max(0, C.Rivals);
            var total = rivals + 1;
            var mySlot = total / 2;
            var pilots = new List<RacePilot>(C.Pilots);
            for (var i = pilots.Count - 1; i > 0; i--)   // seeded shuffle: a different grid each race
            {
                var j = _rng.NextInt(i + 1);
                (pilots[i], pilots[j]) = (pilots[j], pilots[i]);
            }
            var gap = _length * 2.4f;
            var half = _track.GateSize * 0.5f;
            var aiLength = ai.Length > 0f ? ai.Length : _length;
            for (var slot = 0; slot < total; slot++)
            {
                var row = slot / 2;
                var s = _track.Length - (row + 1) * gap;
                var j = _track.SampleAt(s);
                var fwd = _track.Tangents[j];
                var lateral = (slot % 2 == 0 ? -1f : 1f) * half * 0.3f;
                var at = _track.At(s) + _track.Rights[j] * lateral;
                var r = new Racer
                {
                    Position = at, PrevPosition = at, Sample = j,
                    Rotation = Quaternion.LookRotation(fwd, Vector3.up),
                    Lane = math.clamp(lateral / half + _rng.NextFloat(-0.3f, 0.3f), -1f, 1f) * math.saturate(ai.Lane),
                };
                r.PrevRotation = r.Rotation;
                if (slot == mySlot)
                {
                    r.You = true;
                    r.Name = "You";
                    r.View = _player;
                    _me = r;
                    _you.Position = at;
                    _you.Rotation = r.Rotation;
                    _you.Stop();
                }
                else
                {
                    var pilot = pilots.Count > 0 ? pilots[_racers.Count % pilots.Count] : new RacePilot { Name = "Rival", Skin = "idolknight" };
                    r.Name = pilot.Name;
                    var go = Object.Instantiate(template, _root.transform);
                    go.name = $"Racer {pilot.Name}";
                    go.SetActive(true);
                    r.View = go.GetComponent<ShipView>();
                    r.View.Show(pilot.Skin, aiLength);
                    r.Pace = math.lerp(ai.PaceMin, math.max(ai.PaceMin, ai.PaceMax), _rng.NextFloat()) + ai.PacePerDanger * math.max(0, danger - 1);
                    r.Pace = math.max(0.3f, r.Pace);
                    r.View.Pose(r.Position, r.Rotation);
                    r.View.SetHeat(0f, 0.6f);
                }
                _racers.Add(r);
            }
            _order.AddRange(_racers);
        }

        // ---- the frame -------------------------------------------------------------------------------------------

        /// <summary>One frame, after OrbitPresenter has flown you: the countdown, the rivals, gates, cubes, mines, places.</summary>
        public void Tick(float dt)
        {
            if (_me == null) return;
            var now = Time.time;
            _me.PrevPosition = _me.Position;
            _me.Position = _you.Position;
            _me.Velocity = _you.Velocity;
            _me.Rotation = _you.Rotation;

            if (_clock < 0f)
            {
                var before = Mathf.CeilToInt(_countdown);
                _countdown -= dt;
                var after = Mathf.CeilToInt(_countdown);
                if (after != before && after > 0) Banner(after.ToString(), 0.9f);
                _state.Countdown.Value = math.max(0f, _countdown);
                if (_countdown <= 0f)
                {
                    _clock = 0f;
                    _state.Countdown.Value = 0f;
                    foreach (var r in _racers) r.LapStart = 0f;
                    Banner("GO!", 1.2f);
                    Debug.Log("[Race] go");
                }
                PoseRivals(1f);
                Publish(true);
                return;
            }
            if (!_over) _clock += dt;

            // You: where you are on the line, the gate you're after.
            _me.Sample = _track.Nearest(_me.Position, _me.Sample);
            if (_me.Finish == 0) PlayerGates();

            // The rivals, at a fixed rate (drawn between steps), as the rogues.
            const float h = 1f / 60f;
            _simAcc = math.min(_simAcc + dt, h * 5f);
            while (_simAcc >= h)
            {
                _simAcc -= h;
                foreach (var r in _racers)
                {
                    if (r.You) continue;
                    r.PrevPosition = r.Position;
                    r.PrevRotation = r.Rotation;
                }
                foreach (var r in _racers) if (!r.You) Fly(r, h);
                Collide(h);
            }
            PoseRivals(math.saturate(_simAcc / h));

            Cubes(now);
            Mines(now);
            Place();
            Finishing(now);

            var spin = Quaternion.Euler(0f, 90f * dt, 0f);
            foreach (var c in _cubes)
                if (!c.Taken) c.View.transform.rotation = spin * c.View.transform.rotation;
            _flashes.Tick(dt);

            _stateAt -= dt;
            if (_stateAt <= 0f)
            {
                _stateAt = 0.1f;
                Publish(false);
            }
        }

        void PoseRivals(float alpha)
        {
            foreach (var r in _racers)
            {
                if (r.You) continue;
                r.View.Pose(Vector3.Lerp(r.PrevPosition, r.Position, alpha), Quaternion.Slerp(r.PrevRotation, r.Rotation, alpha));
                var spin = r.Flight.Spin / math.max(10f, C.Ai?.TurnRate ?? 75f);
                var boosting = Time.time < r.BoostUntil;
                r.View.SetThrust(new Vector3(0f, 0.2f, math.min(1.4f, r.Throttle)),
                                 new Vector3(math.clamp(spin.x, -1f, 1f), math.clamp(spin.y, -1f, 1f), math.clamp(spin.z * 0.4f, -1f, 1f)),
                                 boosting ? 1.8f : 1.2f);
            }
        }

        /// <summary>Your gates: crossing the next gate's plane inside its frame passes it; outside, a warning.</summary>
        void PlayerGates()
        {
            var gate = _track.Gates[_me.NextGate];
            var a = Vector3.Dot(_me.PrevPosition - gate.Position, gate.Forward);
            var b = Vector3.Dot(_me.Position - gate.Position, gate.Forward);
            if (a < 0f && b >= 0f)
            {
                var f = a / (a - b);
                var cross = Vector3.Lerp(_me.PrevPosition, _me.Position, f);
                var off = cross - gate.Position;
                var half = _track.GateSize * 0.5f * 1.15f;
                if (math.abs(Vector3.Dot(off, gate.Right)) <= half && math.abs(Vector3.Dot(off, gate.Up)) <= half) Pass(_me);
                else if (off.magnitude < _track.GateSize * 2.5f) Banner("MISSED GATE - go back through", 1.6f);
            }
            // Wrong way: flying against the line at speed.
            var along = Vector3.Dot(_me.Velocity, _track.Tangents[_me.Sample]);
            if (along < -15f && Time.time > _bannerUntil) Banner("WRONG WAY", 0.4f);
        }

        /// <summary>Through a gate: the next one; through the line, a lap (past the last lap: finished).</summary>
        void Pass(Racer r)
        {
            var wasLine = r.NextGate == 0;
            r.GatesPassed++;
            r.NextGate = (r.NextGate + 1) % _track.Gates.Count;
            if (!wasLine) return;
            var lapTime = _clock - r.LapStart;
            r.LapStart = _clock;
            if (r.You && lapTime > 1f && (_bestLap <= 0f || lapTime < _bestLap)) _bestLap = lapTime;
            r.Lap++;
            if (r.Lap > _laps) Finish(r, false);
            else if (r.You)
            {
                Banner(r.Lap == _laps ? "FINAL LAP" : $"LAP {r.Lap}", 1.4f);
                Debug.Log($"[Race] lap {r.Lap - 1} in {lapTime:0.0} s");
            }
        }

        // ---- the rivals ------------------------------------------------------------------------------------------

        /// <summary>
        /// A rival: the line `lookahead` m on (in its lane), or a cube it's after; off the others' paths and the rocks; at
        /// its pace (rubber band, effects); flown like a rogue. Its gates count as it passes them along the line.
        /// </summary>
        void Fly(Racer r, float dt)
        {
            var ai = C.Ai ?? new RaceAi();
            var now = Time.time;
            var length = ai.Length > 0f ? ai.Length : _length;
            var s = _track.Distances[r.Sample];
            var lookahead = math.max(40f, ai.Lookahead);
            var ahead = s + lookahead;
            var j = _track.SampleAt(ahead);
            var goal = _track.At(ahead) + _track.Rights[j] * (r.Lane * _track.GateSize * 0.5f);
            // A cube in the next row: some go for one (picked once per row).
            if (r.Finish == 0 && ai.CubeSeek > 0f) Seek(r, s, ref goal);

            // Pace: a share of the baseline boosted top speed, rubber-banded on you, then its effects.
            var speed = _baseTop * r.Pace;
            if (_me.Finish == 0 && ai.RubberBand > 0f)
            {
                var gap = (Progress(_me) - Progress(r)) * _track.Spacing;   // + : you're ahead of it
                speed *= 1f + math.clamp(gap / 1000f, -1f, 1f) * ai.RubberBand;
            }
            if (now < r.BoostUntil) speed *= r.BoostPower;
            if (now < r.SlowUntil) speed *= r.SlowPower;
            if (r.Finish != 0) speed *= 0.6f;   // a lap of honour
            if (_clock <= 0f) speed = 0f;

            var to = goal - r.Position;
            var desired = (to.sqrMagnitude > 1e-3f ? to.normalized : r.Rotation * Vector3.forward) * speed;
            var push = Vector3.zero;
            if (now >= r.PhaseUntil)
            {
                foreach (var o in _racers)
                {
                    if (o == r) continue;
                    var safe = length * (o.You ? 2f : 1.5f);
                    push += Steering.AvoidShip(r.Position - o.Position, r.Velocity - o.Velocity, safe, 1.2f);
                }
                var urgency = math.saturate(push.magnitude);
                if (urgency > 0f) desired = Vector3.Lerp(desired, push.normalized * math.max(desired.magnitude, speed), urgency * 0.7f);
                desired += _nav.Avoid(r.Position, r.Velocity.sqrMagnitude > 1f ? r.Velocity : r.Rotation * Vector3.forward * speed,
                                      length * 0.6f, 1.4f) * (speed * 2f);
            }
            r.Throttle = desired.magnitude / math.max(1f, _baseTop * 0.6f);
            if (speed <= 0f)
            {
                r.Velocity = Vector3.zero;
                return;
            }
            r.Flight.Step(ref r.Rotation, ref r.Velocity, desired, speed, math.max(20f, ai.TurnRate), speed * 1.05f, dt);
            r.Position += r.Velocity * dt;
            if (now >= r.PhaseUntil)
                Impacts.OutOfBoxes(_solid, ref r.Position, ref r.Velocity, length * 0.45f, 0.3f, 0.85f, out _);

            r.Sample = _track.Nearest(r.Position, r.Sample);
            if (r.Finish == 0)
            {
                var gate = _track.Gates[r.NextGate];
                var past = _track.Ahead(r.Sample, gate.Sample);
                if (past >= 0 && past < _track.Samples.Count / 8) Pass(r);
            }
        }

        /// <summary>A rival deciding whether to go for a cube in the coming row (once a row), and steering for it.</summary>
        void Seek(Racer r, float s, ref Vector3 goal)
        {
            var row = -1;
            var rowAt = 0f;
            foreach (var c in _cubes)
            {
                var d = _track.Wrap(c.Spot.Distance - s);
                if (d > 20f && d < 450f) { row = c.Spot.AfterGate; rowAt = d; break; }
            }
            if (row < 0) { r.SeekRow = -1; return; }
            if (r.SeekRow != row)
            {
                r.SeekRow = row;
                r.SeekAt = Vector3.zero;
                if (_rng.NextFloat() < (C.Ai?.CubeSeek ?? 0f))
                {
                    var options = new List<Cube>();
                    foreach (var c in _cubes) if (c.Spot.AfterGate == row && !c.Taken) options.Add(c);
                    if (options.Count > 0) r.SeekAt = options[_rng.NextInt(options.Count)].Spot.Position;
                }
            }
            if (r.SeekAt != Vector3.zero && rowAt < 300f) goal = r.SeekAt;
        }

        /// <summary>Ships bounce off each other (unless one's phased); you take your share of the bounce on your flight.</summary>
        void Collide(float dt)
        {
            var now = Time.time;
            var length = C.Ai?.Length > 0f ? C.Ai.Length : _length;
            for (var i = 0; i < _racers.Count; i++)
            for (var k = i + 1; k < _racers.Count; k++)
            {
                var a = _racers[i];
                var b = _racers[k];
                if (now < a.PhaseUntil || now < b.PhaseUntil) continue;
                var pa = a.You ? _you.Position : a.Position;
                var pb = b.You ? _you.Position : b.Position;
                var room = length * 0.8f;
                var gap = pa - pb;
                var d = gap.magnitude;
                if (d >= room) continue;
                var n = d > 1e-3f ? gap / d : Vector3.up;
                var va = a.You ? _you.Velocity : a.Velocity;
                var vb = b.You ? _you.Velocity : b.Velocity;
                Impacts.Bounce(ref va, ref vb, n, 1f, 1f, 0.3f, 0.9f);
                var push = (room - d) * 0.5f;
                if (a.You) { _you.Position += n * push; _you.Velocity = va; } else { a.Position += n * push; a.Velocity = va; }
                if (b.You) { _you.Position -= n * push; _you.Velocity = vb; } else { b.Position -= n * push; b.Velocity = vb; }
                if (now > a.BumpAt && (a.You || b.You))
                {
                    a.BumpAt = now + 0.3f;
                    _flashes.Flash(pb + n * (room * 0.5f), length * 0.4f, 0.2f, new Color(2.5f, 2.2f, 1.6f));
                }
            }
        }

        // ---- cubes, effects, mines -------------------------------------------------------------------------------

        void Cubes(float now)
        {
            var radius = math.max(2f, C.Track?.CubeSize ?? 9f) * 0.5f + _length * 0.45f;
            foreach (var c in _cubes)
            {
                if (c.Taken)
                {
                    if (now < c.BackAt) continue;
                    c.Taken = false;
                    c.View.SetActive(true);
                }
                foreach (var r in _racers)
                {
                    if (r.Finish != 0 || r.Dnf) continue;
                    var near = r.You ? DistanceToSegment(c.Spot.Position, r.PrevPosition, r.Position) : Vector3.Distance(c.Spot.Position, r.Position);
                    if (near > radius) continue;
                    c.Taken = true;
                    c.BackAt = now + math.max(1f, C.Track?.CubeRespawn ?? 6f);
                    c.View.SetActive(false);
                    _flashes.Flash(c.Spot.Position, radius * 2.2f, 0.35f, Col(Kind(c.Spot.Kind)));
                    Roll(r, Kind(c.Spot.Kind));
                    break;
                }
            }
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var t = ab.sqrMagnitude > 1e-6f ? math.saturate(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>A cube's roll for `r`: d20 + bonus, the colour's table (RaceDice), the effect, the feed.</summary>
        void Roll(Racer r, MysteryCube cube)
        {
            if (cube == null) return;
            var d20 = _rng.NextInt(1, 21);
            var bonus = RaceDice.Bonus(C.Roll, _luck, r.You, r.Place);
            var total = d20 + bonus;
            var o = RaceDice.Pick(cube, C.Fumble, d20, total);
            var said = o == null ? "nothing" : Apply(r, o);
            var hex = ColorUtility.ToHtmlStringRGB(Display(Col(cube)));
            var name = o?.Name ?? "Dud";
            _state.Feed.Add($"{Clean(r.Name)}|{Clean(cube.Name)}|{hex}|{d20}|{bonus}|{total}|{Clean(name)}|{(r.You ? 1 : 0)}");
            while (_state.Feed.Count > 6) _state.Feed.RemoveAt(0);
            if (r.You)
            {
                _state.Roll.Value = $"{Clean(cube.Name)}|{hex}|{d20}|{bonus}|{total}|{Clean(name)}|{Clean(said)}";
                _state.RollSeq.Value++;
            }
            Debug.Log($"[Race] {r.Name} took a {cube.Name} cube: d20 {d20} + {bonus} = {total}: {name} ({said})");
        }

        /// <summary>HDR colours brought into 0..1 for the page (the hue kept).</summary>
        static Color Display(Color c)
        {
            var max = math.max(1f, math.max(c.r, math.max(c.g, c.b)));
            return new Color(c.r / max, c.g / max, c.b / max, 1f);
        }

        /// <summary>The effect on `r` (or, for hostile ones, on the others), and a few words on what it did.</summary>
        string Apply(Racer r, CubeOutcome o)
        {
            var now = Time.time;
            var seconds = math.max(0.1f, o.Seconds);
            switch (o.Effect)
            {
                case RaceEffect.Boost:
                {
                    var power = math.max(1f, o.Power);
                    r.BoostPower = power;
                    r.BoostUntil = now + seconds;
                    var fwd = r.Rotation * Vector3.forward;
                    var kick = fwd * (_baseTop * (power - 1f) * 0.35f);
                    if (r.You) _you.Velocity += kick;
                    else
                    {
                        r.Velocity += kick;
                        r.Flight.Speed += kick.magnitude;
                    }
                    if (r.You) Banner($"{(o.Name ?? "BOOST").ToUpperInvariant()}!", 1f);
                    return $"x{power:0.##} speed for {seconds:0.#} s";
                }
                case RaceEffect.Shield:
                {
                    var max = math.max(1, C.MaxShields);
                    r.Shields = math.min(max, r.Shields + math.max(1, o.Count));
                    return $"{r.Shields} shield charge{(r.Shields == 1 ? "" : "s")}";
                }
                case RaceEffect.Emp:
                {
                    var radius = math.max(10f, o.Radius);
                    var hit = 0;
                    foreach (var other in _racers)
                    {
                        if (other == r || other.Finish != 0) continue;
                        if (Vector3.Distance(other.Position, r.Position) > radius) continue;
                        if (Hit(other, o, r)) hit++;
                    }
                    _flashes.Flash(r.Position, radius * 0.6f, 0.5f, new Color(0.6f, 1.6f, 3.4f));
                    return hit == 0 ? "nobody in range" : $"{hit} slowed";
                }
                case RaceEffect.Zap:
                {
                    var target = o.Leader ? Leader(r) : Ahead(r);
                    if (target == null) return "nobody ahead";
                    var landed = Hit(target, o, r);
                    _flashes.Flash(target.Position, _length * 1.5f, 0.4f, new Color(1.2f, 1.8f, 3.6f));
                    return landed ? $"{target.Name} slowed" : $"{target.Name} blocked it";
                }
                case RaceEffect.Mine:
                {
                    var at = r.Position - r.Rotation * Vector3.forward * (_length * 2f);
                    var view = Placeholders.Block("race_mine", Vector3.one * (_length * 0.35f), new Color(3f, 0.4f, 0.3f), collider: false);
                    view.transform.SetParent(_root.transform, false);
                    view.transform.position = at;
                    Layers.Set(view, Layers.Local);
                    _mines.Add(new Mine { Position = at, Owner = r, Effect = o, ArmedAt = now + 0.8f, GoneAt = now + 40f, View = view });
                    return "a mine dropped behind";
                }
                case RaceEffect.Phase:
                    r.PhaseUntil = now + seconds;
                    if (r.You) Banner("PHASE", 1f);
                    return $"ghosting for {seconds:0.#} s";
                case RaceEffect.Warp:
                {
                    var gates = Warp(r, math.max(1, o.Count));
                    if (r.You) Banner("WARP", 1f);
                    return $"jumped {gates} gate{(gates == 1 ? "" : "s")}";
                }
                case RaceEffect.Stall:
                    Slow(r, o.Power, seconds);
                    if (r.You) Banner((o.Name ?? "STALL").ToUpperInvariant(), 1.2f);
                    return $"slowed for {seconds:0.#} s";
                case RaceEffect.Swap:
                {
                    var target = Ahead(r);
                    if (target == null) return "nobody ahead";
                    if (!Shielded(target, r)) { Swap(r, target); return $"swapped with {target.Name}"; }
                    return $"{target.Name} blocked it";
                }
                default:
                    return "it fizzled";
            }
        }

        /// <summary>A hostile effect on `target`: phase ignores it, a shield charge soaks it, else it's slowed.</summary>
        bool Hit(Racer target, CubeOutcome o, Racer from)
        {
            if (Shielded(target, from)) return false;
            Slow(target, o.Power, math.max(0.1f, o.Seconds));
            if (target.You) Banner($"{(o.Name ?? "HIT").ToUpperInvariant()} from {from.Name}", 1.4f);
            return true;
        }

        bool Shielded(Racer target, Racer from)
        {
            if (Time.time < target.PhaseUntil) return true;
            if (target.Shields <= 0) return false;
            target.Shields--;
            _flashes.Flash(target.Position, _length * 1.4f, 0.3f, new Color(0.5f, 2.4f, 1.4f));
            if (target.You) Banner($"SHIELD blocked {from.Name}", 1.2f);
            return true;
        }

        void Slow(Racer r, float power, float seconds)
        {
            power = math.clamp(power, 0.05f, 1f);
            r.SlowPower = power;
            r.SlowUntil = Time.time + seconds;
            if (r.You) _you.Velocity *= power;
            else
            {
                r.Velocity *= power;
                r.Flight.Speed *= power;
            }
        }

        /// <summary>`r` jumps `count` gates on (not past the line on its last lap): just through the last of them.</summary>
        int Warp(Racer r, int count)
        {
            var jumped = 0;
            for (var i = 0; i < count; i++)
            {
                if (r.NextGate == 0 && r.Lap >= _laps) break;   // the finish is earned
                Pass(r);
                jumped++;
            }
            if (jumped == 0 || r.Finish != 0) return jumped;
            var last = _track.Gates[(r.NextGate - 1 + _track.Gates.Count) % _track.Gates.Count];
            var at = last.Position + last.Forward * (_length * 1.5f) + last.Right * (r.Lane * _track.GateSize * 0.3f);
            var rot = Quaternion.LookRotation(last.Forward, Vector3.up);
            _flashes.Flash(r.Position, _length * 2f, 0.4f, new Color(3f, 2.4f, 0.6f));
            Put(r, at, rot, r.Velocity.magnitude);
            _flashes.Flash(at, _length * 2f, 0.4f, new Color(3f, 2.4f, 0.6f));
            return jumped;
        }

        /// <summary>`a` and `b` change places: where they are, which way they face, their progress.</summary>
        void Swap(Racer a, Racer b)
        {
            var (pa, ra, sa) = (a.Position, a.Rotation, a.Velocity.magnitude);
            var (pb, rb, sb) = (b.Position, b.Rotation, b.Velocity.magnitude);
            (a.NextGate, b.NextGate) = (b.NextGate, a.NextGate);
            (a.Lap, b.Lap) = (b.Lap, a.Lap);
            (a.GatesPassed, b.GatesPassed) = (b.GatesPassed, a.GatesPassed);
            Put(a, pb, rb, sb);
            Put(b, pa, ra, sa);
            _flashes.Flash(pa, _length * 2f, 0.4f, new Color(3f, 0.6f, 0.5f));
            _flashes.Flash(pb, _length * 2f, 0.4f, new Color(3f, 0.6f, 0.5f));
            if (a.You || b.You) Banner("SWAP!", 1.2f);
        }

        void Put(Racer r, Vector3 at, Quaternion rot, float speed)
        {
            r.Position = r.PrevPosition = at;
            r.Rotation = r.PrevRotation = rot;
            r.Velocity = rot * Vector3.forward * speed;
            r.Sample = _track.Nearest(at);
            if (!r.You) return;
            _you.Position = at;
            _you.Rotation = rot;
            _you.Velocity = r.Velocity;
        }

        void Mines(float now)
        {
            var reach = _length * 0.6f;
            for (var i = _mines.Count - 1; i >= 0; i--)
            {
                var m = _mines[i];
                if (m.View != null) m.View.transform.Rotate(0f, 180f * Time.deltaTime, 0f);
                var gone = now > m.GoneAt;
                if (!gone && now >= m.ArmedAt)
                    foreach (var r in _racers)
                    {
                        if (r == m.Owner || r.Finish != 0) continue;
                        if (Vector3.Distance(r.Position, m.Position) > math.max(reach, m.Effect.Radius)) continue;
                        Hit(r, m.Effect, m.Owner);
                        _flashes.Flash(m.Position, _length * 2.5f, 0.5f, new Color(3.5f, 1.2f, 0.4f));
                        gone = true;
                        break;
                    }
                if (!gone) continue;
                if (m.View != null) Object.Destroy(m.View);
                _mines.RemoveAt(i);
            }
        }

        // ---- places and the finish -------------------------------------------------------------------------------

        /// <summary>Gates passed plus the way to the next (finishers by their finish order, at the front).</summary>
        float Progress(Racer r)
        {
            if (r.Finish != 0) return 1e6f - r.Finish;
            var next = _track.Gates[r.NextGate];
            var toNext = Vector3.Distance(r.Position, next.Position);
            return r.GatesPassed + math.saturate(1f - toNext / math.max(1f, _track.Spacing));
        }

        void Place()
        {
            _order.Sort((a, b) => Progress(b).CompareTo(Progress(a)));
            for (var i = 0; i < _order.Count; i++) _order[i].Place = i + 1;
        }

        Racer Ahead(Racer r) => r.Place > 1 ? _order[r.Place - 2] : null;
        Racer Leader(Racer r) => _order.Count > 0 && _order[0] != r ? _order[0] : Ahead(r);

        void Finish(Racer r, bool dnf)
        {
            _finishers++;
            r.Finish = _finishers;
            r.FinishTime = _clock;
            r.Dnf = dnf;
            if (_winnerAt < 0f) _winnerAt = Time.time;
            Place();
            if (!r.You) return;
            _finishedAt = Time.time;
            var place = r.Place;
            var share = !dnf && place <= C.PayShares.Count ? math.max(0f, C.PayShares[place - 1]) : 0f;
            var paid = 0;
            if (!_practice && _job.Active.Value)
            {
                paid = Mathf.RoundToInt(_job.Pay.Value * share);
                _job.Pay.Value = paid;
                _job.Done.Value = paid > 0;
                if (!dnf && place - 1 < C.Xp.Count && C.Xp[place - 1] > 0) _xp.Publish(new CharacterXpGained(C.Xp[place - 1], "Race"));
            }
            var placed = dnf ? "Did not finish" : $"{Ordinal(place)} of {_racers.Count}";
            _state.Result.Value = _practice ? $"{placed} · practice, no pay"
                : paid > 0 ? $"{placed} · {paid:N0} cr at the station" : $"{placed} · no pay this time";
            _state.Payout.Value = paid;
            _state.Finished.Value = true;
            Banner(dnf ? "DID NOT FINISH" : $"FINISHED {Ordinal(place).ToUpperInvariant()}", 3f);
            Debug.Log($"[Race] you finished {placed} in {_clock:0.0} s (best lap {_bestLap:0.0} s): {paid} cr");
        }

        void Finishing(float now)
        {
            if (_me.Finish == 0 && _winnerAt > 0f && now - _winnerAt > math.max(5f, C.DnfSeconds))
                Finish(_me, true);
            if (_finishedAt > 0f && now - _finishedAt > math.max(2f, C.FinishSeconds)) ReadyToLeave = true;
        }

        /// <summary>E after the finish: straight back to the station.</summary>
        public void Leave()
        {
            if (_finishedAt > 0f && Time.time - _finishedAt > 1f) ReadyToLeave = true;
        }

        /// <summary>Lost: far off the line (four gate widths), still racing.</summary>
        public bool OffCourse => _me != null && _me.Finish == 0 && _clock >= 0f &&
                                 Vector3.Distance(_me.Position, _track.Samples[_me.Sample]) > _track.GateSize * 4f;

        /// <summary>E while off course: back at the last gate you passed, facing the next, slow.</summary>
        public void Rescue()
        {
            if (!OffCourse) return;
            var last = _track.Gates[(_me.NextGate - 1 + _track.Gates.Count) % _track.Gates.Count];
            Put(_me, last.Position - last.Forward * (_length * 1.5f), Quaternion.LookRotation(last.Forward, Vector3.up), 10f);
            Banner("BACK ON THE LINE", 1.2f);
            Debug.Log($"[Race] back on the line at gate {(_me.NextGate - 1 + _track.Gates.Count) % _track.Gates.Count}");
        }

        /// <summary>Dev: a cube rolled for you as if you'd flown through one (`kind`: its id; "" any).</summary>
        public void DevRoll(string kind = "")
        {
            MysteryCube cube = null;
            foreach (var c in C.Cubes) if (c.Id == kind) cube = c;
            cube ??= Kind(_rng.NextInt(math.max(1, C.Cubes.Count)));
            Roll(_me, cube);
        }

        /// <summary>Dev: you're through the line on your last lap now (the finish, the pay).</summary>
        public void DevFinish()
        {
            if (_me == null || _me.Finish != 0 || _clock < 0f) return;
            _me.Lap = _laps;
            _me.NextGate = 0;
            Pass(_me);
        }

        public static string Ordinal(int n)
        {
            var tail = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return n + tail;
        }

        // ---- the page --------------------------------------------------------------------------------------------

        void Banner(string text, float seconds)
        {
            _state.Banner.Value = text;
            _bannerUntil = Time.time + seconds;
        }

        void Publish(bool grid)
        {
            if (_bannerUntil > 0f && Time.time > _bannerUntil) { _bannerUntil = 0f; _state.Banner.Value = ""; }
            Place();
            _state.Place.Value = _me.Place;
            _state.Lap.Value = math.min(_laps, _me.Lap);
            _state.Gate.Value = _me.NextGate;
            _state.Time.Value = math.max(0f, _me.Finish != 0 ? _me.FinishTime : _clock);
            _state.BestLap.Value = _bestLap;
            _state.Shields.Value = _me.Shields;
            _state.Speed.Value = _you.Velocity.magnitude;
            var toGate = _track.Gates[_me.NextGate].Position - _me.Position;
            _state.NextGate.Value = toGate.magnitude;
            var local = Quaternion.Inverse(_you.Rotation) * toGate;
            _state.GateAngle.Value = math.degrees(math.atan2(local.x, local.y));
            _state.GateOff.Value = Vector3.Angle(Vector3.forward, local);
            _state.OffCourse.Value = OffCourse;
            var now = Time.time;
            var fx = new StringBuilder();
            void Fx(string name, float until)
            {
                if (now >= until) return;
                if (fx.Length > 0) fx.Append(" · ");
                fx.Append(name).Append(' ').Append((until - now).ToString("0.0")).Append(" s");
            }
            Fx("Boost", _me.BoostUntil);
            Fx("Slowed", _me.SlowUntil);
            Fx("Phase", _me.PhaseUntil);
            _state.Effects.Value = fx.ToString();

            // Standings, only when they change (the page redraws the list on each change).
            var sb = new StringBuilder();
            foreach (var r in _order) sb.Append(r.Name).Append(r.Lap).Append(r.Finish).Append(';');
            var key = sb.ToString();
            if (key != _standings)
            {
                _standings = key;
                _state.Standings.Clear();
                foreach (var r in _order)
                    _state.Standings.Add($"{r.Place}|{Clean(r.Name)}|{math.min(_laps, r.Lap)}|{(r.You ? 1 : 0)}|{(r.Finish != 0 ? r.Dnf ? 2 : 1 : 0)}");
            }

            // The next gate glows; the line stays gold.
            if (_shownGate != _me.NextGate)
            {
                if (_shownGate >= 0) Tint(_gates[_shownGate], _shownGate == 0 ? GateLine : GateIdle);
                _shownGate = _me.NextGate;
                Tint(_gates[_shownGate], _shownGate == 0 ? GateLine * 1.4f : GateNext);
            }
        }

        static string Clean(string s) => (s ?? "").Replace("|", "/");

        public void Dispose()
        {
            _over = true;
            _flashes?.Dispose();
            if (_root != null) Object.Destroy(_root);
            _state.Active.Value = false;
            _state.Banner.Value = "";
            _state.Standings.Clear();
            _state.Feed.Clear();
        }
    }
}
