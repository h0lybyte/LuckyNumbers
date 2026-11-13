using System;
using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Great shaft hoists (docs/holds.md, "Stairs, great shafts, hoists"). A shaft is tracked by its column group and
    /// band (MinePasses.GreatShaftOf) from the first region showing one of its Hoist fixtures, and kept for the visit,
    /// so the cage, its cable and the landing gates never vanish as regions stream. The cage (HoistKit, HoldKit
    /// material, its own lamp) is a moving collider that starts at the top landing. Standing on it: E rides it down to
    /// the next landing, Shift+E up; riding (FirstPersonPresenter.Climbing: it moves you with it), W/S drive it up and
    /// down and it settles at the next landing the way it was going; E / Shift+E pick the next landing down / up. It
    /// stops at a landing with its open side on the dock gateway, whose gate is up while it's there; walk off. At a
    /// landing with the cage elsewhere, E calls it. Dev: LN.send('DevWarpRequested', {to: 'shaft'}) puts you at the
    /// nearest great shaft's top landing, {to: 'stairs'} at the foot of the nearest flight of stairs.
    /// E and the prompts go through InteractionPresenter (IInteractable): E is latched and the next Tick acts on it,
    /// since riding steps the cage with it.
    /// </summary>
    [Presenter]
    public sealed class HoistPresenter : IInteractable, IStartable, ITickable, IDisposable
    {
        static readonly object BoardKey = new(), CallKey = new(), RideKey = new();

        const float OnCageHeight = 0.45f;
        const float GateLift = 2.2f, GateSpeed = 3f;
        const float LampRange = 9f, LampActiveDistance = 45f;

        sealed class Gate
        {
            public int Level;
            public Transform Root;
            public Collider Collider;
            public float Open;
        }

        sealed class Shaft
        {
            public GreatShaft Info;
            public int Top, Bottom;           // world storeys
            public float2 Centre;             // world xz
            public float Width;               // cage side
            public readonly List<int> Landings = new();   // world storeys, ascending
            public readonly Dictionary<int, Gate> Gates = new();
            public Transform Cage, Cable;
            public Light Lamp;
            public float Y, Target;           // cage floor (world y), where it's heading
            public bool Moving;
        }

        readonly HoldWorld _world;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlayerView _player;
        readonly PlayerInput _input;
        readonly HoldView _view;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly MineParams _p;
        readonly Dictionary<int3, Shaft> _shafts = new();
        IDisposable _warpSub;
        Mesh _cageMesh, _gateMesh, _cableMesh;
        Transform _root;
        bool _press, _riding;
        object _offer;        // this frame's: BoardKey, CallKey, RideKey or null
        Shaft _ride;
        int _manual, _lastDir = -1;

        static VerticalRules R => GameContent.Hold.Vertical ?? new VerticalRules();
        float Speed => R.HoistSpeed > 0f ? R.HoistSpeed : 3f;
        float Storey => _world.CellSize.y;
        float Cell => _world.CellSize.x;

        [Inject]
        public HoistPresenter(HoldWorld world, FirstPersonPresenter firstPerson, PlayerView player, PlayerInput input, HoldView view,
                              ISubscriber<DevWarpRequested> warps)
        {
            _world = world;
            _firstPerson = firstPerson;
            _player = player;
            _input = input;
            _view = view;
            _warps = warps;
            _p = MineParams.From(world.Config);
            // Now, not in Start: HoldPresenter's Start may show the first region before ours runs.
            _world.RegionShown += Shown;
        }

        public void Start()
        {
            _warpSub = _warps.Subscribe(e =>
            {
                if (e.To == "shaft") WarpToShaft();
                else if (e.To == "stairs") WarpToStairs();
            });
        }

        // ------------------------------------------------------------------------------------------- tracking

        void Shown(int3 region, HoldGrid grid)
        {
            if (!grid.Fixtures.IsCreated) return;
            foreach (var f in grid.Fixtures)
            {
                if (f.Kind != FixtureKind.Hoist || !MinePasses.GreatShaftAt(_world.Seed, region, _p, out var info)) continue;
                var shaft = Track(info);
                var mask = f.Data >> 16;
                for (var l = 0; l < _p.Levels; l++)
                    if ((mask & (1 << l)) != 0) AddLanding(shaft, region.y * _p.Levels + l);
            }
        }

        static int3 Key(in GreatShaft s) => new(s.Group.x, s.Band, s.Group.y);

        Shaft Track(in GreatShaft info)
        {
            var key = Key(info);
            if (_shafts.TryGetValue(key, out var shaft)) return shaft;
            EnsureKit();
            var min = (float2)info.WorldMin(_p) * Cell;
            shaft = new Shaft
            {
                Info = info, Top = info.TopLevel(_p), Bottom = info.BottomLevel(_p),
                Centre = min + info.Size * Cell * 0.5f, Width = info.Size * Cell - 2f * HoistKit.Inset,
            };
            for (var level = shaft.Bottom; level <= shaft.Top; level++)
                if (info.PlannedLanding(level, _p)) AddLanding(shaft, level);
            shaft.Y = shaft.Target = shaft.Top * Storey;

            var cage = new GameObject($"Hoist cage {key}");
            cage.transform.SetParent(_root, false);
            cage.AddComponent<MeshFilter>().sharedMesh = _cageMesh;
            var r = cage.AddComponent<MeshRenderer>();
            r.sharedMaterial = _view.Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach (var b in HoistKit.CageColliders(shaft.Width))
            {
                var box = cage.AddComponent<BoxCollider>();
                box.center = b.center;
                box.size = b.size;
            }
            var body = cage.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            cage.transform.rotation = HoistKit.Facing(info.Dock);
            shaft.Cage = cage.transform;

            var lamp = new GameObject("Hoist lamp").AddComponent<Light>();
            lamp.transform.SetParent(cage.transform, false);
            lamp.transform.localPosition = new Vector3(0f, HoistKit.Height - 0.3f, 0f);
            lamp.type = LightType.Point;
            var c = _world.Config;
            lamp.color = new Color(c.LanternR, c.LanternG, c.LanternB);
            lamp.range = LampRange;
            lamp.intensity = 1.5f;
            lamp.shadows = LightShadows.None;
            shaft.Lamp = lamp;

            var cable = new GameObject("Hoist cable");
            cable.transform.SetParent(_root, false);
            cable.AddComponent<MeshFilter>().sharedMesh = _cableMesh;
            var cr = cable.AddComponent<MeshRenderer>();
            cr.sharedMaterial = _view.Material;
            cr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shaft.Cable = cable.transform;

            _shafts[key] = shaft;
            Place(shaft);
            Debug.Log($"[Hoist] great shaft {key}: column {info.Region} cells {info.WorldMin(_p)} size {info.Size}, " +
                      $"storeys {shaft.Bottom}..{shaft.Top}, landings {string.Join(",", shaft.Landings)}, dock {info.Dock}");
            return shaft;
        }

        void EnsureKit()
        {
            if (_root != null) return;
            _root = new GameObject("Hoists").transform;
            var light = math.saturate(_world.Config.Ambient * 2.5f);
            var size = math.max(2, _p.GreatSize);
            _cageMesh = HoistKit.Cage(size * Cell - 2f * HoistKit.Inset, Cell, light);
            _gateMesh = HoistKit.Gate(Cell - 0.4f, Cell, light);
            _cableMesh = HoistKit.Cable(Cell, light);
        }

        void AddLanding(Shaft s, int level)
        {
            if (level < s.Bottom || level > s.Top || s.Gates.ContainsKey(level)) return;
            var i = s.Landings.BinarySearch(level);
            s.Landings.Insert(~i, level);
            var go = new GameObject($"Hoist gate {level}");
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = _gateMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _view.Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.6f, 0f);
            box.size = new Vector3(Cell - 0.36f, 1.2f, 0.12f);
            var dock = StairCell.Vec(s.Info.Dock);
            var edge = (float2)(s.Info.Region * _p.Cells + s.Info.DockEdge()) * Cell + Cell * 0.5f;   // dock edge cell centre
            var at = edge + new float2(dock.x, dock.z) * (Cell * 0.5f + 0.11f);
            go.transform.SetPositionAndRotation(new Vector3(at.x, level * Storey, at.y), Quaternion.Euler(0f, dock.x != 0 ? 90f : 0f, 0f));
            s.Gates[level] = new Gate { Level = level, Root = go.transform, Collider = box };
        }

        // ------------------------------------------------------------------------------------------- per frame

        public void Tick()
        {
            var press = _press;
            _press = false;
            var dt = Time.deltaTime;
            if (_shafts.Count == 0 || dt <= 0f) return;
            var body = _player.Body;
            var feet = (float3)body.transform.position;
            var shift = _input.Sprinting.Value;

            _offer = null;
            if (_riding) _offer = Ride(_ride, body, press, shift, dt) ? RideKey : null;
            else if (_firstPerson.Active && Nearest(feet, out var near))
            {
                if (!near.Moving && OnCage(near, feet))
                {
                    _offer = BoardKey;
                    if (press && Next(near, near.Y, shift ? 1 : -1, out var to)) Board(near, to, shift ? 1 : -1);
                }
                else if (AtLanding(near, feet, out var level) && (near.Moving || math.abs(near.Y - level * Storey) > 0.02f))
                {
                    _offer = CallKey;
                    if (press) Send(near, level);
                }
            }

            foreach (var s in _shafts.Values)
            {
                if (s != _ride || !_riding) Drive(s, dt);
                Animate(s, feet, dt);
            }
        }

        /// <summary>The tracked shaft whose column you're in or beside, within its storeys.</summary>
        bool Nearest(float3 feet, out Shaft best)
        {
            best = null;
            var bestD = float.MaxValue;
            var reach = (R.HoistCallDistance > 0f ? R.HoistCallDistance : 3.5f) + Cell;
            foreach (var s in _shafts.Values)
            {
                var level = feet.y / Storey;
                if (level < s.Bottom - 0.5f || level > s.Top + 1f) continue;
                var d = math.cmax(math.abs(feet.xz - s.Centre)) - s.Info.Size * Cell * 0.5f;
                if (d > reach || d >= bestD) continue;
                bestD = d;
                best = s;
            }
            return best != null;
        }

        bool OnCage(Shaft s, float3 feet)
        {
            var half = s.Width * 0.5f;
            return math.all(math.abs(feet.xz - s.Centre) < half) && math.abs(feet.y - s.Y) < OnCageHeight;
        }

        /// <summary>Standing on a landing of the shaft (feet at its floor, outside the footprint, within reach).</summary>
        bool AtLanding(Shaft s, float3 feet, out int level)
        {
            level = (int)math.round(feet.y / Storey);
            if (math.abs(feet.y - level * Storey) > 0.4f || !s.Landings.Contains(level)) return false;
            var d = math.cmax(math.abs(feet.xz - s.Centre)) - s.Info.Size * Cell * 0.5f;
            return d > 0f && d < (R.HoistCallDistance > 0f ? R.HoistCallDistance : 3.5f);
        }

        /// <summary>The next landing from height y going dir (+1 up, -1 down).</summary>
        bool Next(Shaft s, float y, int dir, out int level)
        {
            level = 0;
            if (dir < 0)
            {
                for (var i = s.Landings.Count - 1; i >= 0; i--)
                    if (s.Landings[i] * Storey < y - 0.05f) { level = s.Landings[i]; return true; }
            }
            else
            {
                foreach (var l in s.Landings)
                    if (l * Storey > y + 0.05f) { level = l; return true; }
            }
            return false;
        }

        void Send(Shaft s, int level)
        {
            s.Target = level * Storey;
            s.Moving = math.abs(s.Target - s.Y) > 0.001f;
            Debug.Log($"[Hoist] called to storey {level}");
        }

        void Board(Shaft s, int level, int dir)
        {
            _riding = true;
            _ride = s;
            _manual = 0;
            _lastDir = dir;
            _firstPerson.Climbing = true;
            Send(s, level);
        }

        // ---- IInteractable: board / call / (riding) retarget; E is latched for the next Tick ----------------------

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = _offer == null ? default
                : new Interaction(_offer, _offer == RideKey ? InteractionPriority.Aboard : InteractionPriority.Place, 0f);
            return _offer != null;
        }

        public string Prompt(object key) =>
            key == RideKey ? R.HoistRidePrompt ?? "W/S up/down · E next landing down · Shift+E up"
            : key == BoardKey ? R.HoistBoardPrompt ?? "E  hoist down · Shift+E up"
            : R.HoistCallPrompt ?? "E  call the hoist";

        public void Interact(object key) => _press = true;

        /// <summary>Riding: W/S drive it, E / Shift+E retarget; arriving at a landing lets you go (false).</summary>
        bool Ride(Shaft s, CharacterController body, bool press, bool shift, float dt)
        {
            var w = _input.Move.Value.Y;
            var lo = s.Bottom * Storey;
            var hi = s.Top * Storey;
            float y;
            if (math.abs(w) > 0.3f)
            {
                _manual = w > 0f ? 1 : -1;
                _lastDir = _manual;
                y = math.clamp(s.Y + _manual * Speed * dt, lo, hi);
                s.Target = y;
            }
            else
            {
                if (_manual != 0)
                {
                    _manual = 0;
                    s.Target = Next(s, s.Y + (_lastDir > 0 ? -0.06f : 0.06f), _lastDir, out var l) ? l * Storey
                        : Next(s, s.Y, -_lastDir, out l) ? l * Storey : s.Y;
                }
                if (press && Next(s, s.Y, shift ? 1 : -1, out var to)) { s.Target = to * Storey; _lastDir = shift ? 1 : -1; }
                y = Step(s.Y, s.Target, dt);
            }
            Carry(s, body, y);
            if (_manual == 0 && math.abs(s.Y - s.Target) < 0.001f)
            {
                s.Moving = false;
                _riding = false;
                _ride = null;
                _firstPerson.Climbing = false;
                Debug.Log($"[Hoist] stopped at storey {math.round(s.Y / Storey)}");
                return false;
            }
            s.Moving = true;
            return true;
        }

        float Step(float y, float target, float dt)
        {
            var gap = target - y;
            var v = math.min(Speed, math.max(0.5f, math.abs(gap) * 2.5f));
            return math.abs(gap) <= v * dt ? target : y + math.sign(gap) * v * dt;
        }

        /// <summary>The cage (and the rider) to floor height y: up, the body goes first; down, the cage does.</summary>
        void Carry(Shaft s, CharacterController body, float y)
        {
            var dy = y - s.Y;
            if (dy > 0f) body.Move(new Vector3(0f, dy, 0f));
            s.Y = y;
            Place(s);
            Physics.SyncTransforms();
            if (dy < 0f) body.Move(new Vector3(0f, dy, 0f));
        }

        void Drive(Shaft s, float dt)
        {
            if (!s.Moving) return;
            s.Y = Step(s.Y, s.Target, dt);
            Place(s);
            if (math.abs(s.Y - s.Target) < 0.001f) s.Moving = false;
        }

        void Place(Shaft s)
        {
            s.Cage.position = new Vector3(s.Centre.x, s.Y, s.Centre.y);
            var bottom = s.Y + HoistKit.Height + HoistKit.HookTop;
            var top = (s.Top + 1) * Storey - HoistKit.PulleyDrop;
            s.Cable.position = new Vector3(s.Centre.x, bottom, s.Centre.y);
            s.Cable.localScale = new Vector3(0.05f, math.max(0.01f, top - bottom), 0.05f);
        }

        void Animate(Shaft s, float3 feet, float dt)
        {
            foreach (var g in s.Gates.Values)
            {
                var here = !s.Moving && math.abs(s.Y - g.Level * Storey) < 0.02f;
                g.Open = math.saturate(g.Open + (here ? dt : -dt) * GateSpeed / GateLift);
                var p = g.Root.position;
                p.y = g.Level * Storey + g.Open * GateLift;
                g.Root.position = p;
                g.Collider.enabled = g.Open < 0.5f;
            }
            s.Lamp.enabled = math.distance(feet, (float3)s.Cage.position) < LampActiveDistance;
        }

        // ------------------------------------------------------------------------------------------- dev warps

        void WarpToShaft()
        {
            var me = (float3)_player.Body.transform.position;
            var region = _world.RegionOf(me);
            var group = new int2((int)math.floor(region.x / (float)_p.GreatSpacing), (int)math.floor(region.z / (float)_p.GreatSpacing));
            var found = false;
            var best = default(GreatShaft);
            var bestD = float.MaxValue;
            for (var band = 0; band <= math.max(0, -region.y / _p.GreatBand); band++)
            for (var dz = -2; dz <= 2; dz++)
            for (var dx = -2; dx <= 2; dx++)
            {
                if (!MinePasses.GreatShaftOf(_world.Seed, group + new int2(dx, dz), band, _p, out var s)) continue;
                var at = (float3)new int3(s.WorldMin(_p).x, s.TopLevel(_p), s.WorldMin(_p).y) * _world.CellSize;
                var d = math.distance(at, me);
                if (d >= bestD) continue;
                bestD = d;
                best = s;
                found = true;
            }
            if (!found) { Debug.Log("[Hoist] dev warp shaft: none near"); return; }
            var ring = best.Region * _p.Cells + best.DockRing();
            var cell = new int3(ring.x, best.TopLevel(_p), ring.y);
            _world.LoadNow(_world.RegionOfCell(cell));
            var face = -StairCell.Vec(best.Dock);
            Teleport((float3)_world.CellFloor(cell) + new float3(0f, 0.05f, 0f), face);
            Debug.Log($"[Hoist] dev warp shaft: column {best.Region}, top storey {best.TopLevel(_p)}, standing at {cell}");
        }

        void WarpToStairs()
        {
            var me = (float3)_player.Body.transform.position;
            var here = _world.RegionOf(me);
            var bestD = float.MaxValue;
            var best = int3.zero;
            var bestDir = int3.zero;
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_world.TryGrid(here + new int3(dx, dy, dz), out var g)) continue;
                for (var i = 0; i < g.Cells.Length; i++)
                {
                    var cell = g.Cells[i];
                    if (!StairCell.IsStep(cell) || StairCell.Index(cell) != 0) continue;
                    var local = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                    var d = StairCell.Vec(StairCell.Dir(cell));
                    var foot = g.ToWorld(local - d);
                    var dist = math.distance((float3)_world.CellFloor(foot), me);
                    if (dist >= bestD) continue;
                    bestD = dist;
                    best = foot;
                    bestDir = d;
                }
            }
            if (bestD == float.MaxValue) { Debug.Log("[Hoist] dev warp stairs: none loaded"); return; }
            Teleport((float3)_world.CellFloor(best) + new float3(0f, 0.05f, 0f), bestDir);
            Debug.Log($"[Hoist] dev warp stairs: foot at {best}");
        }

        void Teleport(float3 at, int3 face)
        {
            if (_riding) return;
            var yaw = math.degrees(math.atan2(face.x, face.z));
            var body = _player.Body;
            body.enabled = false;
            body.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, 0f);
        }

        public void Dispose()
        {
            _world.RegionShown -= Shown;
            if (_riding) _firstPerson.Climbing = false;
            _warpSub?.Dispose();
            if (_cageMesh != null) UnityEngine.Object.Destroy(_cageMesh);
            if (_gateMesh != null) UnityEngine.Object.Destroy(_gateMesh);
            if (_cableMesh != null) UnityEngine.Object.Destroy(_cableMesh);
        }
    }
}
