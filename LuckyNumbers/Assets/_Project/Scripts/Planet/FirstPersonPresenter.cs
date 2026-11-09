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

        float _yaw, _pitch, _vertical, _stride, _landDip, _errorLogAt;
        readonly float[] _touch = new float[2];
        readonly bool[] _touching = new bool[2];
        readonly RaycastHit[] _hits = new RaycastHit[4];
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
            _arms.SetLife(_config.ArmBreathDegrees, _config.ArmSwingDegrees, _config.FingerFidgetDegrees);
            if (_config.Hands != null) _arms.Init(_config.Hands);
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

            _arms.SetMotion(_stride, moving);
            // Arms lag against the look and follow the stride.
            var target = new Vector3(-lookDelta.y, lookDelta.x, 0f) * _config.Sway;
            _sway = Vector3.Lerp(_sway, target, 1f - math.exp(-10f * dt));
            _arms.Pose(new Vector3(bobX, bobY * 0.6f - _landDip * 0.04f, 0f),
                       Quaternion.Euler(math.clamp(_sway.x, -6f, 6f), math.clamp(_sway.y, -6f, 6f), -bobX * 30f));
            Hands(dt);
        }

        /// <summary>IK self-test (debug_reach) or touching: a palm goes flat on walls/hulls you're facing up close.</summary>
        void Hands(float dt)
        {
            if (_arms.ArmCount < 2) return;
            var eye = _player.Camera.transform;
            if (_config.DebugReach)
            {
                var t = Time.time;
                for (var s = 0; s < 2; s++)
                {
                    var side = s == 0 ? -1f : 1f;   // L, R
                    var target = eye.position + eye.forward * 0.42f + eye.right * (side * 0.2f + 0.09f * math.cos(t * 1.7f + s))
                               + eye.up * (-0.14f + 0.09f * math.sin(t * 2.3f + s * 2f));
                    _arms.ReachTo(s, target, 1f);
                }
                if (Time.time > _errorLogAt)
                {
                    _errorLogAt = Time.time + 2f;
                    Debug.Log($"[Hands] IK error L {_arms.LastError[0]:0.0000} m R {_arms.LastError[1]:0.0000} m");
                }
                return;
            }

            for (var s = 0; s < 2; s++)
            {
                var side = s == 0 ? -1f : 1f;
                // Where you look, a little toward each hand's side; only things you could touch (not the ground).
                var dir = Quaternion.AngleAxis(side * 9f, eye.up) * eye.forward;
                var hit = false;
                var best = new RaycastHit { distance = float.MaxValue };
                var n = Physics.RaycastNonAlloc(eye.position, dir, _hits, _config.TouchDistance, ~0, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < n; i++)
                {
                    var h = _hits[i];
                    if (h.collider.transform.IsChildOf(_player.Body.transform)) continue;                    // not ourselves
                    if (h.collider.GetComponentInParent<GroundView>() != null) continue;                     // not the ground
                    if (Vector3.Distance(_arms.ShoulderPosition(s), h.point) > _arms.Reach(s) * 1.45f) continue; // reachable (small lean)
                    if (h.distance < best.distance) { best = h; hit = true; }
                }
                if (hit != _touching[s])
                {
                    _touching[s] = hit;
                    Debug.Log(hit ? $"[Hands] {(s == 0 ? "left" : "right")} hand touches {best.collider.name} at {best.distance:0.00} m"
                                  : $"[Hands] {(s == 0 ? "left" : "right")} hand lets go");
                }
                _touch[s] = Mathf.MoveTowards(_touch[s], hit ? 1f : 0f, dt * (hit ? 4f : 3f));
                if (_touch[s] <= 0f) { _arms.ReachTo(s, Vector3.zero, 0f); continue; }
                if (hit)
                {
                    var up = Vector3.ProjectOnPlane(eye.up + eye.right * side * 0.3f, best.normal).normalized;
                    // Wrist sits below the palm's centre and a hair off the surface.
                    var wrist = best.point + best.normal * 0.03f - up * 0.07f;
                    _lastTouch[s] = (wrist, -best.normal, up);
                }
                var (w, facing, along) = _lastTouch[s];
                var ease = _touch[s] * _touch[s] * (3f - 2f * _touch[s]);
                _arms.ReachTo(s, w, ease, facing, along, 0.85f);
            }
        }

        readonly (Vector3 wrist, Vector3 facing, Vector3 along)[] _lastTouch = new (Vector3, Vector3, Vector3)[2];

        public void Dispose() => _subscription?.Dispose();
    }
}
