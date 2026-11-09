using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Drives the 3D universe: builds the SpaceWorld from the scope's SpaceBackdrop, runs OrbitSystem,
    /// mirrors positions to views, and orbits the camera around a target body (drag rotates, scroll zooms).
    /// </summary>
    [Presenter]
    public sealed class SpacePresenter : IStartable, ITickable
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");

        readonly SpaceView _view;
        readonly SpaceBackdrop _backdrop;
        readonly PlayerInput _input;
        SpaceWorld _world;
        Transform[] _bodies;
        int _target, _sun;
        float _yaw, _pitch, _distance;
        float2 _spin;               // degrees per second (yaw, pitch)
        bool _dragging, _wasPressed;

        [Inject]
        public SpacePresenter(SpaceView view, SpaceBackdrop backdrop, PlayerInput input)
        {
            _view = view;
            _backdrop = backdrop;
            _input = input;
        }

        public void Start()
        {
            if (_view.SkyboxMaterial != null) _backdrop.Stars.ApplyTo(_view.SkyboxMaterial);
            _world = new SpaceWorld(_backdrop.Bodies);
            _bodies = new Transform[_world.Count];
            for (var i = 0; i < _world.Count; i++)
            {
                var star = _world.Kind[i] == BodyKind.Star;
                var r = _view.SpawnBody(_world.Ids[i], star);
                if (star) _world.Star[i].ApplyTo(r.material);
                else _world.Planet[i].ApplyTo(r.material);
                r.transform.localScale = Vector3.one * _world.Scale[i];
                _bodies[i] = r.transform;
            }
            _sun = _world.FirstStar();
            if (_sun >= 0) Shader.SetGlobalVector(SunColor, _world.Star[_sun].LightColor.ToColor());
            else Shader.SetGlobalVector(SunPosition, Vector4.zero); // planets fall back to a fixed light direction

            var cam = _backdrop.Camera;
            _target = math.max(0, _world.IndexOf(cam.Target));
            _yaw = cam.YawDegrees;
            _pitch = cam.PitchDegrees;
            _distance = cam.Distance;
            if (cam.FieldOfViewDegrees > 0f) _view.Camera.fieldOfView = cam.FieldOfViewDegrees;
        }

        public void Tick()
        {
            OrbitSystem.Update(_world, Time.time);
            for (var i = 0; i < _world.Count; i++) _bodies[i].localPosition = _world.Position[i];
            if (_sun >= 0)
            {
                var s = _world.Position[_sun];
                Shader.SetGlobalVector(SunPosition, new Vector4(s.x, s.y, s.z, 1f)); // w = 1: point light set
            }
            OrbitCamera(Time.deltaTime);
        }

        void OrbitCamera(float dt)
        {
            var c = _backdrop.Camera;
            if (dt > 0f)
            {
                var pressed = _input.PointerPressed.Value;
                if (pressed && !_wasPressed) _dragging = !_view.IsPointerOverUI(); // drags can't start on buttons
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
                if (scroll != 0f && !_view.IsPointerOverUI())
                    _distance = math.clamp(_distance * (1f - math.sign(scroll) * c.ZoomStep), c.MinDistance, c.MaxDistance);
            }

            var target = (Vector3)_world.Position[_target];
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var t = _view.Camera.transform;
            t.SetPositionAndRotation(target - rotation * Vector3.forward * _distance, rotation);
            t.position += t.right * c.FrameOffset.X + t.up * c.FrameOffset.Y; // off-centre framing
        }
    }
}
