using System;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Drives the universe: builds the SpaceWorld from the scope's SpaceBackdrop, runs OrbitSystem,
    /// mirrors positions to views, and pans the camera on click-drag (glide + bounds).
    /// </summary>
    [Presenter]
    public sealed class SpacePresenter : IStartable, ITickable
    {
        static readonly int PanOffset = Shader.PropertyToID("_PanOffset");

        readonly SpaceView _view;
        readonly SpaceBackdrop _backdrop;
        readonly PlayerInput _input;
        SpaceWorld _world;
        Transform[] _bodies;
        float2 _origin, _velocity;
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
            _backdrop.Stars.ApplyTo(_view.StarfieldMaterial);
            _world = new SpaceWorld(_backdrop.Bodies);
            _bodies = new Transform[_world.Count];
            for (var i = 0; i < _world.Count; i++)
            {
                var r = _view.SpawnBody(_world.Ids[i], i);
                _world.Look[i].ApplyTo(r.material);
                r.transform.localScale = Vector3.one * _world.Scale[i];
                _bodies[i] = r.transform;
            }
            var cam = _view.Camera.transform.position;
            _origin = new float2(cam.x, cam.y);
        }

        public void Tick()
        {
            OrbitSystem.Update(_world, Time.time);
            for (var i = 0; i < _world.Count; i++)
            {
                var p = _world.Position[i];
                var t = _bodies[i].localPosition;
                _bodies[i].localPosition = new Vector3(p.x, p.y, t.z);
            }
            Pan(Time.deltaTime);
        }

        void Pan(float dt)
        {
            var pan = _backdrop.Pan;
            if (pan == null || dt <= 0f) return;

            var pressed = _input.PointerPressed.Value;
            if (pressed && !_wasPressed) _dragging = !_view.IsPointerOverUI(); // drags can't start on buttons
            if (!pressed) _dragging = false;
            _wasPressed = pressed;

            var cam = _view.Camera;
            var unitsPerPixel = 2f * cam.orthographicSize / Mathf.Max(1, Screen.height);
            if (_dragging)
            {
                var d = _input.PointerDelta.Value;
                _velocity = -new float2(d.X, d.Y) * unitsPerPixel * pan.DragSpeed / dt; // grab-and-move
            }
            else
            {
                _velocity *= math.pow(math.saturate(pan.Glide), dt * 60f); // glide, frame-rate independent
            }

            var p = cam.transform.position;
            var next = new float2(p.x, p.y) + _velocity * dt;
            var bounds = new float2(pan.Bounds.X, pan.Bounds.Y);
            next = math.clamp(next, _origin - bounds, _origin + bounds);
            cam.transform.position = new Vector3(next.x, next.y, p.z);
            _view.StarfieldMaterial.SetVector(PanOffset, new Vector4(next.x - _origin.x, next.y - _origin.y));
        }
    }
}
