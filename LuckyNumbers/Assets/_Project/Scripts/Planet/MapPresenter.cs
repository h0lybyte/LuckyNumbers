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
    /// M: the planet you're on as a hex globe (content/planet.txtpb map). Built once, from the same seed and
    /// look as the ground: cells coloured and raised by terrain noise, sea below sea_level. The crash cell is
    /// marked and a pin shows where you've walked to (walk_radius_metres per radian). Drag to turn, scroll to
    /// zoom, M (or Esc) to close; first-person control pauses meanwhile.
    /// </summary>
    [Presenter]
    public sealed class MapPresenter : IStartable, ITickable, IDisposable
    {
        readonly PlanetScene _config;
        readonly MapView _map;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly IFirstPerson _firstPerson;
        readonly ICrashSite _site;
        readonly PlayerInput _input;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _subscription;
        bool _open, _toggle, _close;
        float _distance;
        Vector3 _crash, _east, _north;
        Vector3 _crashOnGround;

        [Inject]
        public MapPresenter(PlanetScene config, MapView map, PlanetView view, PlayerView player, IFirstPerson firstPerson,
                            ICrashSite site, PlayerInput input, ISubscriber<InputActionPerformed> actions)
        {
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
                if (e.Action == InputAction.Map) _toggle = true;
                if (e.Action == InputAction.Cancel) _close = true;
            });
            _map.Show(false, Color.black);
        }

        public void Tick()
        {
            var config = _config.Map;
            if (config == null) return;
            if (_toggle && (_open || _firstPerson.Active) && _site.Built) Toggle(config);
            else if (_close && _open) Toggle(config);
            _toggle = _close = false;
            if (!_open) return;

            // Drag to turn (about the camera's up and right), scroll to zoom.
            if (_input.PointerPressed.Value)
            {
                var d = _input.PointerDelta.Value;
                var k = config.DragDegreesPerPixel * (_distance - 1f) / math.max(0.1f, config.Distance - 1f);   // finer up close
                _map.Globe.rotation = Quaternion.AngleAxis(-d.X * k, Vector3.up) * Quaternion.AngleAxis(d.Y * k, Vector3.right) * _map.Globe.rotation;
            }
            var scroll = _input.PointerScroll.Value;
            if (scroll != 0f) _distance = math.clamp(_distance * math.pow(1.1f, -math.sign(scroll)), config.ZoomMin, config.ZoomMax);
            _map.Distance(_distance);
            _map.Place(_crash, Pin(config), config.Relief * 1.5f + 0.02f, config.CrashColor.ToColor(), config.PinColor.ToColor());
        }

        void Toggle(PlanetMap config)
        {
            _open = !_open;
            if (_open && !_map.Built) Build(config);
            _firstPerson.Active = !_open;
            _player.Camera.enabled = !_open;               // the map camera draws instead (and the arms with it)
            if (_open)
            {
                Cursor.lockState = CursorLockMode.None;    // drag the globe
                _distance = config.Distance;
                // Turn the globe so where you are faces the camera, north up.
                var here = Pin(config);
                _map.Globe.rotation = Quaternion.Inverse(Quaternion.LookRotation(-here, _north));
            }
            _map.Show(_open, config.Background.ToColor());
            _view.MapHint(_open ? config.Hint : null);
            Debug.Log(_open ? $"[Map] open: {_site.Body}" : "[Map] closed");
        }

        /// <summary>Where you are on the globe: walking (x, z) metres from the crash goes east/north along a great circle.</summary>
        Vector3 Pin(PlanetMap config)
        {
            var offset = _player.Body.transform.position - _crashOnGround;
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

            var heights = new float[cells.Count];
            var colors = new Color[cells.Count];
            var lowland = Color.Lerp(land * 0.55f, air * 0.5f, 0.2f);
            var highland = Color.Lerp(land, Color.white, 0.15f);
            for (var i = 0; i < cells.Count; i++)
            {
                var h = rank[i];
                var wet = config.SeaLevel > 0f && h < config.SeaLevel;
                var height = wet ? 0f : (h - config.SeaLevel) / math.max(0.01f, 1f - config.SeaLevel);   // 0 at the shore
                heights[i] = height * height * config.Relief;                                               // mostly low, a few peaks
                var c = wet ? Color.Lerp(sea * 0.6f, sea * 1.1f, h / math.max(0.01f, config.SeaLevel))
                      : height > 0.9f ? Color.Lerp(highland, Color.white, (height - 0.9f) * 6f)               // snowy tops
                      : Color.Lerp(lowland, highland, height / 0.9f);
                if (cells[i].Pentagon) c = Color.Lerp(c, air, 0.15f);   // the 12 pentagons, faintly tinted
                c.a = 1f;
                colors[i] = c;
            }

            // The crash site: a seeded cell on land.
            var rng = new Random(_site.Seed * 2654435761u | 1u);
            var crashCell = 0;
            for (var tries = 0; tries < 64; tries++)
            {
                crashCell = rng.NextInt(0, cells.Count);
                if (heights[crashCell] > 0f) break;
            }
            _crash = cells[crashCell].Centre;
            _east = Vector3.Cross(Vector3.up, _crash).normalized;
            if (_east.sqrMagnitude < 1e-6f) _east = Vector3.right;
            _north = Vector3.Cross(_crash, _east).normalized;
            _crashOnGround = new Vector3(0f, 0f, -1.5f);   // where you woke (CrashSitePresenter): the walk counts from here

            _map.Build(cells, heights, colors, config.Gap);
            _map.Material.SetColor("_AtmosphereColor", air);
            var pentagons = 0;
            foreach (var c in cells) if (c.Pentagon) pentagons++;
            Debug.Log($"[Map] hex globe: {cells.Count} cells ({cells.Count - pentagons} hexagons, {pentagons} pentagons), " +
                      $"built in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
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
