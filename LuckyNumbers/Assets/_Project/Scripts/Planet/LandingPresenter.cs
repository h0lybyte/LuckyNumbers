using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using MessagePipe;
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
    ///
    /// Flight assist (FlightLanding.assist, on by default): W/S set a speed the ship holds, the engine pushing
    /// forward and air brakes pulling back, so letting off really slows you; Space hovers: the thrusters hold a
    /// controlled descent (quick up high, gentle near the ground), bleed off drift and level the ship. The HUD
    /// shows descent, drift and tilt against the touchdown limits (green / amber / red) once you're low.
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
        readonly FlightState _state;
        readonly ISubscriber<FlightHudReady> _hudReady;
        IDisposable _hudSubscription;
        bool _pageHud;          // the page draws the flight HUD (FlightHudReady): Unity's readout steps aside
        float _stateTimer;

        Phase _phase;
        Vector3 _velocity, _spin;      // m/s; degrees/s around the ship's right (pitch), up (yaw), forward (roll)
        Quaternion _rotation;
        Vector3 _position;
        float _throttle, _phaseTime;
        // Assisted steering: heading (around the world's up), pitch and bank, and the smoothed turn rates.
        float _heading, _pitch, _bank, _yawRate, _pitchRate;
        Vector3 _pad;                  // the landing pad: the flattest spot a little way from the wreck
        Transform _padMarker;
        Vector3 _camPos;
        Transform _beacon;
        Collider[] _ownColliders = Array.Empty<Collider>();
        readonly Collider[] _hits = new Collider[16];
        UniTaskCompletionSource _done;

        [Inject]
        public LandingPresenter(PlanetScene config, PlanetView view, PlayerView player, GroundView ground,
                                IFirstPerson firstPerson, PlayerInput input, PlayerShip playerShip, FlightState state,
                                ISubscriber<FlightHudReady> hudReady)
        {
            _config = config;
            _view = view;
            _player = player;
            _ground = ground;
            _firstPerson = firstPerson;
            _input = input;
            _playerShip = playerShip;
            _state = state;
            _hudReady = hudReady;
            _hudSubscription = _hudReady.Subscribe(e =>
            {
                _pageHud = e.On;
                if (_pageHud && _phase == Phase.Flying) _view.Hint("", 0f);
            });
        }

        FlightLanding L => _config.Landing;
        FlightAssist A => _config.Landing.Assist;
        bool Assisted => A != null && !A.Disabled;
        FlightHover H => _config.Landing.Hover;
        bool HoverShip => H != null && H.Enabled;
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
            Pad();
            Restart();
            _site.Follow(Ship.Pivot, _view.FlightCamera.transform);
            for (var t = 0f; t < 1f; t += Time.deltaTime) { _view.Fade(1f - t); await UniTask.Yield(ct); }
            _view.Fade(0f);
            if (!_pageHud) _view.Hint(L.Hint, 1f);   // the page's HUD has its own controls card
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
            _heading = _rotation.eulerAngles.y;
            _pitch = _bank = _yawRate = _pitchRate = 0f;
            // Assisted, the throttle is a speed: start at the speed you arrive with.
            _throttle = Assisted ? math.saturate(L.StartSpeed / math.max(1f, A.MaxSpeed)) : 0.35f;
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

            var hovering = false;
            var boost = 1f;
            if (HoverShip) HoverMove(look, move, dt, out hovering, out boost);
            else
            {
                var altitudeNow = _position.y - _ground.HeightAt(_position);
                hovering = Assisted && _input.Lifting.Value;
                if (Assisted) Steer(look, move.X, hovering, altitudeNow, dt);
                else
                {
                    // Turning: mouse pitch/yaw, A/D roll; settles with damping; wings level themselves when not rolling.
                    var want = new Vector3(-look.y * L.TurnDegreesPerPixel / dt, look.x * L.TurnDegreesPerPixel / dt, -move.X * L.RollDegreesPerSecond);
                    _spin = Vector3.Lerp(_spin, want, 1f - math.exp(-L.TurnDamping * dt));
                    _rotation = _rotation * Quaternion.Euler(_spin * dt);
                }
                if (!Assisted && math.abs(move.X) < 0.1f)
                {
                    // Wings level themselves when you let go of roll; quicker low down with the assist.
                    var level = Quaternion.LookRotation(_rotation * Vector3.forward, Vector3.up);
                    var rate = L.RollReturnDegreesPerSecond * (Assisted && altitudeNow < A.LowAltitude ? math.max(1f, A.LowLevelFactor) : 1f);
                    _rotation = Quaternion.RotateTowards(_rotation, level, rate * dt);
                }

                // Forces: main engine (throttle, boost), lift thrusters, gravity, drag.
                _throttle = math.saturate(_throttle + move.Y * L.ThrottlePerSecond * dt);
                boost = _input.Sprinting.Value ? L.Boost : 1f;
                var forward = _rotation * Vector3.forward;
                var airspeed = math.max(0f, Vector3.Dot(_velocity, forward));
                Vector3 accel;
                if (Assisted)
                {
                    // Speed hold: the engine pushes towards the throttle's speed, the air brakes pull back down to it.
                    var target = _throttle * A.MaxSpeed * (_input.Sprinting.Value ? math.max(1f, A.BoostSpeed) : 1f);
                    if (hovering) target = math.min(target, L.MaxTouchdownHorizontal * 0.6f);   // creep: safe to touch down
                    var push = math.clamp((target - airspeed) * A.SpeedGain, -A.Brake, L.Thrust * boost);
                    accel = forward * push
                          + _rotation * Vector3.up * (L.WingLift * airspeed * airspeed)
                          + Vector3.down * L.Gravity;
                    if (hovering)
                    {
                        // Hover: thrusters hold a descent (faster up high, gentle near the ground), straight up in the
                        // world so it doesn't matter how the ship is tilted, and drift bleeds off.
                        var descent = math.clamp(altitudeNow * A.DescentPerMetre, A.HoverDescent, math.max(A.HoverDescent, A.MaxDescent));
                        // Cushion: the last few metres ease off further, so the gear settles rather than lands.
                        if (A.CushionHeight > 0f && altitudeNow < A.CushionHeight)
                            descent *= math.max(0.35f, altitudeNow / A.CushionHeight);
                        var wing = L.WingLift * airspeed * airspeed * math.max(0f, (_rotation * Vector3.up).y);
                        var up = math.clamp(L.Gravity - wing + (-descent - _velocity.y) * A.HoverGain, 0f, L.Lift * 1.6f);
                        accel += Vector3.up * up;
                        var drift = new Vector3(_velocity.x, 0f, _velocity.z);
                        _velocity -= drift * (1f - math.exp(-A.HoverDriftDamping * dt));
                    }
                }
                else
                {
                    accel = forward * (L.Thrust * _throttle * boost)
                          + _rotation * Vector3.up * (L.WingLift * airspeed * airspeed)            // wings: glide fast, sink slow
                          + (_input.Lifting.Value ? _rotation * Vector3.up * L.Lift : Vector3.zero)
                          + Vector3.down * L.Gravity;
                }
                _velocity += accel * dt;
                _velocity *= math.exp(-L.Drag * dt);
                // Grip: the air bleeds off sideways drift (along the wings), more the faster you go, so the ship
                // goes where its nose points instead of skating.
                var right = _rotation * Vector3.right;
                _velocity -= right * (Vector3.Dot(_velocity, right) * (1f - math.exp(-L.Grip * math.saturate(airspeed / 25f) * dt)));
                _position += _velocity * dt;
                Ship.SetHeat(0f, 0.6f + 1.4f * _throttle * boost + (_input.Lifting.Value ? 0.6f : 0f));   // engines glow with thrust
            }

            // Touchdown: the lowest of three gear points reaches the ground.
            var half = L.ShipLength * 0.5f;
            Vector3 Gear(float right, float forward) => _position + _rotation * new Vector3(right * half, -half * 0.18f, forward * half);
            var lowest = float.MaxValue;
            foreach (var g in new[] { Gear(0f, 0.7f), Gear(-0.55f, -0.5f), Gear(0.55f, -0.5f) })
                lowest = math.min(lowest, g.y - _ground.SurfaceAt(g, 0f));   // the mesh you'd hit, not the height function
            var normal = _ground.NormalAt(_position);
            var vertical = -Vector3.Dot(_velocity, normal);
            var horizontal = Vector3.ProjectOnPlane(_velocity, normal).magnitude;
            var tilt = Vector3.Angle(_rotation * Vector3.up, normal);
            var altitude = _position.y - _ground.HeightAt(_position);
            if (lowest <= 0f)
            {
                if (vertical <= L.MaxTouchdownVertical && horizontal <= L.MaxTouchdownHorizontal && tilt <= L.MaxTouchdownTilt) Land(normal);
                else
                {
                    var why = new System.Collections.Generic.List<string>();
                    if (vertical > L.MaxTouchdownVertical) why.Add("descent");
                    if (horizontal > L.MaxTouchdownHorizontal) why.Add("drift");
                    if (tilt > L.MaxTouchdownTilt) why.Add("tilt");
                    Crash($"{vertical:0.0} m/s down, {horizontal:0.0} m/s along, {tilt:0} deg tilt", string.Join(",", why));
                }
                return;
            }
            // Hitting anything solid (the wreck, a boulder) in the air is a crash too.
            var n = Physics.OverlapSphereNonAlloc(_position, half * 0.45f, _hits, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                if (Array.IndexOf(_ownColliders, _hits[i]) >= 0 || _hits[i].GetComponentInParent<GroundView>() != null) continue;
                Crash($"hit {_hits[i].name}", "hit");
                return;
            }

            // HUD: altitude, climb, speed, throttle, and where the wreck is (distance, and left/right of your heading).
            var to = Wreck - _position;
            var flat = new Vector3(to.x, 0f, to.z);
            var heading = Vector3.SignedAngle(Vector3.ProjectOnPlane(_rotation * Vector3.forward, Vector3.up), flat, Vector3.up);
            var turn = math.abs(heading) < 8f ? "ahead" : heading < 0f ? $"{-heading:0} deg left" : $"{heading:0} deg right";
            var power = HoverShip ? (_input.Braking.Value ? "BRAKING" : _input.Descending.Value ? "DOWN" : _input.Lifting.Value ? "UP" : "ASSIST") : Assisted
                ? $"SET {_throttle * A.MaxSpeed * (_input.Sprinting.Value ? math.max(1f, A.BoostSpeed) : 1f):0} m/s{(hovering ? "  HOVER" : "")}"
                : $"THROTTLE {_throttle * 100f:0}%{(boost > 1f ? " BOOST" : "")}";
            var toPad = _pad - _position;
            var padFlat = new Vector3(toPad.x, 0f, toPad.z);
            var padHeading = Vector3.SignedAngle(Vector3.ProjectOnPlane(_rotation * Vector3.forward, Vector3.up), padFlat, Vector3.up);
            var padTurn = padFlat.magnitude < 6f ? "below" : math.abs(padHeading) < 8f ? "ahead"
                        : padHeading < 0f ? $"{-padHeading:0} deg left" : $"{padHeading:0} deg right";
            var hud = $"ALT {altitude:0} m    CLIMB {-vertical:+0.0;-0.0} m/s    SPEED {_velocity.magnitude:0} m/s    {power}    " +
                      (Assisted ? $"<color=#46a758>PAD {padFlat.magnitude:0} m {padTurn}</color>" : $"WRECK {flat.magnitude:0} m {turn}");
            // Low down: the three touchdown limits, each green (fine), amber (close) or red (would crash).
            if (altitude < 40f)
                hud += $"\n{Gauge("DESCENT", vertical, L.MaxTouchdownVertical, "m/s")}    " +
                       $"{Gauge("DRIFT", horizontal, L.MaxTouchdownHorizontal, "m/s")}    {Gauge("TILT", tilt, L.MaxTouchdownTilt, "deg")}";
            _view.Flight(_pageHud ? "" : hud, null);

            // For the page's HUD (15 times a second is plenty).
            _stateTimer -= dt;
            if (_stateTimer <= 0f)
            {
                _stateTimer = 1f / 15f;
                _state.Flying.Value = true;
                _state.Crashed.Value = false;
                _state.Landed.Value = false;
                _state.Assisted.Value = Assisted || HoverShip;
                _state.HoverShip.Value = HoverShip;
                _state.Altitude.Value = altitude;
                _state.Climb.Value = _velocity.y;   // true vertical speed (the gauges below stay relative to the slope)
                _state.Speed.Value = _velocity.magnitude;
                _state.SetSpeed.Value = HoverShip ? 0f : Assisted ? _throttle * A.MaxSpeed * (_input.Sprinting.Value ? math.max(1f, A.BoostSpeed) : 1f) : _throttle * 100f;
                _state.Hovering.Value = hovering;
                _state.Boost.Value = _input.Sprinting.Value;
                _state.PadDistance.Value = (Assisted ? padFlat : flat).magnitude;
                _state.PadBearing.Value = Assisted ? padHeading : heading;
                _state.Descent.Value = vertical;
                _state.Drift.Value = horizontal;
                _state.Tilt.Value = tilt;
                _state.MaxDescent.Value = L.MaxTouchdownVertical;
                _state.MaxDrift.Value = L.MaxTouchdownHorizontal;
                _state.MaxTilt.Value = L.MaxTouchdownTilt;
            }
        }

        /// <summary>
        /// Assisted steering: the mouse turns the heading (around the world's up) and the pitch, rate-capped and
        /// smoothed, pitch limited (no loops); the ship banks into its turns and A/D roll on top. Hovering, pitch and
        /// bank settle to level, and low down level means the slope under you (touchdown tilt is measured against it).
        /// </summary>
        void Steer(Vector2 look, float roll, bool hovering, float altitude, float dt)
        {
            var settle = 1f - math.exp(-L.TurnDamping * dt);
            var yawWant = math.clamp(look.x * L.TurnDegreesPerPixel / dt, -A.MaxTurnRate, A.MaxTurnRate);
            var pitchWant = math.clamp(-look.y * L.TurnDegreesPerPixel / dt, -A.MaxTurnRate, A.MaxTurnRate);
            _yawRate = math.lerp(_yawRate, yawWant, settle);
            _pitchRate = math.lerp(_pitchRate, pitchWant, settle);
            _heading += _yawRate * dt;
            _pitch = math.clamp(_pitch + _pitchRate * dt, -A.MaxPitch, A.MaxPitch);
            var bankWant = math.clamp(-_yawRate * A.BankPerTurn - roll * A.MaxBank, -A.MaxBank * 1.5f, A.MaxBank * 1.5f);
            if (hovering)
            {
                // Settle to level: the pitch eases back unless you're pitching, the bank to a hover's gentle lean.
                if (math.abs(look.y) < 0.5f) _pitch = Mathf.MoveTowards(_pitch, 0f, A.HoverLevelDegreesPerSecond * dt);
                bankWant *= 0.3f;
            }
            var levelRate = altitude < A.LowAltitude ? math.max(1f, A.LowLevelFactor) : 1f;
            _bank = math.lerp(_bank, bankWant, 1f - math.exp(-L.TurnDamping * levelRate * dt));
            _rotation = Quaternion.Euler(_pitch, _heading, _bank);
            if (hovering && altitude < A.LowAltitude)
            {
                var normal = _ground.NormalAt(_position);
                var toSlope = Quaternion.FromToRotation(Vector3.up, normal);
                // Fully on the slope by 60% of the way down from low_altitude, so the TILT gauge is green in time.
                _rotation = Quaternion.Slerp(_rotation, toSlope * _rotation, math.saturate((A.LowAltitude - altitude) / (A.LowAltitude * 0.4f)));
            }
        }

        /// <summary>The landing pad: the flattest spot in a ring round the wreck, marked with a ring on the ground.</summary>
        void Pad()
        {
            _pad = Wreck;
            if (!Assisted) return;
            var best = float.MaxValue;
            for (var r = A.PadMinDistance; r <= A.PadMaxDistance + 0.01f; r += math.max(1f, (A.PadMaxDistance - A.PadMinDistance) / 3f))
                for (var a = 0; a < 360; a += 15)
                {
                    var p = Wreck + Quaternion.Euler(0f, a, 0f) * Vector3.forward * r;
                    var slope = Vector3.Angle(_ground.NormalAt(p), Vector3.up);
                    // The flattest, and of those the nearest the wreck.
                    var score = slope + r * 0.02f;
                    if (score >= best) continue;
                    best = score;
                    _pad = p;
                }
            _pad.y = _ground.HeightAt(_pad);
            _padMarker = _view.LandingPad;
            if (_padMarker == null) return;
            _padMarker.SetPositionAndRotation(_pad + Vector3.up * 0.06f, Quaternion.FromToRotation(Vector3.up, _ground.NormalAt(_pad)));
            _padMarker.gameObject.SetActive(true);
            Debug.Log($"[Landing] pad {Vector3.Distance(_pad, Wreck):0} m from the wreck, {best:0.0} slope score");
        }

        /// <summary>
        /// Six-axis flight with assist (FlightHover). Aim: the mouse yaws and pitches the nose, Q/E roll (rates eased a
        /// little, so the ship has mass). Travel: W/S, A/D and Space/C ask for a velocity in the ship's frame and the
        /// assist eases the real one towards it, so aim and travel stay independent: swing the nose and you slide on a
        /// moment before the thrusters catch up; let go of a key and that motion eases out; X brakes hard along your
        /// travel. Gravity is cancelled (a released ship holds where it is). Near the ground a descent eases to the
        /// touchdown speed, and low with no steering the ship levels itself to the slope, so landing stays forgiving.
        /// </summary>
        void HoverMove(Vector2 look, Vec2 move, float dt, out bool descending, out float boost)
        {
            boost = _input.Sprinting.Value ? math.max(1f, H.Boost) : 1f;
            descending = _input.Descending.Value;
            var roll = _input.Roll.Value;

            // Aim.
            var settle = 1f - math.exp(-math.max(0.5f, H.TurnSmoothing) * dt);
            var want = new Vector3(math.clamp(-look.y * L.TurnDegreesPerPixel / dt, -H.TurnRate, H.TurnRate),
                                   math.clamp(look.x * L.TurnDegreesPerPixel / dt, -H.TurnRate, H.TurnRate),
                                   -roll * H.RollRate);
            _spin = Vector3.Lerp(_spin, want, settle);
            _rotation = _rotation * Quaternion.Euler(_spin * dt);
            var altitude = _position.y - _ground.HeightAt(_position);
            var steering = math.abs(look.x) + math.abs(look.y) > 0.5f || math.abs(roll) > 0.1f;
            if (!steering && altitude < H.LevelHeight)
            {
                var normal = _ground.NormalAt(_position);
                var nose = Vector3.ProjectOnPlane(_rotation * Vector3.forward, normal);
                if (nose.sqrMagnitude > 1e-4f)
                    _rotation = Quaternion.RotateTowards(_rotation, Quaternion.LookRotation(nose, normal), H.LevelRate * dt);
            }

            // Travel: the velocity the keys ask for, in the ship's frame; the assist eases towards it.
            var forward = move.Y > 0.1f ? H.MaxSpeed * boost * move.Y : move.Y < -0.1f ? H.ReverseSpeed * move.Y : 0f;
            var side = math.abs(move.X) > 0.1f ? H.StrafeSpeed * move.X : 0f;
            var vertical = _input.Lifting.Value ? H.ClimbSpeed : descending ? -H.DescendSpeed : 0f;
            var target = _input.Braking.Value ? Vector3.zero : _rotation * new Vector3(side, vertical, forward);
            var change = target - _velocity;
            var pushing = target.sqrMagnitude > 0.01f && Vector3.Dot(change, target) > 0f;
            var rate = (_input.Braking.Value ? H.HardBrake : pushing ? H.Accel : H.Brake) * dt;
            if (change.magnitude > rate) change = change.normalized * rate;
            _velocity += change;

            // Near the ground, however you're heading down, the descent eases to a safe touchdown: inside 80% of the
            // touchdown limit all the way through the flare (so the gauge stays green), and down to touchdown_speed.
            var safe = L.MaxTouchdownVertical * 0.8f;
            var flare = math.max(1f, H.FlareHeight);
            var cap = altitude < flare ? math.lerp(H.TouchdownSpeed, safe, altitude / flare) : safe + (altitude - flare) * 1.2f;
            // Closing on the ground, not just sinking: drifting over rising ground brings it up at you (slope x
            // horizontal speed), so the floor on vertical speed is the ground's own rate minus the cap.
            var n = _ground.NormalAt(_position);
            var groundRate = n.y > 0.2f ? -(_velocity.x * n.x + _velocity.z * n.z) / n.y : 0f;
            if (_velocity.y - groundRate < -cap) _velocity.y = groundRate - cap;
            _position += _velocity * dt;
            Ship.SetHeat(0f, 0.6f + 0.9f * (_velocity.magnitude / math.max(1f, H.MaxSpeed)) * boost + (pushing ? 0.5f : 0.2f));
        }

        static string Gauge(string label, float value, float limit, string unit)
        {
            var colour = value <= limit * 0.7f ? "#46a758" : value <= limit ? "#e5a23a" : "#e5484d";
            return $"{label} <color={colour}>{math.max(0f, value):0.0} {unit}</color> / {limit:0}";
        }

        void Crash(string why, string reason = "")
        {
            _state.Flying.Value = false;
            _state.Crashed.Value = true;
            _state.CrashReason.Value = reason;
            _phase = Phase.Crashed;
            _phaseTime = 0f;
            _velocity = Vector3.zero;
            _spin = Vector3.zero;
            Ship.SetHeat(0.9f, 0.2f);                                          // scorched
            _view.Flight("", _pageHud ? null : L.CrashedText);
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
            _state.Flying.Value = false;
            _state.Landed.Value = true;
            _velocity = Vector3.zero;
            // Settle on the ground, upright along the slope.
            var forward = Vector3.ProjectOnPlane(_rotation * Vector3.forward, normal).normalized;
            _rotation = Quaternion.LookRotation(forward, normal);
            _position.y = _ground.SurfaceAt(_position) + L.ShipLength * 0.5f * 0.18f;   // resting on the mesh
            Ship.Pose(_position, _rotation);
            Ship.SetHeat(0f, 0.3f);
            SetSolid(true);
            if (_beacon != null) _beacon.gameObject.SetActive(false);
            if (_padMarker != null) _padMarker.gameObject.SetActive(false);
            Debug.Log($"[Landing] landed {Vector3.Distance(_position, Wreck):0} m from the wreck, " +
                      $"{Vector3.Distance(new Vector3(_position.x, 0, _position.z), new Vector3(_pad.x, 0, _pad.z)):0} m from the pad");
            StepOutAsync().Forget();
        }

        /// <summary>Fade, then you stand beside your ship facing the wreck, on foot.</summary>
        async UniTaskVoid StepOutAsync()
        {
            _view.Flight("", _pageHud ? null : L.LandedText);
            await UniTask.Delay(900);
            for (var t = 0f; t < 0.5f; t += Time.deltaTime) { _view.Fade(t * 2f); await UniTask.Yield(); }
            var side = _position + _rotation * Vector3.right * (L.ShipLength * 0.5f + 2f);
            side.y = _ground.SurfaceAt(side) + 0.15f;   // on the mesh you collide with, not just the height function
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
            var floor = _ground.SurfaceAt(_camPos) + 1.5f;                      // never under the ground (its mesh)
            if (_camPos.y < floor) _camPos.y = floor;
            // Assisted: tilt a little with the bank, and widen the view with speed (a sense of speed).
            var camUp = Assisted ? Vector3.Slerp(Vector3.up, _rotation * Vector3.up, math.saturate(A.CameraBank)) : Vector3.up;
            cam.SetPositionAndRotation(_camPos, Quaternion.LookRotation(_position + _rotation * Vector3.forward * L.ShipLength - _camPos, camUp));
            if (Assisted)
                _view.FlightCamera.fieldOfView = math.lerp(_view.FlightCamera.fieldOfView,
                    65f + A.SpeedFov * math.saturate(_velocity.magnitude / math.max(1f, A.MaxSpeed)), 1f - math.exp(-3f * dt));
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

        public void Dispose()
        {
            _state.Flying.Value = false;
            _hudSubscription?.Dispose();
            _done?.TrySetCanceled();
        }
    }
}
