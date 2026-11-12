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
using Object = UnityEngine.Object;

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
    ///
    /// Your guns work out here (click, as on the planet), and space combat runs (Dogfight, content/dogfight.txtpb):
    /// on a satellite job (JobKind.Uplink) the relay sits out from the bay and the rogues come in waves while it
    /// uploads; docking with it done gets you paid (the station's homecoming). Dev: ?ln-dogfight starts a practice
    /// uplink with no job. Hull at 0: the memory restore (your chip wakes you at the station, the rebuild billed,
    /// into debt if need be: StationArrival.Restored).
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
        readonly CharacterSheet _sheet;
        readonly PlayerJob _job;
        readonly DogfightState _dogfight;
        readonly IInventory _pack;
        readonly IPublisher<CombatRoll> _rolls;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly ISubscriber<DevWarpRequested> _warps;
        ShipGuns _guns;
        Dogfight _fight;
        GameObject _rogueTemplate;
        bool _lockClick, _canFire, _destroyed;

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
                              ISubscriber<InputActionPerformed> actions, IPublisher<SceneChangeRequested> scenes,
                              CharacterSheet sheet, PlayerJob job, DogfightState dogfight, IInventory pack,
                              IPublisher<CombatRoll> rolls, IPublisher<CharacterXpGained> xp, ISubscriber<DevWarpRequested> warps)
        {
            _sheet = sheet;
            _job = job;
            _dogfight = dogfight;
            _pack = pack;
            _rolls = rolls;
            _xp = xp;
            _warps = warps;
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
            _view.Hint(Controls.Pick(O.Hint, O.TouchHint), 1f);
            _view.Wreck.Hide();
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _interact = true;
            }));
            var skin = PlayerShipSkin.Current(_playerShip);
            _stats = PlayerShipStats.Of(_playerShip);   // with your upgrades
            _ship = _view.Fighter;
            // The rogues are copies of the fighter, taken before it's shown (so no flames or gear come along).
            _rogueTemplate = Object.Instantiate(_ship.gameObject);
            _rogueTemplate.name = "Rogue template";
            _rogueTemplate.SetActive(false);
            Layers.Set(_rogueTemplate, Layers.Local);
            _ship.Show(skin, Length);
            Layers.Set(_ship.gameObject, Layers.Local);   // the flight camera's only (PlaceInSpace)
            _guns = new ShipGuns(_ship) { Layer = Layers.Local };
            _guns.Arm(_stats);
            // Dev (the page's inspector): "rogues" sends one at you, dead ahead (so you can try your guns at once).
            _subscriptions.Add(_warps.Subscribe(e =>
            {
                if (e.To == "rogues" && _fight != null && !_fight.Destroyed)
                    _fight.SpawnWave(1, 2, _flight.Position + _flight.Rotation * Vector3.forward * 320f, 0f, 30f);
            }));
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
            Layers.Set(_orbit.gameObject, Layers.Local);
            PlaceInSpace(ct).Forget();

            // Backed out of the bay: a little way off the beacon, nose to the station (you see it, and where to come back).
            _flight.Position = _beacon + _out * (Length * math.max(0f, O.StartDistance));
            _flight.Rotation = Quaternion.LookRotation(-_out, Vector3.up);
            _flight.Stop();
            _camPos = _flight.Position + _out * (Length * O.ChaseDistance) + Vector3.up * (Length * O.ChaseHeight);
            _ship.Pose(_flight.Position, _flight.Pose);
            Fight();
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
            // The universe only: the hull, ships and bolts near the origin are the flight camera's (Layers.Local).
            _space.Camera.cullingMask &= ~(1 << Layers.Local);
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

            if (!_leaving && !_destroyed)
            {
                _flight.Step(_input, dt, _stats.Speed, _stats.Agility, level: false, _ship);
                KeepOutOfHull();
                Shoot();
            }
            if (!_destroyed) _ship.Pose(_flight.Position, _flight.Pose);
            if (_fight != null)
            {
                _fight.Tick(dt, _flight.Position, _flight.Velocity);
                if (_fight.Destroyed && !_destroyed) Restore().Forget();
            }
            Physics.SyncTransforms();   // the hitboxes where the ships are now, for this frame's bolts
            _guns?.Tick(dt);

            // Docking: inside the beacon and slow enough, E docks.
            var inside = Vector3.Distance(_flight.Position, _beacon) < Length * math.max(0.5f, O.DockRadius);
            var speed = _flight.Velocity.magnitude;
            var slow = speed <= HoverFlight.TopSpeed(math.max(0.1f, _stats.Speed)) * math.max(0.01f, O.DockSpeed);
            _pulse = Mathf.MoveTowards(_pulse, inside ? 1f : 0f, dt * 3f);
            _orbit.BeaconScale(1f + 0.06f * _pulse * Mathf.Sin(Time.time * 6f));
            string message = null;
            if (inside) message = slow ? O.DockPrompt : O.SlowPrompt;
            else if (Vector3.Distance(_flight.Position, _centre) > Length * math.max(5f, O.Range)) message = "Far out: turn back to the station";
            if (inside && slow && interact && !_leaving && !_destroyed) Dock().Forget();

            if (!_destroyed && (Time.time >= _readoutAt || _leaving))
            {
                _readoutAt = Time.time + math.max(0.05f, O.ReadoutSeconds);
                var toBeacon = Vector3.Distance(_flight.Position, _beacon);
                var defence = _fight == null ? "" : $"   SHIELD {_dogfight.Shield.Value:0}/{_dogfight.ShieldMax.Value:0}   HULL {_dogfight.Hull.Value:0}/{_dogfight.HullMax.Value:0}";
                var uplink = _dogfight.UplinkJob.Value ? $"\n{_dogfight.Objective.Value}  ({_dogfight.Uplink.Value * 100f:0}%, satellite {_dogfight.SatelliteDistance.Value:0} m)" : "";
                _view.Flight($"{O.Title}   {_stats.Name}   {speed:0} m/s   docking beacon {toBeacon:0} m{defence}{uplink}",
                             _leaving || _destroyed ? null : message);
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
            // The station puts you off the ship beside it; on a job, Dispatch settles it (the homecoming).
            _voyage.Arrival.Value = StationArrival.Orbit;
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        // ---- space combat ----------------------------------------------------------------------------------------

        static bool DevDogfight => (Application.absoluteURL ?? "").Contains("ln-dogfight");

        /// <summary>The fight: always armed (rogues can find you), and on a satellite job (or ?ln-dogfight) the relay.</summary>
        void Fight()
        {
            if (GameContent.Dogfight == null) return;
            var seed = _space.Seed ^ (_job.Seed.Value * 0x9E3779B9u) ^ (uint)_voyage.Trips.Value;
            _fight = new Dogfight(_ship, _rogueTemplate, _stats, _sheet, _space.Seed, Length, _dogfight, _job, _voyage, _rolls, _xp,
                                  (p, r) => { var none = Vector3.zero; return OutOfHull(p, r, ref none); }, seed, _hull);
            _fight.Arm(_guns);
            var onJob = _job.Active.Value && _job.Kind.Value == JobKind.Uplink;
            if (!onJob && !DevDogfight) return;
            var s = GameContent.Dogfight.Satellite;
            if (s == null) return;
            // The relay: out from the bay, turned off the straight line by the job's seed (so each job's is somewhere new).
            var rng = new Unity.Mathematics.Random(seed | 1u);
            var dir = Quaternion.Euler(rng.NextFloat(-12f, 12f), rng.NextFloat(-50f, 50f), 0f) * _out;
            var at = _beacon + dir * (Length * math.max(4f, s.Distance));
            var radius = Length * math.max(1f, s.Radius);
            var view = _orbit.Satellite(at, math.max(2f, s.Size), radius);
            Layers.Set(view.gameObject, Layers.Local);
            var type = UplinkType();
            var danger = onJob ? math.max(1, _job.Danger.Value) : 2;
            var seconds = (type != null && type.UplinkSeconds > 0f ? type.UplinkSeconds : s.Seconds) + (type?.UplinkSecondsPerDanger ?? 0f) * danger;
            var waves = (type != null && type.Waves > 0 ? type.Waves : s.Waves) + danger / 2;
            var perWave = onJob ? math.max(1, _job.Goal.Value) : 2;
            _fight.StartUplink(at, view, radius, seconds, waves, perWave, danger, onJob);
        }

        static JobType UplinkType()
        {
            var jobs = GameContent.Jobs;
            if (jobs != null) foreach (var t in jobs.Types) if (t.Kind == JobKind.Uplink) return t;
            return null;
        }

        /// <summary>Guns: hold the button and the barrels fire in turn (the click that locks the pointer doesn't fire).</summary>
        void Shoot()
        {
            var pressed = _input.PointerPressed.Value;
            var locked = PointerLock.Locked;
            if (!pressed && _lockClick) { _lockClick = false; _canFire = true; }
            if (pressed && !locked && !_canFire) _lockClick = true;
            if (locked && !pressed) _canFire = true;
            if (pressed && _canFire) _guns?.TryFire(Vector3.Dot(_flight.Velocity, _flight.Rotation * Vector3.forward));
        }

        /// <summary>
        /// Your hull's gone: the ship blows apart, the memory chip uploads (the page's card), and you wake at the station
        /// in a new body. The bill (content/dogfight.txtpb restore): a share of the ship's worth plus the body's fee,
        /// taken even past zero (debt); what you carried is lost and the job fails (Dispatch says so).
        /// </summary>
        async UniTaskVoid Restore()
        {
            _destroyed = true;
            var r = GameContent.Dogfight.Restore ?? new MemoryRestore();
            _flight.Stop();
            _ship.Hide();
            PointerLock.Release();
            _view.Hint("", 0f);
            _view.Flight(null, r.DestroyedText);
            var worth = r.HullValue + ShipUpgrades.Spent(_playerShip);
            var bill = Mathf.RoundToInt(worth * math.max(0f, r.RebuildShare)) + math.max(0, r.Fee);
            var lost = _pack.TakeRelics();
            _voyage.Credits.Value -= bill;
            _voyage.RestoreBill.Value = bill;
            Debug.Log($"[Orbit] ship destroyed: memory restore, bill {bill} cr (worth {worth}), lost {lost.Count} relic(s); " +
                      $"{_voyage.Credits.Value} cr now");
            await UniTask.Delay(TimeSpan.FromSeconds(math.max(1f, r.Seconds)), cancellationToken: _cts.Token);
            _leaving = true;
            await UniTask.Delay(TimeSpan.FromSeconds(0.6), cancellationToken: _cts.Token);
            _voyage.Arrival.Value = StationArrival.Restored;
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        public void Dispose()
        {
            _fight?.Dispose();
            _guns?.Dispose();
            _dogfight.Destroyed.Value = false;
            if (_rogueTemplate != null) Object.Destroy(_rogueTemplate);
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
