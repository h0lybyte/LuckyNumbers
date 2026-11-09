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
    public sealed class SpacePresenter : IStartable, ITickable, IDisposable
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");

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
        uint? _pendingSeed;

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
                _subscription = _seeds.Subscribe(e => OnSeed(e.Seed)); // buffered: also delivers the latest seed
        }

        void OnSeed(uint seed)
        {
            if (seed == 0) return; // buffered default before the first roll; lucky numbers are never 0
            Debug.Log($"[Space] lucky number {seed}");
            _pendingSeed = seed;
            if (_world == null || _world.Count == 0) { Expand(); return; }
            _phase = Phase.Collapsing;
            _phaseTime = 0f;
        }

        void Expand()
        {
            var bodies = UniverseGenerator.Generate(_backdrop.Generator, _pendingSeed ?? 1u);
            Build(bodies);
            var layout = new System.Text.StringBuilder();
            foreach (var b in bodies)
                if (b.Orbit != null) layout.Append($" {b.Id}@{b.Orbit.Radius:0.#}(d{b.Scale:0.#})");
            Debug.Log($"[Space] system:{layout}");
            _pendingSeed = null;
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
            _target = math.max(0, _world.IndexOf(_backdrop.Camera.Target));
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            OrbitSystem.Update(_world, Time.time);
            var bang = BigBang(dt);   // 1 = normal; <1 pulls bodies to the centre and shrinks them
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
            OrbitCamera(dt);
            // Galaxies are sky objects: Position is an offset from the camera, so they never come closer.
            var eye = (float3)_view.Camera.transform.position;
            for (var i = 0; i < _world.Count; i++)
                if (_world.Kind[i] == BodyKind.Galaxy) _bodies[i].localPosition = eye + _world.Position[i];
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
            t.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
            t.position += (t.right * c.FrameOffset.X + t.up * c.FrameOffset.Y) * size; // off-centre framing
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
