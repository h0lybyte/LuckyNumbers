using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// You arrive in your own fighter and land it near the wreck, fully piloted (content/planet.txtpb landing):
    /// mouse pitches and yaws, A/D roll (wings level themselves when you let go), W/S throttle, Shift boost,
    /// Space lift thrusters, under gravity and air drag. Touch down slow, level and gentle, or crash and start
    /// over. Landed: you step out beside your ship and walk (first person).
    /// </summary>
    [Presenter]
    public sealed class LandingPresenter : ITickable, IArrival, IDisposable
    {
        enum Phase { Idle, Flying, Crashed, Landed }

        readonly PlanetScene _config;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly GroundView _ground;
        readonly IFirstPerson _firstPerson;
        ICrashSite _site;
        readonly PlayerInput _input;
        readonly PlayerShip _playerShip;

        Phase _phase;
        Vector3 _velocity, _spin;      // m/s; degrees/s around the ship's right (pitch), up (yaw), forward (roll)
        Quaternion _rotation;
        Vector3 _position;
        float _throttle, _phaseTime;
        Vector3 _camPos;
        Transform _beacon;
        Collider[] _ownColliders = Array.Empty<Collider>();
        readonly Collider[] _hits = new Collider[16];
        UniTaskCompletionSource _done;

        [Inject]
        public LandingPresenter(PlanetScene config, PlanetView view, PlayerView player, GroundView ground,
                                IFirstPerson firstPerson, PlayerInput input, PlayerShip playerShip)
        {
            _config = config;
            _view = view;
            _player = player;
            _ground = ground;
            _firstPerson = firstPerson;
            _input = input;
            _playerShip = playerShip;
        }

        FlightLanding L => _config.Landing;
        ShipView Ship => _view.Fighter;
        Vector3 Wreck => _view.Wreck.Pivot.position;

        public async UniTask ArriveAsync(ICrashSite site, CancellationToken ct)
        {
            _site = site;
            _firstPerson.Active = false;
            _player.Camera.enabled = false;                        // the chase camera instead (and no arms)
            _view.FlightCamera.gameObject.SetActive(true);
            Ship.Show(PlayerShipSkin.Current(_playerShip), L.ShipLength);
            _ownColliders = Ship.Pivot.GetComponentsInChildren<Collider>(true);
            Beacon();
            Restart();
            _site.Follow(Ship.Pivot, _view.FlightCamera.transform);
            for (var t = 0f; t < 1f; t += Time.deltaTime) { _view.Fade(1f - t); await UniTask.Yield(ct); }
            _view.Fade(0f);
            _view.Hint(L.Hint, 1f);
            _done = new UniTaskCompletionSource();
            await _done.Task.AttachExternalCancellation(ct);
        }

        /// <summary>Back to the start: up high, some way off, heading for the wreck (seeded bearing).</summary>
        void Restart()
        {
            var bearing = Quaternion.Euler(0f, (_site.Seed % 360u) * 1f, 0f) * Vector3.forward;
            var start = Wreck - bearing * L.StartDistance;
            start.y = _ground.HeightAt(start) + L.StartAltitude;
            var toWreck = Wreck - start;
            toWreck.y = 0f;
            _position = start;
            _rotation = Quaternion.LookRotation(toWreck.normalized, Vector3.up);
            _velocity = _rotation * Vector3.forward * L.StartSpeed;
            _spin = Vector3.zero;
            _throttle = 0.35f;
            _camPos = _position - _rotation * Vector3.forward * (L.ShipLength * L.ChaseDistance * 3f) + Vector3.up * L.ShipLength;
            SetSolid(false);
            _phase = Phase.Flying;
            _phaseTime = 0f;
            _view.Flight("", null);
            Debug.Log($"[Landing] start {L.StartDistance:0} m from the wreck, {L.StartAltitude:0} m up");
        }

        public void Tick()
        {
            if (_phase == Phase.Idle || _phase == Phase.Landed) return;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            _phaseTime += dt;
            if (_phase == Phase.Flying) Fly(dt);
            else if (_phase == Phase.Crashed && _phaseTime > 2.2f) Restart();
            Ship.Pose(_position, _rotation);
            Chase(dt);
        }

        void Fly(float dt)
        {
            // Pointer lock like on foot: a click locks it; while locked (or dragging) the mouse steers.
            var pressed = _input.PointerPressed.Value;
            if (pressed && Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            var steering = Cursor.lockState == CursorLockMode.Locked || pressed;
            var look = steering ? new Vector2(_input.Look.Value.X, _input.Look.Value.Y) : Vector2.zero;
            var move = _input.Move.Value;

            // Turning: mouse pitch/yaw, A/D roll; settles with damping; wings level themselves when not rolling.
            var want = new Vector3(-look.y * L.TurnDegreesPerPixel / dt, look.x * L.TurnDegreesPerPixel / dt, -move.X * L.RollDegreesPerSecond);
            _spin = Vector3.Lerp(_spin, want, 1f - math.exp(-L.TurnDamping * dt));
            _rotation = _rotation * Quaternion.Euler(_spin * dt);
            if (math.abs(move.X) < 0.1f)
            {
                var level = Quaternion.LookRotation(_rotation * Vector3.forward, Vector3.up);
                _rotation = Quaternion.RotateTowards(_rotation, level, L.RollReturnDegreesPerSecond * dt);
            }

            // Forces: main engine (throttle, boost), lift thrusters, gravity, drag.
            _throttle = math.saturate(_throttle + move.Y * L.ThrottlePerSecond * dt);
            var boost = _input.Sprinting.Value ? L.Boost : 1f;
            var forward = _rotation * Vector3.forward;
            var airspeed = math.max(0f, Vector3.Dot(_velocity, forward));
            var accel = forward * (L.Thrust * _throttle * boost)
                      + _rotation * Vector3.up * (L.WingLift * airspeed * airspeed)            // wings: glide fast, sink slow
                      + (_input.Lifting.Value ? _rotation * Vector3.up * L.Lift : Vector3.zero)
                      + Vector3.down * L.Gravity;
            _velocity += accel * dt;
            _velocity *= math.exp(-L.Drag * dt);
            // Grip: the air bleeds off sideways drift (along the wings), more the faster you go, so the ship
            // goes where its nose points instead of skating.
            var right = _rotation * Vector3.right;
            _velocity -= right * (Vector3.Dot(_velocity, right) * (1f - math.exp(-L.Grip * math.saturate(airspeed / 25f) * dt)));
            _position += _velocity * dt;
            Ship.SetHeat(0f, 0.6f + 1.4f * _throttle * boost + (_input.Lifting.Value ? 0.6f : 0f));   // engines glow with thrust

            // Touchdown: the lowest of three gear points reaches the ground.
            var half = L.ShipLength * 0.5f;
            Vector3 Gear(float right, float forward) => _position + _rotation * new Vector3(right * half, -half * 0.18f, forward * half);
            var lowest = float.MaxValue;
            foreach (var g in new[] { Gear(0f, 0.7f), Gear(-0.55f, -0.5f), Gear(0.55f, -0.5f) })
                lowest = math.min(lowest, g.y - _ground.HeightAt(g));
            var normal = _ground.NormalAt(_position);
            var vertical = -Vector3.Dot(_velocity, normal);
            var horizontal = Vector3.ProjectOnPlane(_velocity, normal).magnitude;
            var tilt = Vector3.Angle(_rotation * Vector3.up, normal);
            var altitude = _position.y - _ground.HeightAt(_position);
            if (lowest <= 0f)
            {
                if (vertical <= L.MaxTouchdownVertical && horizontal <= L.MaxTouchdownHorizontal && tilt <= L.MaxTouchdownTilt) Land(normal);
                else Crash($"{vertical:0.0} m/s down, {horizontal:0.0} m/s along, {tilt:0} deg tilt");
                return;
            }
            // Hitting anything solid (the wreck, a boulder) in the air is a crash too.
            var n = Physics.OverlapSphereNonAlloc(_position, half * 0.45f, _hits, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                if (Array.IndexOf(_ownColliders, _hits[i]) >= 0 || _hits[i].GetComponentInParent<GroundView>() != null) continue;
                Crash($"hit {_hits[i].name}");
                return;
            }

            // HUD: altitude, climb, speed, throttle, and where the wreck is (distance, and left/right of your heading).
            var to = Wreck - _position;
            var flat = new Vector3(to.x, 0f, to.z);
            var heading = Vector3.SignedAngle(Vector3.ProjectOnPlane(_rotation * Vector3.forward, Vector3.up), flat, Vector3.up);
            var turn = math.abs(heading) < 8f ? "ahead" : heading < 0f ? $"{-heading:0} deg left" : $"{heading:0} deg right";
            _view.Flight($"ALT {altitude:0} m    CLIMB {-vertical:+0.0;-0.0} m/s    SPEED {_velocity.magnitude:0} m/s    " +
                         $"THROTTLE {_throttle * 100f:0}%{(boost > 1f ? " BOOST" : "")}    WRECK {flat.magnitude:0} m {turn}", null);
        }

        void Crash(string why)
        {
            _phase = Phase.Crashed;
            _phaseTime = 0f;
            _velocity = Vector3.zero;
            _spin = Vector3.zero;
            Ship.SetHeat(0.9f, 0.2f);                                          // scorched
            _view.Flight("", L.CrashedText);
            Debug.Log($"[Landing] crashed: {why}");
            FlashAsync().Forget();
        }

        async UniTaskVoid FlashAsync()
        {
            for (var t = 0f; t < 1f; t += Time.deltaTime) { _view.Fade(1f - t); await UniTask.Yield(); }
            _view.Fade(0f);
        }

        void Land(Vector3 normal)
        {
            _phase = Phase.Landed;
            _velocity = Vector3.zero;
            // Settle on the ground, upright along the slope.
            var forward = Vector3.ProjectOnPlane(_rotation * Vector3.forward, normal).normalized;
            _rotation = Quaternion.LookRotation(forward, normal);
            _position.y = _ground.HeightAt(_position) + L.ShipLength * 0.5f * 0.18f;
            Ship.Pose(_position, _rotation);
            Ship.SetHeat(0f, 0.3f);
            SetSolid(true);
            if (_beacon != null) _beacon.gameObject.SetActive(false);
            Debug.Log($"[Landing] landed {Vector3.Distance(_position, Wreck):0} m from the wreck");
            StepOutAsync().Forget();
        }

        /// <summary>Fade, then you stand beside your ship facing the wreck, on foot.</summary>
        async UniTaskVoid StepOutAsync()
        {
            _view.Flight("", L.LandedText);
            await UniTask.Delay(900);
            for (var t = 0f; t < 0.5f; t += Time.deltaTime) { _view.Fade(t * 2f); await UniTask.Yield(); }
            var side = _position + _rotation * Vector3.right * (L.ShipLength * 0.5f + 2f);
            side.y = _ground.HeightAt(side) + 0.05f;
            var body = _player.Body;
            body.enabled = false;
            var toWreck = Wreck - side;
            body.transform.SetPositionAndRotation(side, Quaternion.LookRotation(new Vector3(toWreck.x, 0f, toWreck.z), Vector3.up));
            body.enabled = true;
            _view.Flight(null, null);
            _view.FlightCamera.gameObject.SetActive(false);
            _player.Camera.enabled = true;
            _site.Follow(null, null);
            _firstPerson.Look(body.transform.eulerAngles.y, 6f);
            _firstPerson.Active = true;
            for (var t = 0f; t < 0.6f; t += Time.deltaTime) { _view.Fade(1f - t / 0.6f); await UniTask.Yield(); }
            _view.Fade(0f);
            _view.Hint(_config.Hint, 1f);
            foreach (var line in L.LandedLines) await DialogueLine.Play(line, _view.Dialogue, default);
            _done?.TrySetResult();
        }

        void Chase(float dt)
        {
            var cam = _view.FlightCamera.transform;
            var flatForward = Vector3.ProjectOnPlane(_rotation * Vector3.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f) flatForward = _rotation * Vector3.up;
            var want = _position - flatForward.normalized * (L.ShipLength * L.ChaseDistance) + Vector3.up * (L.ShipLength * L.ChaseHeight);
            _camPos = Vector3.Lerp(_camPos, want, 1f - math.exp(-5f * dt));
            var floor = _ground.HeightAt(_camPos) + 1.5f;                       // never under the ground
            if (_camPos.y < floor) _camPos.y = floor;
            cam.SetPositionAndRotation(_camPos, Quaternion.LookRotation(_position + _rotation * Vector3.forward * L.ShipLength - _camPos, Vector3.up));
        }

        /// <summary>The tall orange beam over the wreck (built with the scene): visible through the haze.</summary>
        void Beacon()
        {
            _beacon = _view.WreckBeacon;
            _beacon.position = Wreck + Vector3.up * (_beacon.localScale.y);   // a cylinder is 2 high: base on the wreck
            _beacon.gameObject.SetActive(true);
        }

        void SetSolid(bool solid)
        {
            foreach (var c in _ownColliders) c.enabled = solid;   // off while flying (the ship tests the world itself)
        }

        public void Dispose() => _done?.TrySetCanceled();
    }
}
