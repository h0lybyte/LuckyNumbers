using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Drives the 3D universe: builds a SpaceWorld (hand-placed bodies, or generated from each
    /// UniverseSeedChanged), runs OrbitSystem, mirrors positions to views, and orbits the camera around a
    /// target body (drag rotates, scroll zooms). New seeds play a big bang: collapse, then expand.
    /// </summary>
    [Presenter]
    public sealed class SpacePresenter : IStartable, ITickable, IDisposable, ISpaceDirector
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");
        static readonly int DiskFade = Shader.PropertyToID("_Fade");
        static readonly int DiskGaps0 = Shader.PropertyToID("_Gaps0");
        static readonly int DiskGaps1 = Shader.PropertyToID("_Gaps1");

        enum Phase { Idle, Collapsing, Expanding }

        readonly SpaceView _view;
        readonly SpaceBackdrop _backdrop;
        readonly PlayerInput _input;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        IDisposable _subscription;

        SpaceWorld _world;
        Transform[] _bodies = Array.Empty<Transform>();
        int _target, _sun;
        float _yaw, _pitch, _distance;
        float2 _spin;               // degrees per second (yaw, pitch)
        bool _dragging, _wasPressed;

        Phase _phase;
        float _phaseTime;
        UniverseSeedChanged? _pendingRoll;
        uint _seed;
        bool _directed;

        public uint Seed => _seed;
        public Camera Camera => _view.Camera;
        public void TakeCamera() => _directed = true;
        public void Release() => _directed = false;

        public System.Collections.Generic.IReadOnlyList<string> Worlds
        {
            get
            {
                var worlds = new System.Collections.Generic.List<string>();
                for (var i = 0; i < (_world?.Count ?? 0); i++)
                {
                    if (_world.Kind[i] != BodyKind.Planet) continue;
                    var parent = _world.OrbitParent[i];
                    if (parent < 0 || _world.Kind[parent] == BodyKind.Star) worlds.Add(_world.Ids[i]);   // moons orbit planets
                }
                return worlds;
            }
        }

        public string Focus => _world != null && _world.Count > 0 ? _world.Ids[_target] : null;

        /// <summary>The planet the museum freighter goes down on: one of the system's planets, from the lucky number.</summary>
        public static string CrashSite(System.Collections.Generic.IReadOnlyList<string> worlds, uint seed) =>
            worlds.Count == 0 ? null : worlds[new Unity.Mathematics.Random((seed ^ 0xA11CE5EDu) | 1u).NextInt(worlds.Count)];

        public string Nearest(float3 point, out float surfaceDistance)
        {
            string best = null;
            surfaceDistance = float.MaxValue;
            for (var i = 0; i < (_world?.Count ?? 0); i++)
            {
                if (_world.Kind[i] == BodyKind.Galaxy) continue;
                var d = math.distance(point, _world.Position[i]) - _world.Scale[i] * 0.5f;
                if (d < surfaceDistance) { surfaceDistance = d; best = _world.Ids[i]; }
            }
            return best;
        }

        public bool TryGetBody(string id, out float3 position, out float diameter)
        {
            var i = _world?.IndexOf(id) ?? -1;
            position = i >= 0 ? _world.Position[i] : float3.zero;
            diameter = i >= 0 ? _world.Scale[i] : 0f;
            return i >= 0;
        }
        float _diskRadius, _systemRadius, _overviewRadius;

        [Inject]
        public SpacePresenter(SpaceView view, SpaceBackdrop backdrop, PlayerInput input,
                              IBufferedSubscriber<UniverseSeedChanged> seeds)
        {
            _view = view;
            _backdrop = backdrop;
            _input = input;
            _seeds = seeds;
        }

        public void Start()
        {
            if (_view.SkyboxMaterial != null) _backdrop.Stars.ApplyTo(_view.SkyboxMaterial);
            var cam = _backdrop.Camera;
            _yaw = cam.YawDegrees;
            _pitch = cam.PitchDegrees;
            _distance = cam.Distance;
            if (cam.FieldOfViewDegrees > 0f) _view.Camera.fieldOfView = cam.FieldOfViewDegrees;

            Build(_backdrop.Bodies);
            if (_backdrop.Generator != null)
                _subscription = _seeds.Subscribe(OnSeed); // buffered: also delivers the latest roll
        }

        void OnSeed(UniverseSeedChanged roll)
        {
            if (roll.Seed == 0) return; // buffered default before the first roll; lucky numbers are never 0
            Debug.Log($"[Space] lucky number {roll.Seed} (star {roll.Star:0.##}, worlds {roll.Worlds:0.##}, luck {roll.Luck:0.##}, moons {roll.Moons:0.##})");
            _pendingRoll = roll;
            if (_backdrop.Instant && (_world == null || _world.Count == 0))
            {
                Expand();
                _phase = Phase.Idle;   // already formed: no big bang
                Place(1f);
                return;
            }
            if (_world == null || _world.Count == 0) { Expand(); return; }
            _phase = Phase.Collapsing;
            _phaseTime = 0f;
        }

        void Expand()
        {
            var roll = _pendingRoll ?? new UniverseSeedChanged(1u, -1f, -1f, -1f, -1f);
            _seed = roll.Seed;
            var bodies = UniverseGenerator.Generate(_backdrop.Generator, roll);
            Build(bodies);
            var sky = UniverseGenerator.Sky(_backdrop.Generator, roll);
            if (sky != null && _view.SkyboxMaterial != null) sky.ApplyTo(_view.SkyboxMaterial);
            SetupDisk(UniverseGenerator.Disk(_backdrop.Generator, roll), bodies);
            var layout = new System.Text.StringBuilder();
            foreach (var b in bodies)
                if (b.Orbit != null) layout.Append($" {b.Id}@{b.Orbit.Radius:0.#}(d{b.Scale:0.#})");
            Debug.Log($"[Space] system:{layout}");
            _pendingRoll = null;
            _phase = Phase.Expanding;
            _phaseTime = 0f;
        }

        void Build(IReadOnlyList<CelestialBody> bodies)
        {
            foreach (var t in _bodies) if (t != null) Object.Destroy(t.gameObject);
            _world = new SpaceWorld(bodies);
            _bodies = new Transform[_world.Count];
            for (var i = 0; i < _world.Count; i++)
            {
                var kind = _world.Kind[i];
                var r = _view.SpawnBody(_world.Ids[i], kind);
                switch (kind)
                {
                    case BodyKind.Star: _world.Star[i].ApplyTo(r.material); break;
                    case BodyKind.Galaxy: _world.Galaxy[i].ApplyTo(r.material); break;
                    default: _world.Planet[i].ApplyTo(r.material); break;
                }
                r.transform.localScale = Vector3.one * _world.Scale[i];
                _bodies[i] = r.transform;
            }
            _sun = _world.FirstStar();
            if (_sun >= 0) Shader.SetGlobalVector(SunColor, _world.Star[_sun].LightColor.ToColor());
            else Shader.SetGlobalVector(SunPosition, Vector4.zero); // planets fall back to a fixed light direction
            // Camera focus: the configured body, or the crash site picked from the lucky number (same for every scene).
            var focus = string.IsNullOrEmpty(_backdrop.Camera.Target) ? CrashSite(Worlds, _seed) : _backdrop.Camera.Target;
            _target = math.max(0, _world.IndexOf(focus));
            _systemRadius = 0f;
            for (var i = 0; i < _world.Count; i++)
                if (_world.Kind[i] != BodyKind.Galaxy) _systemRadius = math.max(_systemRadius, math.length(_world.Position[i]) * 1.2f);
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            var bang = BigBang(dt);   // 1 = normal; <1 pulls bodies to the centre and shrinks them
            UpdateDisk(bang);
            Place(bang);
            if (!_directed) OrbitCamera(dt);
            // Galaxies are sky objects: Position is an offset from the camera, so they never come closer.
            var eye = (float3)_view.Camera.transform.position;
            for (var i = 0; i < _world.Count; i++)
                if (_world.Kind[i] == BodyKind.Galaxy) _bodies[i].localPosition = eye + _world.Position[i];
        }

        /// <summary>Dust disk out to the outermost planet, with a gap at each planet's orbit.</summary>
        void SetupDisk(DiskLook look, List<CelestialBody> bodies)
        {
            var disk = _view.Disk;
            _diskRadius = 0f;
            if (disk == null || look == null) return;
            var radii = new List<float>();
            foreach (var b in bodies)
                if (b.Orbit != null && b.Orbit.Around == "sun") radii.Add(b.Orbit.Radius);
            foreach (var r in radii) _diskRadius = math.max(_diskRadius, r * 1.2f);
            if (_diskRadius <= 0f) return;
            _systemRadius = math.max(_systemRadius, _diskRadius);
            var gaps = new float[8];
            for (var i = 0; i < radii.Count && i < 8; i++) gaps[i] = radii[i] / _diskRadius;
            var m = disk.material;
            look.ApplyTo(m);
            m.SetVector(DiskGaps0, new Vector4(gaps[0], gaps[1], gaps[2], gaps[3]));
            m.SetVector(DiskGaps1, new Vector4(gaps[4], gaps[5], gaps[6], gaps[7]));
        }

        /// <summary>The disk flares as the system forms, grows with it, then clears.</summary>
        void UpdateDisk(float bang)
        {
            var disk = _view.Disk;
            if (disk == null) return;
            var g = _backdrop.Generator;
            var e = _phase == Phase.Expanding && g != null ? math.saturate(_phaseTime / math.max(0.01f, g.ExpandSeconds)) : 1f;
            var fade = _diskRadius > 0f && _phase == Phase.Expanding ? math.smoothstep(0f, 0.06f, e) * math.pow(1f - e, 1.4f) : 0f;
            disk.gameObject.SetActive(fade > 0.001f);
            if (fade <= 0.001f) return;
            var t = disk.transform;
            t.localPosition = _sun >= 0 ? _world.Position[_sun] : float3.zero;
            t.localRotation = Quaternion.Euler(90f, 0f, 0f);            // quad (XY) -> orbital plane (XZ)
            t.localScale = Vector3.one * (_diskRadius * 2f * math.max(bang, 0.05f));
            disk.material.SetFloat(DiskFade, fade);
        }

        const float OverviewPitchDegrees = 55f;

        /// <summary>0 = follow the target, 1 = overview: eases out during collapse, holds, eases back late in expand.</summary>
        float OverviewWeight()
        {
            var g = _backdrop.Generator;
            if (g == null || _phase == Phase.Idle) return 0f;
            if (_phase == Phase.Collapsing) return math.smoothstep(0f, 1f, math.saturate(_phaseTime / math.max(0.01f, g.CollapseSeconds)));
            var e = math.saturate(_phaseTime / math.max(0.01f, g.ExpandSeconds));
            return 1f - math.smoothstep(0.55f, 1f, e);
        }

        /// <summary>Orbits -> body transforms and the sun light. Also run right after a build, so the first
        /// frame (e.g. a scene that continues the title's view) already shows bodies on their orbits.</summary>
        void Place(float bang)
        {
            OrbitSystem.Update(_world, Time.time);
            var centre = _sun >= 0 ? _world.Position[_sun] : float3.zero;
            for (var i = 0; i < _world.Count; i++)
            {
                if (_world.Kind[i] == BodyKind.Galaxy) continue;
                _bodies[i].localPosition = math.lerp(centre, _world.Position[i], bang);
                _bodies[i].localScale = Vector3.one * (_world.Scale[i] * bang);
            }
            if (_sun >= 0)
            {
                var s = _world.Position[_sun];
                Shader.SetGlobalVector(SunPosition, new Vector4(s.x, s.y, s.z, 1f)); // w = 1: point light set
            }
        }

        float BigBang(float dt)
        {
            var g = _backdrop.Generator;
            if (_phase == Phase.Idle || g == null) return 1f;
            _phaseTime += dt;
            if (_phase == Phase.Collapsing)
            {
                var t = math.saturate(_phaseTime / math.max(0.01f, g.CollapseSeconds));
                if (t >= 1f) { Expand(); return 0f; }
                return 1f - t * t;                                 // accelerate into the centre
            }
            var e = math.saturate(_phaseTime / math.max(0.01f, g.ExpandSeconds));
            if (e >= 1f) _phase = Phase.Idle;
            return 1f - math.pow(1f - e, 3f);                      // burst out, settle gently
        }

        void OrbitCamera(float dt)
        {
            var c = _backdrop.Camera;
            if (dt > 0f)
            {
                var pressed = _input.PointerPressed.Value;
                var pointer = _input.PointerPosition.Value;
                var overUi = _view.IsPointerOverUI(new Vector2(pointer.X, pointer.Y));
                if (pressed && !_wasPressed) _dragging = !overUi; // drags can't start on buttons
                if (!pressed) _dragging = false;
                _wasPressed = pressed;

                var degreesPerPixel = c.DragDegreesPerScreen / Mathf.Max(1, Screen.width);
                if (_dragging)
                {
                    var d = _input.PointerDelta.Value;
                    _spin = new float2(d.X, -d.Y) * degreesPerPixel / dt;   // grab the universe and turn it
                }
                else
                {
                    _spin *= math.pow(math.saturate(c.Glide), dt * 60f);     // glide, frame-rate independent
                }
                _yaw += _spin.x * dt;
                _pitch = math.clamp(_pitch + _spin.y * dt, c.MinPitchDegrees, c.MaxPitchDegrees);

                var scroll = _input.PointerScroll.Value;
                if (scroll != 0f && !overUi)
                    _distance = math.clamp(_distance * (1f - math.sign(scroll) * c.ZoomStep), c.MinDistance, c.MaxDistance);
            }

            if (_world.Count == 0) return;
            var target = (Vector3)_world.Position[_target];
            var size = math.max(0.01f, _world.Scale[_target]);
            var distance = math.max(_distance, 0.6f) * size;   // content distances are in target diameters
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var t = _view.Camera.transform;
            var position = target - rotation * Vector3.forward * distance
                         + (rotation * Vector3.right * c.FrameOffset.X + rotation * Vector3.up * c.FrameOffset.Y) * size; // off-centre framing

            // Big bang: pull up and out to watch the whole system form from above, then glide back.
            _overviewRadius = _overviewRadius <= 0f ? _systemRadius : math.lerp(_overviewRadius, _systemRadius, 1f - math.exp(-4f * dt));
            var w = OverviewWeight();
            if (w > 0f && _overviewRadius > 0f)
            {
                var centre = _sun >= 0 ? (Vector3)_world.Position[_sun] : Vector3.zero;
                var overview = Quaternion.Euler(OverviewPitchDegrees, _yaw, 0f);
                var far = centre - overview * Vector3.forward * (_overviewRadius * 2.4f); // fits the system in ~45 degrees
                position = Vector3.Lerp(position, far, w);
                rotation = Quaternion.Slerp(rotation, overview, w);
            }
            t.SetPositionAndRotation(position, rotation);
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
