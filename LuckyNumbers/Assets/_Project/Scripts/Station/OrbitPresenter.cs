using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Outside the station (content/orbit.txtpb; GameScene.Orbit), from the Docking Bay's launch console: your ship
    /// just off the bay's outer wall, heading out, in your universe (the space camera parked at the station's point in
    /// orbit, turned with the flight camera, so the planet it orbits is off to the north as through its windows).
    /// The hull is the station's seeded plan (the same lucky number, so the same station) in placeholder blocks
    /// (OrbitView), scaled so your fighter at its flying size fits the bay as the docked ship does inside; it's solid.
    /// Flies like the Flight School (HoverFlight). Inside the docking beacon and slow enough, E docks: the station
    /// puts you off the ship beside it (PlayerVoyage.docked).
    /// </summary>
    public sealed class OrbitPresenter : IStartable, ITickable, ILateTickable, IDisposable
    {
        readonly PlanetView _view;
        readonly OrbitView _orbit;
        readonly PlayerInput _input;
        readonly PlayerShip _playerShip;
        readonly PlayerVoyage _voyage;
        readonly ISpaceDirector _space;
        readonly StationScene _config;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly List<IDisposable> _subscriptions = new();
        readonly CancellationTokenSource _cts = new();
        readonly HoverFlight _flight = new();
        readonly List<Bounds> _hull = new();

        ShipView _ship;
        ShipStats _stats = new();
        bool _ready, _leaving, _interact, _inSpace;
        float _fade = 1f, _readoutAt, _scale = 1f, _pulse;
        Vector3 _camPos, _beacon, _out, _centre;
        Vector3 _orbitPosition;
        Quaternion _stationInSpace = Quaternion.identity;

        static OrbitScene O => GameContent.Orbit;
        static float Length => GameContent.School.FighterLength;   // the ship at its flying size

        [Inject]
        public OrbitPresenter(PlanetView view, OrbitView orbit, PlayerInput input, PlayerShip playerShip,
                              PlayerVoyage voyage, ISpaceDirector space, StationScene config,
                              ISubscriber<InputActionPerformed> actions, IPublisher<SceneChangeRequested> scenes)
        {
            _view = view;
            _orbit = orbit;
            _input = input;
            _playerShip = playerShip;
            _voyage = voyage;
            _space = space;
            _config = config;
            _actions = actions;
            _scenes = scenes;
        }

        public void Start()
        {
            _view.Bind();
            _view.Fade(1f);
            _view.Dialogue(null, null, 0);
            _view.MapHint(null);
            _view.Hint(O.Hint, 1f);
            _view.Wreck.Hide();
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _interact = true;
            }));
            var skin = PlayerShipSkin.Current(_playerShip);
            _stats = PlayerShipStats.Of(skin);
            _ship = _view.Fighter;
            _ship.Show(skin, Length);
            _view.FlightCamera.gameObject.SetActive(true);
            SetUp(_cts.Token).Forget();
        }

        /// <summary>Wait for the universe (its seed builds the station), then the hull, the beacon and the ship.</summary>
        async UniTaskVoid SetUp(CancellationToken ct)
        {
            while (_space.Worlds.Count == 0) await UniTask.Yield(ct);
            var layout = _config.Generator != null && _config.Generator.Enabled
                ? LayoutGenerator.Generate(_config, _space.Seed)
                : StationLayout.FromContent(_config);
            BuildHull(layout);
            PlaceInSpace(ct).Forget();

            // Backed out of the bay: a little way off the beacon, nose to the station (you see it, and where to come back).
            _flight.Position = _beacon + _out * (Length * math.max(0f, O.StartDistance));
            _flight.Rotation = Quaternion.LookRotation(-_out, Vector3.up);
            _flight.Stop();
            _camPos = _flight.Position + _out * (Length * O.ChaseDistance) + Vector3.up * (Length * O.ChaseHeight);
            _ship.Pose(_flight.Position, _flight.Rotation);
            _ready = true;
            Debug.Log($"[Orbit] ready: {_stats.Name} off the bay of station {_space.Seed}, {_hull.Count} hull blocks"); // marker for ln.py webshot
        }

        /// <summary>
        /// The plan in blocks: every walkable cell and the walls round it, merged into rectangles (row runs, stacked
        /// while they line up), scaled to flying size. The beacon goes off the outer wall west of the ship's dock.
        /// </summary>
        void BuildHull(StationLayout layout)
        {
            var plan = layout.Plan;
            var depth = plan.Count;
            var width = 0;
            foreach (var r in plan) width = Math.Max(width, r.Length);
            bool Open(int x, int y) => y >= 0 && y < depth && x >= 0 && x < plan[y].Length && plan[y][x] != '#';
            var solid = new bool[width, depth];
            for (var y = 0; y < depth; y++)
            for (var x = 0; x < width; x++)
            {
                var any = false;
                for (var dy = -1; dy <= 1 && !any; dy++)
                for (var dx = -1; dx <= 1 && !any; dx++)
                    any = Open(x + dx, y + dy);
                solid[x, y] = any;
            }

            var dockLength = layout.Dock != null && layout.Dock.Length > 0f ? layout.Dock.Length : 9f;
            _scale = Length / dockLength;
            var cell = StationPlan.Cell * _scale;
            var height = math.max(1f, O.HullHeight) * _scale;
            // Plan cell (x, row) -> world: x east, z north (row 0 is the north edge), as StationPlan.PlanToWorld.
            Bounds Rect(int x0, int x1, int r0, int r1)
            {
                var west = x0 * cell;
                var east = (x1 + 1) * cell;
                var north = (depth - r0) * cell;
                var south = (depth - 1 - r1) * cell;
                return new Bounds(new Vector3((west + east) / 2f, height / 2f - cell * 0.5f, (north + south) / 2f),
                                  new Vector3(east - west, height, north - south));
            }

            _hull.Clear();
            var open = new Dictionary<(int, int), (int r0, int r1)>();   // run (x0, x1) -> rows so far
            for (var y = 0; y <= depth; y++)
            {
                var runs = new HashSet<(int, int)>();
                for (var x = 0; y < depth && x < width;)
                {
                    if (!solid[x, y]) { x++; continue; }
                    var x0 = x;
                    while (x < width && solid[x, y]) x++;
                    runs.Add((x0, x - 1));
                }
                foreach (var key in new List<(int, int)>(open.Keys))
                {
                    if (runs.Contains(key)) continue;
                    var (r0, r1) = open[key];
                    _hull.Add(Rect(key.Item1, key.Item2, r0, r1));
                    open.Remove(key);
                }
                foreach (var run in runs)
                    open[run] = open.TryGetValue(run, out var rows) ? (rows.r0, y) : (y, y);
            }

            // The bay's outer wall: west from the ship's dock along its row to the edge of the hull.
            var dockCol = layout.Dock != null ? Mathf.RoundToInt(layout.Dock.Col) : width / 2;
            var dockRow = layout.Dock != null ? Mathf.RoundToInt(layout.Dock.Row) : depth / 2;
            var edge = Math.Clamp(dockCol, 0, width - 1);
            while (edge > 0 && solid[edge - 1, Math.Clamp(dockRow, 0, depth - 1)]) edge--;
            var wallX = edge * cell;
            var z = (depth - 1 - dockRow + 0.5f) * cell;
            _out = Vector3.left;
            var size = Length * math.max(0.5f, O.DockRadius) * 2f;
            _beacon = new Vector3(wallX - Length * math.max(0.5f, O.DockOffset), height / 2f - cell * 0.5f, z);
            var trims = new List<Bounds>
            {
                // A lit panel on the wall behind the beacon: where the bay is.
                new(new Vector3(wallX - cell * 0.05f, _beacon.y, z), new Vector3(cell * 0.1f, size * 1.15f, size * 1.15f)),
            };
            _orbit.BuildHull(_hull, trims);
            _orbit.Beacon(_beacon, _out, size);
            _centre = new Vector3(width * cell / 2f, 0f, depth * cell / 2f);
        }

        /// <summary>The space camera at the station's point in orbit (as StationPresenter: sunward, its north to the body).</summary>
        async UniTaskVoid PlaceInSpace(CancellationToken ct)
        {
            var worlds = _space.Worlds;
            var target = string.IsNullOrEmpty(_config.Orbit) ? SpacePresenter.CrashSite(worlds, _space.Seed) : _config.Orbit;
            float3 at;
            float diameter;
            while (!_space.TryGetBody(target, out at, out diameter)) await UniTask.Yield(ct);
            _space.TakeCamera();
            var toSun = _space.TryGetBody("sun", out var sun, out _) ? math.normalizesafe(sun - at, new float3(1, 0, 0)) : new float3(1, 0, 0);
            var side = math.normalizesafe(math.cross(new float3(0, 1, 0), toSun), new float3(0, 0, 1));
            var away = math.normalize(toSun * 0.6f + side * 0.75f + new float3(0, 0.2f, 0));
            _orbitPosition = (Vector3)(at + away * diameter * math.max(0.6f, _config.OrbitDiameters));
            _stationInSpace = Quaternion.LookRotation((Vector3)(at - (float3)_orbitPosition), Vector3.up);
            _space.Camera.nearClipPlane = math.max(0.001f, diameter * 0.01f);
            _inSpace = true;
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            var interact = _interact;
            _interact = false;
            _fade = Mathf.MoveTowards(_fade, _leaving || !_ready ? 1f : 0f, dt / (_leaving ? 0.5f : 0.8f));
            _view.Fade(_fade);
            if (!_ready) return;

            if (!_leaving)
            {
                _flight.Step(_input, dt, _stats.Speed, _stats.Agility, level: false, _ship);
                KeepOutOfHull();
            }
            _ship.Pose(_flight.Position, _flight.Rotation);

            // Docking: inside the beacon and slow enough, E docks.
            var inside = Vector3.Distance(_flight.Position, _beacon) < Length * math.max(0.5f, O.DockRadius);
            var speed = _flight.Velocity.magnitude;
            var slow = speed <= HoverFlight.TopSpeed(math.max(0.1f, _stats.Speed)) * math.max(0.01f, O.DockSpeed);
            _pulse = Mathf.MoveTowards(_pulse, inside ? 1f : 0f, dt * 3f);
            _orbit.BeaconScale(1f + 0.06f * _pulse * Mathf.Sin(Time.time * 6f));
            string message = null;
            if (inside) message = slow ? O.DockPrompt : O.SlowPrompt;
            else if (Vector3.Distance(_flight.Position, _centre) > Length * math.max(5f, O.Range)) message = "Far out: turn back to the station";
            if (inside && slow && interact && !_leaving) Dock().Forget();

            if (Time.time >= _readoutAt || _leaving)
            {
                _readoutAt = Time.time + math.max(0.05f, O.ReadoutSeconds);
                var toBeacon = Vector3.Distance(_flight.Position, _beacon);
                _view.Flight($"{O.Title}   {_stats.Name}   {speed:0} m/s   docking beacon {toBeacon:0} m", _leaving ? null : message);
            }
        }

        /// <summary>The hull is solid: a sphere round the ship is pushed out of every block it's in, losing speed into it.</summary>
        void KeepOutOfHull()
        {
            var v = _flight.Velocity;
            _flight.Position = OutOfHull(_flight.Position, Length * 0.35f, ref v);
            _flight.Velocity = v;
        }

        /// <summary>`p` pushed out of every hull block by `radius` (along its shallowest side); `v` loses its speed into them.</summary>
        Vector3 OutOfHull(Vector3 p, float radius, ref Vector3 v)
        {
            foreach (var b in _hull)
            {
                var e = b.extents + Vector3.one * radius;
                var d = p - b.center;
                var pen = new Vector3(e.x - Mathf.Abs(d.x), e.y - Mathf.Abs(d.y), e.z - Mathf.Abs(d.z));
                if (pen.x <= 0f || pen.y <= 0f || pen.z <= 0f) continue;
                if (pen.x <= pen.y && pen.x <= pen.z) { p.x += Mathf.Sign(d.x) * pen.x; if (v.x * d.x < 0f) v.x = 0f; }
                else if (pen.y <= pen.z) { p.y += Mathf.Sign(d.y) * pen.y; if (v.y * d.y < 0f) v.y = 0f; }
                else { p.z += Mathf.Sign(d.z) * pen.z; if (v.z * d.z < 0f) v.z = 0f; }
            }
            return p;
        }

        /// <summary>Chase camera, then the universe seen from the station's point in orbit, turned the same way.</summary>
        public void LateTick()
        {
            var dt = Time.deltaTime;
            var cam = _view.FlightCamera.transform;
            if (_ready)
            {
                var fwd = _flight.Rotation * Vector3.forward;
                var up = _flight.Rotation * Vector3.up;
                var want = _flight.Position - fwd * (Length * O.ChaseDistance) + up * (Length * O.ChaseHeight);
                _camPos = Vector3.Lerp(_camPos, want, 1f - math.exp(-6f * dt));
                var none = Vector3.zero;
                _camPos = OutOfHull(_camPos, 2f, ref none);   // the hull is opaque: the camera stays outside it
                var look = _flight.Position + fwd * Length - _camPos;
                if (look.sqrMagnitude < 1e-4f) look = fwd;
                cam.SetPositionAndRotation(_camPos, Quaternion.LookRotation(look, up));
            }
            if (_inSpace)
            {
                var space = _space.Camera;
                space.transform.SetPositionAndRotation(_orbitPosition, _stationInSpace * cam.rotation);
                space.fieldOfView = _view.FlightCamera.fieldOfView;
            }
        }

        async UniTaskVoid Dock()
        {
            _leaving = true;
            _flight.Stop();
            _ship.EnginesOff();
            PointerLock.Release();
            _view.Hint("", 0f);
            Debug.Log("[Orbit] docked: back to the station");
            await UniTask.Delay(TimeSpan.FromSeconds(0.6), cancellationToken: _cts.Token);
            _voyage.Arrival.Value = StationArrival.Orbit;   // the station puts you off the ship beside it
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
