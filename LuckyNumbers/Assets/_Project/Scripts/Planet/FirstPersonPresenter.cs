using System;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>Lets the crash-site sequence hand control to the player (and set where they look).</summary>
    public interface IFirstPerson
    {
        bool Active { get; set; }
        /// <summary>Head yaw/pitch (degrees) the player takes over from.</summary>
        void Look(float yawDegrees, float pitchDegrees);
    }

    /// <summary>
    /// Walk + look on the ground patch (CharacterController): WASD, mouse (pointer lock on click), Shift to
    /// run, Space to jump. Feel is procedural: head bob from stride, arms sway against the look and dip on landing.
    /// </summary>
    [Presenter]
    public sealed class FirstPersonPresenter : IStartable, ITickable, IDisposable, IFirstPerson
    {
        readonly PlanetScene _config;
        readonly PlayerView _player;
        readonly ArmsView _arms;
        readonly PlayerInput _input;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _subscription;

        float _yaw, _pitch, _vertical, _stride, _landDip;
        Vector3 _sway;
        bool _jump, _wasGrounded = true;

        public bool Active { get; set; }

        public void Look(float yawDegrees, float pitchDegrees)
        {
            _yaw = yawDegrees;
            _pitch = pitchDegrees;
        }

        [Inject]
        public FirstPersonPresenter(PlanetScene config, PlayerView player, ArmsView arms, PlayerInput input,
                                    ISubscriber<InputActionPerformed> actions)
        {
            _config = config;
            _player = player;
            _arms = arms;
            _input = input;
            _actions = actions;
        }

        public void Start()
        {
            _subscription = _actions.Subscribe(e => { if (e.Action == InputAction.Jump) _jump = true; });
            _arms.Play("relax");
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (!Active || dt <= 0f) { _jump = false; return; }

            // Pointer lock: browsers need a click; Esc releases it.
            if (_input.PointerPressed.Value && Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            var looking = Cursor.lockState == CursorLockMode.Locked || _input.PointerPressed.Value;
            var look = _input.Look.Value;
            var lookDelta = looking ? new Vector2(look.X, look.Y) : Vector2.zero;
            _yaw += lookDelta.x * _config.LookDegreesPerPixel;
            _pitch = math.clamp(_pitch - lookDelta.y * _config.LookDegreesPerPixel, -85f, 85f);

            var body = _player.Body;
            var grounded = body.isGrounded;
            var move = _input.Move.Value;
            var speed = _input.Sprinting.Value ? _config.SprintSpeed : _config.WalkSpeed;
            var yawRot = Quaternion.Euler(0f, _yaw, 0f);
            var wish = yawRot * new Vector3(move.X, 0f, move.Y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            if (grounded && _vertical < 0f) _vertical = -2f;                 // stick to slopes
            if (_jump && grounded) _vertical = _config.JumpSpeed;
            _jump = false;
            _vertical -= _config.Gravity * dt;
            body.Move((wish * speed + Vector3.up * _vertical) * dt);
            body.transform.rotation = yawRot;

            if (!_wasGrounded && body.isGrounded) _landDip = 1f;              // landing thump
            _wasGrounded = body.isGrounded;
            _landDip = math.max(0f, _landDip - dt * 3f);

            // Head bob from stride, scaled by how fast we move over ground.
            var planar = new Vector3(body.velocity.x, 0f, body.velocity.z).magnitude;
            var moving = body.isGrounded ? math.saturate(planar / math.max(0.1f, _config.WalkSpeed)) : 0f;
            _stride += dt * _config.BobFrequency * 2f * math.PI * math.max(0.2f, planar / math.max(0.1f, _config.WalkSpeed));
            var bobY = math.abs(math.sin(_stride)) * _config.BobAmount * moving - _landDip * 0.08f;
            var bobX = math.sin(_stride) * _config.BobAmount * 0.5f * moving;
            _player.Head.localPosition = new Vector3(0f, _config.EyeHeight + bobY, 0f);
            _player.Head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // Arms lag against the look and follow the stride.
            var target = new Vector3(-lookDelta.y, lookDelta.x, 0f) * _config.Sway;
            _sway = Vector3.Lerp(_sway, target, 1f - math.exp(-10f * dt));
            _arms.Pose(new Vector3(bobX, bobY * 0.6f - _landDip * 0.04f, 0f),
                       Quaternion.Euler(math.clamp(_sway.x, -6f, 6f), math.clamp(_sway.y, -6f, 6f), -bobX * 30f));
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
