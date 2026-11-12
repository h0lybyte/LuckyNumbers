using LuckyNumbers.Core;
using System;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// M: the planet you're on as a smooth hex globe (content/planet.txtpb map), in your real universe: the roll's
    /// sky (without the surface haze), your star where it really is (lighting the globe) and the planet's moons.
    /// The globe is turned so your crash cell points up, east along x, north along z: the same frame as the
    /// ground, so sky, sun and map all agree. Opens from straight above your own tile and pulls out; closes by
    /// zooming back into it. Drag to orbit, scroll to zoom, M (or Esc) to close. Markers: you (the pin), your landed
    /// ship, and the wreck where there is one.
    /// </summary>
    [Presenter]
    public sealed class MapPresenter : IStartable, ITickable, ILateTickable, IDisposable
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int Haze = Shader.PropertyToID("_Haze");
        enum Phase { Closed, Opening, Open, Closing }

        readonly PlanetScene _config;
        readonly MapView _map;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly IFirstPerson _firstPerson;
        readonly ICrashSite _site;
        readonly IFighter _fighter;
        readonly PlayerInput _input;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly MapState _state;
        IDisposable _subscription;
        /// <summary>Key presses since the last Tick (kept while there's no map config); Tick takes and clears them.</summary>
        [Flags] enum Press : byte { None = 0, Toggle = 1 << 0, Close = 1 << 1 }

        Press _press;
        Phase _phase;
        float _t, _distance, _fromDistance, _haze;
        Quaternion _rotation, _fromRotation;
        Vector3 _crash, _east, _north, _star;
        static readonly Vector3 WokeAt = new(0f, 0f, -1.5f);   // where you wake (CrashSitePresenter): the walk counts from here

        [Inject]
        public MapPresenter(PlanetScene config, MapView map, PlanetView view, PlayerView player, IFirstPerson firstPerson,
                            ICrashSite site, PlayerInput input, ISubscriber<InputActionPerformed> actions, MapState state,
                            IFighter fighter)
        {
            _fighter = fighter;
            _state = state;
            _config = config;
            _map = map;
            _view = view;
            _player = player;
            _firstPerson = firstPerson;
            _site = site;
            _input = input;
            _actions = actions;
        }

        public void Start()
        {
            _subscription = _actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Map) _press |= Press.Toggle;
                if (e.Action == InputAction.Cancel) _press |= Press.Close;
            });
            _map.Show(false);
            _state.Open.Value = false;   // app-wide state: don't carry a map left open in an earlier visit
        }

        public void Tick()
        {
            var config = _config.Map;
            if (config == null) return;
            var dt = Time.deltaTime;
            var press = _press;
            _press = Press.None;
            if ((press & Press.Toggle) != 0 && _phase == Phase.Closed && _firstPerson.Active && _site.Built) Open(config);
            else if (press != Press.None && _phase == Phase.Open) BeginClose();

            switch (_phase)
            {
                case Phase.Closed: return;
                case Phase.Opening:
                {
                    _t = math.min(1f, _t + dt / math.max(0.05f, config.OpenSeconds));
                    var e = 1f - math.pow(1f - _t, 3f);                       // rise fast off your tile, settle in orbit
                    _distance = math.lerp(config.ZoomFrom, config.Distance, e);
                    if (_t >= 1f) { _phase = Phase.Open; _view.MapHint(Controls.Pick(config.Hint, config.TouchHint)); }
                    break;
                }
                case Phase.Open:
                {
                    // Drag to orbit (finer up close), scroll to zoom.
                    if (_input.PointerPressed.Value)
                    {
                        var d = _input.PointerDelta.Value;
                        var k = config.DragDegreesPerPixel * (_distance - 1f) / math.max(0.1f, config.Distance - 1f);
                        _rotation = Quaternion.AngleAxis(d.X * k, _rotation * Vector3.up) *
                                    Quaternion.AngleAxis(-d.Y * k, _rotation * Vector3.right) * _rotation;
                    }
                    var scroll = _input.PointerScroll.Value;
                    if (scroll != 0f) _distance = math.clamp(_distance * math.pow(1.1f, -math.sign(scroll)), config.ZoomMin, config.ZoomMax);
                    break;
                }
                case Phase.Closing:
                {
                    _t = math.min(1f, _t + dt / math.max(0.05f, config.CloseSeconds));
                    var e = _t * _t * (3f - 2f * _t);
                    _distance = math.lerp(_fromDistance, config.ZoomFrom, e);
                    _rotation = Quaternion.Slerp(_fromRotation, AboveYou(), e);   // back over your tile, heading up
                    if (_t >= 1f) { FinishClose(); return; }
                    break;
                }
            }
            _map.Look(_rotation, _distance);
            _map.Place(OnGlobe(_view.Wreck.Pivot.position, config), Pin(config), config.CrashColor.ToColor(), config.PinColor.ToColor());   // the wreck where it lies
            _map.ShowCrash(_site.HasWreck);
            var ship = _fighter.BoardingSpot;   // beside the landed ship (null while it isn't landed)
            _map.PlaceShip(ship.HasValue ? OnGlobe(ship.Value, config) : null,
                           config.ShipColor != null ? config.ShipColor.ToColor() : new Color(0.3f, 0.9f, 1f));
        }

        public void LateTick()
        {
            // While open, the star that lights the globe (and the moons) is the map's: after the crash site sets its own.
            if (_phase != Phase.Closed) Shader.SetGlobalVector(SunPosition, new Vector4(_star.x, _star.y, _star.z, 1f));
        }

        void Open(PlanetMap config)
        {
            if (!_map.Built) Build(config);
            _phase = Phase.Opening;
            _state.Open.Value = true;
            _t = 0f;
            _firstPerson.Lock(ControlLock.Map);           // frees the pointer: drag the globe
            _player.Camera.enabled = false;               // the map camera draws instead (and the arms with it)
            if (RenderSettings.skybox != null)            // the same sky, seen from space: no atmosphere haze
            {
                _haze = RenderSettings.skybox.GetFloat(Haze);
                RenderSettings.skybox.SetFloat(Haze, 0f);
            }
            _rotation = AboveYou();
            _distance = config.ZoomFrom;
            _map.Show(true);
            _map.Look(_rotation, _distance);
            _view.MapHint("");                            // hide the crosshair and controls hint now; the map's own line comes once open
            Debug.Log($"[Map] open: {_site.Body}");
        }

        void BeginClose()
        {
            _phase = Phase.Closing;
            _t = 0f;
            _fromDistance = _distance;
            _fromRotation = _rotation;
            _view.MapHint(null);
        }

        void FinishClose()
        {
            _phase = Phase.Closed;
            _state.Open.Value = false;
            _map.Show(false);
            if (RenderSettings.skybox != null) RenderSettings.skybox.SetFloat(Haze, _haze);
            _player.Camera.enabled = true;
            _firstPerson.Unlock(ControlLock.Map);
            Debug.Log("[Map] closed");
        }

        /// <summary>Camera straight above your tile, looking down, the way you face at the top of the screen.</summary>
        Quaternion AboveYou()
        {
            var here = _map.Globe.rotation * Pin(_config.Map);
            var heading = Vector3.ProjectOnPlane(_player.Body.transform.forward, here);
            if (heading.sqrMagnitude < 1e-6f) heading = Vector3.ProjectOnPlane(Vector3.forward, here);
            return Quaternion.LookRotation(-here, heading.normalized);
        }

        /// <summary>Where you are (globe space): walking (x, z) metres from where you woke goes east/north on a great circle.</summary>
        Vector3 Pin(PlanetMap config) => OnGlobe(_player.Body.transform.position, config);

        /// <summary>A point on the ground (world), in globe space: its (x, z) metres from where you woke, east/north.</summary>
        Vector3 OnGlobe(Vector3 world, PlanetMap config)
        {
            var offset = world - WokeAt;
            var walked = new Vector2(offset.x, offset.z);
            var angle = walked.magnitude / math.max(1f, config.WalkRadiusMetres);
            if (angle < 1e-6f) return _crash;
            var toward = (_east * walked.x + _north * walked.y).normalized;
            return (_crash * math.cos(angle) + toward * math.sin(angle)).normalized;
        }

        void Build(PlanetMap config)
        {
            var started = Time.realtimeSinceStartup;
            var cells = HexSphere.Build(config.Frequency);
            var look = _site.Look;
            var offset = look != null ? new float3(look.NoiseOffset.X, look.NoiseOffset.Y, look.NoiseOffset.Z) : float3.zero;
            var land = look != null ? look.PlanetColor.ToColor() : new Color(0.5f, 0.45f, 0.4f);
            var air = look != null ? look.AtmosphereColor.ToColor() : new Color(0.4f, 0.6f, 1f);
            var sea = Color.Lerp(Color.Lerp(air, land, 0.25f), new Color(0.08f, 0.22f, 0.55f), 0.55f) * 0.6f;   // deep, bluer than the land

            // Terrain by rank, not raw noise: fBm bunches around 0.5, so spread it to an even 0..1 over the cells
            // (sea_level is then exactly the share of sea).
            var raw = new float[cells.Count];
            var order = new int[cells.Count];
            for (var i = 0; i < cells.Count; i++) { raw[i] = Terrain(cells[i].Centre, config, offset); order[i] = i; }
            Array.Sort(order, (a, b) => raw[a].CompareTo(raw[b]));
            var rank = new float[cells.Count];
            for (var r = 0; r < order.Length; r++) rank[order[r]] = r / (float)(order.Length - 1);

            var colors = new Color[cells.Count];
            var lowland = Color.Lerp(land * 0.55f, air * 0.5f, 0.2f);
            var highland = Color.Lerp(land, Color.white, 0.15f);
            for (var i = 0; i < cells.Count; i++)
            {
                var h = rank[i];
                var wet = config.SeaLevel > 0f && h < config.SeaLevel;
                var height = wet ? 0f : (h - config.SeaLevel) / math.max(0.01f, 1f - config.SeaLevel);   // 0 at the shore
                var c = wet ? Color.Lerp(sea * 0.6f, sea * 1.1f, h / math.max(0.01f, config.SeaLevel))
                      : height > 0.9f ? Color.Lerp(highland, Color.white, (height - 0.9f) * 6f)               // snowy tops
                      : Color.Lerp(lowland, highland, height / 0.9f);
                if (cells[i].Pentagon) c = Color.Lerp(c, air, 0.15f);   // the 12 pentagons, faintly tinted
                c.a = 1f;
                colors[i] = c;
            }

            // The crash site: a seeded cell on land. Turn the globe so it points up (x east, z north), like the ground.
            var rng = new Random(_site.Seed * 2654435761u | 1u);
            var crashCell = 0;
            for (var tries = 0; tries < 64; tries++)
            {
                crashCell = rng.NextInt(0, cells.Count);
                if (rank[crashCell] >= config.SeaLevel) break;
            }
            _crash = cells[crashCell].Centre;
            _north = Vector3.ProjectOnPlane(Vector3.up, _crash);
            if (_north.sqrMagnitude < 1e-6f) _north = Vector3.ProjectOnPlane(Vector3.forward, _crash);
            _north.Normalize();
            _east = Vector3.Cross(_crash, _north);
            _map.Globe.rotation = Quaternion.Inverse(Quaternion.LookRotation(_north, _crash));

            _map.Build(cells, colors, config.Gap, config.Grout.ToColor());
            _map.Material.SetColor("_AtmosphereColor", air);
            Universe();
            var pentagons = 0;
            foreach (var c in cells) if (c.Pentagon) pentagons++;
            Debug.Log($"[Map] hex globe: {cells.Count} cells ({cells.Count - pentagons} hexagons, {pentagons} pentagons), " +
                      $"built in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        /// <summary>The star and moons, to scale with the globe (its radius = 1) where the system really has them.</summary>
        void Universe()
        {
            CelestialBody home = null, sun = null;
            foreach (var b in _site.Bodies ?? Array.Empty<CelestialBody>())
            {
                if (b.Id == _site.Body) home = b;
                if (b.Star != null && sun == null) sun = b;
            }
            var radius = home != null ? math.max(0.01f, home.Scale * 0.5f) : 1f;
            // The star: real direction (from the ground), real size for its distance, pulled in to fit the camera.
            var orbit = home?.Orbit != null && home.Orbit.Radius > 0f ? home.Orbit.Radius / radius : 80f;
            var size = sun != null ? sun.Scale / radius : 8f;
            var distance = math.min(orbit, 150f);
            _map.Star(_view.Sun.sharedMaterial, _site.SunDirection, distance, size * distance / orbit);
            _star = _map.Globe.position + _site.SunDirection.normalized * distance;

            var moons = 0;
            foreach (var b in _site.Bodies ?? Array.Empty<CelestialBody>())
            {
                if (b.Orbit == null || b.Orbit.Around != _site.Body || b.Planet == null) continue;
                var o = b.Orbit;
                var angle = o.Phase * 2f * math.PI;
                var flat = new Vector3(math.cos(angle), 0f, math.sin(angle));
                var tilt = Quaternion.AngleAxis(o.NodeDegrees, Vector3.up) * Quaternion.AngleAxis(o.InclinationDegrees, Vector3.right);
                b.Planet.ApplyTo(_map.Moon(tilt * flat * (o.Radius / radius), b.Scale / radius));
                moons++;
            }
            Debug.Log($"[Map] universe: star {sun?.Id} {distance:0} radii away, {moons} moon(s)");
        }

        static float Terrain(Vector3 dir, PlanetMap config, float3 offset)
        {
            var p = (float3)dir * config.NoiseScale + offset;
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (var o = 0; o < math.max(1, config.Octaves); o++) { sum += noise.snoise(p) * amp; norm += amp; amp *= 0.5f; p *= 2f; }
            return math.saturate(sum / norm * 0.5f + 0.5f);
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
