using System;
using LuckyNumbers.Core;
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
        /// <summary>Multiplies walking and running speed (1 = normal; relics can slow you).</summary>
        float SpeedScale { get; set; }
        /// <summary>Head yaw/pitch (degrees) the player takes over from.</summary>
        void Look(float yawDegrees, float pitchDegrees);
        /// <summary>What the hands hold (null: nothing, or still reaching for it).</summary>
        Rigidbody Held { get; }
        /// <summary>Reach for and pick up this thing as if E were pressed looking at it (scripts: the range's bake run).</summary>
        bool PickUp(Rigidbody thing);
        /// <summary>Put down what's held, as E does (the equipped gun: holstered, not dropped).</summary>
        void Drop();
        /// <summary>
        /// The equipped weapon's model (the pack's weapon slot, its item's `model`), which the hands draw when they're
        /// free and E holsters; null: none equipped, or it has no model yet.
        /// </summary>
        Rigidbody Equipped { get; }
        /// <summary>
        /// Asked as a held gun fires, with what the crosshair is on and how far (the station range's attack rolls).
        /// False: the shot misses, so the bolt goes wide of it. Null: every shot flies true.
        /// </summary>
        Func<Collider, float, bool> ShotCheck { get; set; }
    }

    /// <summary>
    /// Walk + look on the ground (CharacterController): WASD, mouse (pointer lock on click), Shift to run,
    /// Space to jump. Feel is procedural: head bob from stride, arms sway against the look, pump when running,
    /// rise in the air, shake after the crash. Hands: palms go flat on things you face up close; E picks up a
    /// pebble (crouching and stepping in for it), E drops it, click throws it. A gun (WeaponView) is held in view
    /// instead, both hands on it (IK to its grip and foregrip), and click fires it; a melee weapon (MeleeView) is held
    /// by the handle in the right fist, and click swings it. The gun in your pack's weapon slot is drawn: its model
    /// (SceneBuilder parks one per weapon item, inactive, under "Equipped weapons") comes up from the hip whenever the
    /// hands are free; E holsters it (and draws it again), unequipping or dropping it from the pack puts it away.
    /// </summary>
    [Presenter]
    public sealed class FirstPersonPresenter : IStartable, ITickable, IDisposable, IFirstPerson
    {
        const int Left = 0, Right = 1;   // right: the carrying hand
        const float MaxLean = 0.25f;   // metres the head (and arms with it) may lean toward a far reach
        readonly PlanetScene _config;
        readonly PlayerView _player;
        readonly ArmsView _arms;
        readonly PlayerInput _input;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly IPublisher<ItemHandled> _handled;
        readonly IInventory _pack;
        IDisposable _subscription;
        float _reportAt = float.MaxValue;   // when to toast how well the hands settled on what they just picked up

        float _yaw, _pitch, _vertical, _stride, _moving, _landDip, _errorLogAt, _sprint, _air, _crouch, _awakeAt = -1f;
        bool _jump, _interact, _wasGrounded = true, _wasPressed, _lockSeen;
        Vector3 _sway, _lean;

        // Touch: per hand, how far in (0..1) and where.
        readonly float[] _touch = new float[2];
        readonly bool[] _touching = new bool[2];
        readonly (Vector3 wrist, Vector3 facing, Vector3 along)[] _lastTouch = new (Vector3, Vector3, Vector3)[2];
        readonly RaycastHit[] _hits = new RaycastHit[8];

        // Carry: reaching for (_reachT 0..1, grabbed at 0.5), holding, throwing (_throwT seconds).
        Rigidbody _reaching, _held;
        float _reachT, _crouchFor, _carry, _throwT = -1f, _throwAt;
        Vector3 _reachFrom;

        // A gun in hand: held in view (parented to the eyes), the hands on it; recoil kick (metres, decays).
        WeaponView _weapon;
        MeleeView _melee;
        Vector3 _armsShift;   // the whole arms rig, shifted toward the held gun (eased)
        float _kick;
        Vector3 _viewBob;
        Quaternion _viewTilt = Quaternion.identity;

        public bool Active { get; set; }
        public float SpeedScale { get; set; } = 1f;
        public Func<Collider, float, bool> ShotCheck { get; set; }

        public Rigidbody Held => _held;

        // The equipped gun: its model (null: none), which item model that is, and whether E put it away.
        Rigidbody _equipped;
        string _equippedModel = "";
        bool _holstered, _drawing;
        public Rigidbody Equipped => _equipped;

        public bool PickUp(Rigidbody thing)
        {
            if (thing != null && thing != _equipped && _equipped != null && (_held == _equipped || _reaching == _equipped))
            {
                Holster();   // hands free for it; the gun stays away until E
                _holstered = true;
            }
            if (thing == null || _held != null || _reaching != null || _throwT >= 0f) return false;
            _reaching = thing;
            _reachT = 0f;
            _crouchFor = math.clamp(_arms.ShoulderPosition(Right).y - thing.position.y - _arms.Reach(Right) * 0.8f, 0f, _config.CrouchDepth);
            return true;
        }

        public void Drop()
        {
            if (_held != null && _held == _equipped) { Holster(); _holstered = true; }
            else if (_held != null && _throwT < 0f) Release(Vector3.zero);
        }

        public void Look(float yawDegrees, float pitchDegrees)
        {
            _yaw = yawDegrees;
            _pitch = pitchDegrees;
        }

        [Inject]
        public FirstPersonPresenter(PlanetScene config, PlayerView player, ArmsView arms, PlayerInput input,
                                    ISubscriber<InputActionPerformed> actions, IPublisher<ItemHandled> handled, IInventory pack)
        {
            _pack = pack;
            _handled = handled;
            _config = config;
            _player = player;
            _arms = arms;
            _input = input;
            _actions = actions;
        }

        public void Start()
        {
            _subscription = _actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Jump) _jump = true;
                if (e.Action == InputAction.Interact) _interact = true;
            });
            if (_config.Hands != null) _arms.Init(_config.Hands);
            _arms.Play(_config.IdleClip);
            _arms.SetLife(_config.ArmBreathDegrees, _config.ArmSwingDegrees, _config.FingerFidgetDegrees);
            _arms.SetFeel(_config.SprintPump, _config.AirRaiseDegrees);
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (!Active || dt <= 0f) { _jump = _interact = false; return; }
            if (_awakeAt < 0f) _awakeAt = Time.time;

            // Pointer lock: browsers need a click; Esc releases it. A click while locked throws.
            var locked = Cursor.lockState == CursorLockMode.Locked;
            _lockSeen |= locked;
            var pressed = _input.PointerPressed.Value;
            var click = pressed && !_wasPressed && (locked || !_lockSeen);   // no pointer lock (headless): any click
            _wasPressed = pressed;
            if (pressed && !locked) Cursor.lockState = CursorLockMode.Locked;
            var looking = locked || pressed;
            var look = _input.Look.Value;
            var lookDelta = looking ? new Vector2(look.X, look.Y) : Vector2.zero;
            _yaw += lookDelta.x * _config.LookDegreesPerPixel;
            _pitch = math.clamp(_pitch - lookDelta.y * _config.LookDegreesPerPixel, -85f, 85f);

            var body = _player.Body;
            var grounded = body.isGrounded;
            var move = _input.Move.Value;
            var running = _input.Sprinting.Value;
            var speed = (running ? _config.SprintSpeed : _config.WalkSpeed) * SpeedScale;
            var yawRot = Quaternion.Euler(0f, _yaw, 0f);
            var wish = yawRot * new Vector3(move.X, 0f, move.Y);
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            if (_reaching != null && _reachT < 0.5f) wish += StepIn(_reaching.position) / math.max(0.1f, speed);   // step in to grab

            if (grounded && _vertical < 0f) _vertical = -2f;                 // stick to slopes
            if (_jump && grounded && _reaching == null) _vertical = _config.JumpSpeed;
            _jump = false;
            _vertical -= _config.Gravity * dt;
            body.Move((wish * speed + Vector3.up * _vertical) * dt);
            body.transform.rotation = yawRot;

            if (!_wasGrounded && body.isGrounded) _landDip = 1f;              // landing thump
            _wasGrounded = body.isGrounded;
            _landDip = math.max(0f, _landDip - dt * 3f);

            // Head bob from stride, scaled by how fast we move over ground; crouch while grabbing.
            var planar = new Vector3(body.velocity.x, 0f, body.velocity.z).magnitude;
            // Eased: velocity stops dead when you let go of the keys, and swing/bob scaled by it would snap to rest.
            var movingNow = body.isGrounded ? math.saturate(planar / math.max(0.1f, _config.WalkSpeed)) : 0f;
            _moving = math.lerp(_moving, movingNow, 1f - math.exp(-(movingNow > _moving ? 8f : 5f) * dt));
            var moving = _moving;
            _stride += dt * _config.BobFrequency * 2f * math.PI * math.max(0.2f, planar / math.max(0.1f, _config.WalkSpeed));
            var bobY = math.abs(math.sin(_stride)) * _config.BobAmount * moving - _landDip * 0.08f;
            var bobX = math.sin(_stride) * _config.BobAmount * 0.5f * moving;
            // Lean in toward far reaches (head and arms together, so the arm mesh's open ends stay out of view):
            // integrate the overshoot the arms report, never leaning back past upright, ease out when not reaching.
            if (_arms.IsReaching)
            {
                _lean = Vector3.ClampMagnitude(_lean + _arms.Reaching * (1f - math.exp(-8f * dt)), MaxLean);
                var back = Vector3.Dot(_lean, body.transform.forward);
                if (back < 0f) _lean -= body.transform.forward * back;
            }
            else _lean = Vector3.Lerp(_lean, Vector3.zero, 1f - math.exp(-6f * dt));
            _player.Head.localPosition = new Vector3(0f, _config.EyeHeight + bobY - _crouch, 0f) + Quaternion.Inverse(body.transform.rotation) * _lean;
            _player.Head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // Body feel: running (pump + run clip), airborne (arms up), shaky hands after the crash.
            var ease = 1f - math.exp(-6f * dt);
            _sprint = math.lerp(_sprint, running && moving > 0.5f ? 1f : 0f, ease);
            _air = math.lerp(_air, grounded ? 0f : 1f, 1f - math.exp(-(grounded ? 10f : 4f) * dt));
            var since = Time.time - _awakeAt;
            var tremor = _config.CrashTremorDegrees * math.pow(math.saturate(1f - since / math.max(1f, _config.TremorSeconds)), 2f);
            _arms.Play(_sprint > 0.5f ? _config.SprintClip : _config.IdleClip, 0.35f);
            _arms.SetMotion(_stride, moving, _sprint, _air, tremor);

            // Arms lag against the look and follow the stride.
            var target = new Vector3(-lookDelta.y, lookDelta.x, 0f) * _config.Sway;
            _sway = Vector3.Lerp(_sway, target, 1f - math.exp(-10f * dt));
            _viewBob = new Vector3(bobX, bobY * 0.6f - _landDip * 0.04f, 0f);
            _viewTilt = Quaternion.Euler(math.clamp(_sway.x, -6f, 6f), math.clamp(_sway.y, -6f, 6f), -bobX * 30f);
            _armsShift = Vector3.Lerp(_armsShift, _weapon != null && _held != null ? _weapon.ArmsShift : Vector3.zero, 1f - math.exp(-8f * dt));
            _arms.Pose(_viewBob + _armsShift, _viewTilt);
            Hands(dt, click);
            _interact = false;
        }

        /// <summary>Horizontal velocity (m/s) to close in until within arm's length of a point.</summary>
        Vector3 StepIn(Vector3 point)
        {
            var to = point - _player.Body.transform.position;
            to.y = 0f;
            var gap = to.magnitude - 0.45f;
            return gap > 0f ? to.normalized * math.min(gap * 5f, 2.5f) : Vector3.zero;
        }

        void Hands(float dt, bool click)
        {
            if (_arms.ArmCount < 2) return;
            var eye = _player.Camera.transform;
            if (_config.DebugReach) { DebugReach(eye); return; }
            Carry(dt, eye, click);
            if (_weapon != null) return;   // both hands are on the gun
            for (var s = 0; s < 2; s++)
                if (s != Right || (_reaching == null && _held == null && _throwT < 0f)) Touch(s, dt, eye);
        }

        /// <summary>IK self-test: hands trace circles in front of you, cycling every finger pose.</summary>
        void DebugReach(Transform eye)
        {
            var t = Time.time;
            var poses = _config.Hands.Poses;
            var pose = poses.Count > 0 ? poses[(int)(t / 1.5f) % poses.Count].Name : null;
            for (var s = 0; s < 2; s++)
            {
                var side = s == 0 ? -1f : 1f;   // L, R
                var target = eye.position + eye.forward * 0.42f + eye.right * (side * 0.2f + 0.09f * math.cos(t * 1.7f + s))
                           + eye.up * (-0.14f + 0.09f * math.sin(t * 2.3f + s * 2f));
                _arms.ReachTo(s, target, 1f, eye.forward * -1f, eye.up);   // palms toward you: watch the fingers
                if (_arms.CurrentPose(s) != pose && s == 1) Debug.Log($"[Hands] pose {pose}");
                _arms.SetPose(s, pose);
            }
            if (t > _errorLogAt)
            {
                _errorLogAt = t + 2f;
                Debug.Log($"[Hands] IK error L {_arms.LastError[0]:0.0000} m R {_arms.LastError[1]:0.0000} m");
            }
        }

        /// <summary>A palm goes flat on walls/hulls/stones you're facing up close (not the ground).</summary>
        void Touch(int s, float dt, Transform eye)
        {
            var side = s == 0 ? -1f : 1f;
            // Where you look, a little toward each hand's side; only things you could touch.
            var dir = Quaternion.AngleAxis(side * 9f, eye.up) * eye.forward;
            var hit = false;
            var best = new RaycastHit { distance = float.MaxValue };
            var n = Physics.RaycastNonAlloc(eye.position, dir, _hits, _config.TouchDistance, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (h.collider.transform.IsChildOf(_player.Body.transform)) continue;                    // not ourselves
                if (h.collider.GetComponentInParent<GroundView>() != null) continue;                     // not the ground
                if (h.rigidbody != null) continue;                                                      // pebbles: pick up instead
                if (Vector3.Distance(_arms.ShoulderPosition(s), h.point) > _arms.Reach(s) * 1.45f) continue; // reachable (small lean)
                if (h.distance < best.distance) { best = h; hit = true; }
            }
            if (hit != _touching[s])
            {
                _touching[s] = hit;
                Debug.Log(hit ? $"[Hands] {Name(s)} hand touches {best.collider.name} at {best.distance:0.00} m" : $"[Hands] {Name(s)} hand lets go");
            }
            _touch[s] = Mathf.MoveTowards(_touch[s], hit ? 1f : 0f, dt * (hit ? 4f : 3f));
            if (_touch[s] <= 0f) { _arms.ReachTo(s, Vector3.zero, 0f); _arms.SetPose(s, null); return; }
            if (hit)
            {
                var up = Vector3.ProjectOnPlane(eye.up + eye.right * side * 0.3f, best.normal).normalized;
                // Wrist sits below the palm's centre and a hair off the surface.
                _lastTouch[s] = (best.point + best.normal * 0.03f - up * 0.07f, -best.normal, up);
            }
            var (w, facing, along) = _lastTouch[s];
            _arms.ReachTo(s, w, Smooth(_touch[s]), facing, along);
            _arms.SetPose(s, hit ? "flat" : null);
        }

        /// <summary>Pick up (E), carry, drop (E) or throw (click) a pebble with the right hand.</summary>
        void Carry(float dt, Transform eye, bool click)
        {
            var hold = eye.position + eye.forward * 0.42f + eye.right * 0.17f - eye.up * 0.19f;

            EquippedGun(eye);
            if (_interact && _held != null && _held == _equipped) { Holster(); _holstered = true; _interact = false; }
            if (_interact && _held != null && _throwT < 0f) { Release(Vector3.zero); _interact = false; }
            if (_interact && _held == null && _reaching == null && _throwT < 0f)
            {
                if (!FindPebble(eye, out var pebble))
                {
                    if (_equipped != null && _holstered) { _holstered = false; Draw(eye); return; }   // E again: draw it
                    Debug.Log($"[Hands] nothing to pick up there (looking {_pitch:0} deg down)");
                    return;
                }
                _reaching = pebble;
                _reachT = 0f;
                // Crouch just enough for the shoulder to get within arm's length (with the step-in).
                _crouchFor = math.clamp(_arms.ShoulderPosition(Right).y - pebble.position.y - _arms.Reach(Right) * 0.8f, 0f, _config.CrouchDepth);
                Debug.Log($"[Hands] reaching for {pebble.name} (crouch {_crouchFor:0.00} m)");
            }

            if (_reaching != null)
            {
                _reachT = math.min(1f, _reachT + dt / 0.9f);
                var down = math.sin(math.PI * math.saturate(_reachT * 1.15f));   // crouch down, grab at the bottom, stand back up
                _crouch = _crouchFor * down;
                if (_reachT < 0.5f)
                {
                    // Look at it, and reach a comfortable arm's length toward it (full stretch shows the mesh's cut
                    // shoulder); the stone hops the last bit into the palm when grabbed.
                    var to = _reaching.position - eye.position;
                    var wantPitch = math.degrees(math.atan2(-to.y, new Vector2(to.x, to.z).magnitude));
                    _pitch = math.lerp(_pitch, wantPitch, 1f - math.exp(-8f * dt));
                    var shoulder = _arms.ShoulderPosition(Right);
                    var reach = Vector3.ClampMagnitude(_reaching.position + Vector3.up * 0.06f - shoulder, _arms.Reach(Right) * 0.85f);
                    _arms.SetPose(Right, "open");   // anticipation: fingers open on the way down
                    _arms.ReachTo(Right, shoulder + reach, Smooth(_reachT * 2f), Vector3.down, eye.forward);
                }
                else if (_reaching.TryGetComponent<MeleeView>(out var blade))
                {
                    // A melee weapon: the right fist closes on the handle and brings it up to the ready.
                    if (_melee == null) GrabMelee(_reaching, blade, eye);
                    HoldMelee(dt, false);
                }
                else if (_reaching.TryGetComponent<WeaponView>(out var gun))
                {
                    // A gun: the right hand brings it up into view; the left joins it on the way.
                    if (_weapon == null) GrabWeapon(_reaching, gun, eye);
                    HoldWeapon(eye, dt, Smooth((_reachT - 0.5f) * 2f));
                }
                else
                {
                    if (_reaching.transform.parent != _arms.HandBone(Right)) Grab(_reaching);
                    SnapToPalm(_reaching, dt);
                    _arms.SetPose(Right, "grip");
                    _reachFrom = Vector3.Lerp(_reachFrom == Vector3.zero ? _reaching.position : _reachFrom, hold, Smooth((_reachT - 0.5f) * 2f));
                    _arms.ReachTo(Right, _reachFrom, 1f, Vector3.Lerp(Vector3.down, eye.up, Smooth((_reachT - 0.5f) * 2f)), eye.forward);
                }
                if (_reachT >= 1f)
                {
                    _held = _reaching; _reaching = null; _reachFrom = Vector3.zero; _carry = 1f; _drawing = false;
                    if (_weapon != null || _melee != null) _reportAt = Time.time + 1f;   // once the hold has settled
                }
                return;
            }
            _crouch = math.max(0f, _crouch - dt * 2f);

            if (_held != null && _melee != null)
            {
                if (click) _melee.TrySwing();
                HoldMelee(dt, true);
                if (Time.time >= _reportAt)
                {
                    _reportAt = float.MaxValue;
                    var (rt, rf) = _arms.GraspContacts[Right];
                    Toast(_held, "holding", $"right hand IK error {_arms.LastError[Right] * 100f:0.0} cm; fingers on it {rt}/{rf}");
                }
                return;
            }

            if (_held != null && _weapon != null)
            {
                HoldWeapon(eye, dt, 1f);
                if (Time.time >= _reportAt)
                {
                    _reportAt = float.MaxValue;
                    var (lt, lf) = _arms.GraspContacts[Left];
                    var (rt, rf) = _arms.GraspContacts[Right];
                    Toast(_held, "holding", $"IK error: left {_arms.LastError[Left] * 100f:0.0} cm, right {_arms.LastError[Right] * 100f:0.0} cm; " +
                                            $"fingers on it: left {lt}/{lf}, right {rt}/{rf}");
                }
                if (Time.time > _errorLogAt)
                {
                    _errorLogAt = Time.time + 2f;
                    Debug.Log($"[Hands] fingers inside {_held.name} over the last 2 s: left {_arms.GraspClippingMax[Left]}, right {_arms.GraspClippingMax[Right]} (at most); now left [{_arms.GraspClippingNames[Left]}] right [{_arms.GraspClippingNames[Right]}]");
                    _arms.GraspClippingMax[Left] = _arms.GraspClippingMax[Right] = 0;
                    Debug.Log($"[Hands] holding {_held.name}: IK error L {_arms.LastError[Left]:0.000} m R {_arms.LastError[Right]:0.000} m; " +
                              $"shoulders (eye space) L {eye.InverseTransformPoint(_arms.ShoulderPosition(Left))} R {eye.InverseTransformPoint(_arms.ShoulderPosition(Right))}");
                }
                if (click) Fire(eye);
                return;
            }

            if (_held != null && _throwT < 0f)
            {
                SnapToPalm(_held, dt);
                _arms.ReachTo(Right, hold, 1f, eye.up, eye.forward);   // palm up, showing it
                _arms.SetPose(Right, "grip");
                if (click)
                {
                    _throwT = 0f;
                    _throwAt = _arms.PlayOnce(Right, _config.ThrowClip) * 0.38f;   // let go near full extension
                    Debug.Log("[Hands] throw");
                }
                return;
            }

            if (_throwT >= 0f)
            {
                _throwT += dt;
                _carry = math.max(0f, _carry - dt * 8f);   // the throw clip takes the arm
                _arms.ReachTo(Right, hold, _carry, eye.up, eye.forward);
                if (_held != null && _throwT >= _throwAt)
                {
                    Release((eye.forward + eye.up * 0.15f).normalized * _config.ThrowSpeed);
                    _arms.SetPose(Right, "open");
                }
                if (_throwT > _throwAt + 0.5f) { _throwT = -1f; _arms.SetPose(Right, null); }
            }
        }

        /// <summary>The loose thing (a non-kinematic rigidbody) in reach closest to where you look, within a 15 degree cone.</summary>
        bool FindPebble(Transform eye, out Rigidbody pebble)
        {
            pebble = null;
            var best = 15f;
            var n = Physics.OverlapSphereNonAlloc(eye.position, _config.GrabDistance, _near, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var rb = _near[i].attachedRigidbody;
                if (rb == null || rb.isKinematic) continue;
                var angle = Vector3.Angle(eye.forward, rb.worldCenterOfMass - eye.position);
                if (angle < best) { best = angle; pebble = rb; }
            }
            return pebble != null;
        }

        readonly Collider[] _near = new Collider[32];

        void Grab(Rigidbody rb)
        {
            rb.isKinematic = true;
            // Interpolation would keep moving the transform to the body's smoothed physics pose, fighting the hand
            // it's parented to (the stone lagged into the palm on the move). Off while held, back on when thrown.
            rb.interpolation = RigidbodyInterpolation.None;
            if (rb.TryGetComponent<Collider>(out var c)) c.enabled = false;
            _arms.Hold(Right, rb.transform);
            Debug.Log($"[Hands] grabbed {rb.name}");
            Toast(rb, "picked up", "carried on the palm - click throws");
        }

        /// <summary>
        /// Ease a held thing onto the palm in the hand's own space (it's parented to the hand bone). Easing toward a
        /// world point instead lags a frame behind the hand's animation and IK, so it sank into the hand on the move.
        /// </summary>
        void SnapToPalm(Rigidbody rb, float dt)
        {
            var t = rb.transform;
            var radius = t.TryGetComponent<Renderer>(out var r) ? (r.bounds.extents.x + r.bounds.extents.y + r.bounds.extents.z) / 3f : 0.05f;
            t.localPosition = Vector3.Lerp(t.localPosition, _arms.PalmRest(Right, radius), 1f - math.exp(-25f * dt));
        }

        void GrabWeapon(Rigidbody rb, WeaponView gun, Transform eye)
        {
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            if (rb.TryGetComponent<Collider>(out var c)) c.enabled = false;
            rb.transform.SetParent(eye, true);   // eased into the hold pose from where it lay
            ArmsView.SetLayer(rb.transform, _arms.gameObject.layer);
            _weapon = gun;
            _kick = 0f;
            _arms.SetTriggerFinger(Right, true);
            Grasp(rb, true, Left, Right);
            Debug.Log($"[Hands] {(_drawing ? "drew" : "picked up")} {rb.name}");
            Toast(rb, _drawing ? "drew" : "picked up", _drawing ? "equipped - E holsters it" : $"gun - scale {gun.HeldScale:0.00}, arms shift {gun.ArmsShift.x * 100f:0} cm");
        }

        void GrabMelee(Rigidbody rb, MeleeView blade, Transform eye)
        {
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            if (rb.TryGetComponent<Collider>(out var c)) c.enabled = false;
            rb.transform.SetParent(eye, true);   // eased into the ready from where it lay
            ArmsView.SetLayer(rb.transform, _arms.gameObject.layer);
            _melee = blade;
            blade.Take(_player.Body.transform);
            Grasp(rb, true, Right);
            Debug.Log($"[Hands] {(_drawing ? "drew" : "picked up")} {rb.name}");
            Toast(rb, _drawing ? "drew" : "picked up", _drawing ? "equipped - click swings, E puts it away" : "melee - click swings");
        }

        /// <summary>
        /// Pose the melee weapon (camera space: at the ready, or along its swing), swaying with the arms, and close
        /// the right fist on its handle. `held`: fully in hand (snappy), else still coming up from the pickup (eased).
        /// </summary>
        void HoldMelee(float dt, bool held)
        {
            var blade = _melee;
            var t = blade.transform;
            blade.ViewPose(out var fist, out var rot);
            fist += _viewBob;
            rot = _viewTilt * rot;
            var pos = fist - rot * blade.Grip;
            var k = blade.Swinging ? 1f : 1f - math.exp(-(held ? 30f : 9f) * dt);   // a swing is posed exactly
            t.localPosition = Vector3.Lerp(t.localPosition, pos, k);
            t.localRotation = Quaternion.Slerp(t.localRotation, rot, k);
            blade.FistOn(out var wrist, out var palm, out var along);
            _arms.ReachTo(Right, wrist, 1f, palm, along);
            _arms.SetPose(Right, "fist");
        }

        /// <summary>
        /// Ease the gun into its hold (camera space: grip at its hold offset, barrel converging on the crosshair a way
        /// ahead, swaying and bobbing with the arms, kicked back by recoil), then put the hands on it: the right on
        /// the grip, the left (`left` 0..1) on the foregrip.
        /// </summary>
        void HoldWeapon(Transform eye, float dt, float left)
        {
            var gun = _weapon;
            var t = gun.transform;
            _kick = math.lerp(_kick, 0f, 1f - math.exp(-14f * dt));
            var hold = gun.HoldOffset + _viewBob - Vector3.forward * _kick;
            var aim = Quaternion.LookRotation(new Vector3(0f, 0f, 25f) - hold, Vector3.up);
            var rot = _viewTilt * aim * Quaternion.Euler(-_kick * 160f, 0f, 0f);   // muzzle climbs with the kick
            var pos = hold - rot * (gun.Grip * gun.HeldScale);
            var k = 1f - math.exp(-(left < 1f ? 9f : 40f) * dt);
            t.localPosition = Vector3.Lerp(t.localPosition, pos, k);
            t.localRotation = Quaternion.Slerp(t.localRotation, rot, k);
            t.localScale = Vector3.Lerp(t.localScale, Vector3.one * gun.HeldScale, k);

            // Each hand on its socket (WeaponView: by default the right palm on the pistol grip's left side, knuckles
            // forward and down its rake; the left palm up under the handguard): the wrist sits back along the fingers
            // and off the palm from where the hand closes. The fingers then curl onto the gun (ArmsView's grasp).
            var along = t.TransformDirection(gun.GripAlong).normalized;
            var palm = Vector3.ProjectOnPlane(t.TransformDirection(gun.GripPalm), along).normalized;
            _arms.ReachTo(Right, t.TransformPoint(gun.Grip) - along * 0.075f - palm * 0.035f, 1f, palm, along);
            _arms.SetPose(Right, "grip");
            along = t.TransformDirection(gun.ForeAlong).normalized;
            palm = Vector3.ProjectOnPlane(t.TransformDirection(gun.ForePalm), along).normalized;
            _arms.ReachTo(Left, t.TransformPoint(gun.Foregrip) - along * 0.075f - palm * 0.035f, Smooth(left), palm, along);
            _arms.SetPose(Left, left > 0.3f ? "grip" : "open");
        }

        /// <summary>Fire at what the crosshair is on (first hit that isn't us), or far ahead.</summary>
        void Fire(Transform eye)
        {
            var target = eye.position + eye.forward * 80f;
            var best = float.MaxValue;
            Collider on = null;
            var n = Physics.RaycastNonAlloc(eye.position, eye.forward, _hits, 80f, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                if (_hits[i].collider.transform.IsChildOf(_player.Body.transform)) continue;
                if (_hits[i].distance < best) { best = _hits[i].distance; target = _hits[i].point; on = _hits[i].collider; }
            }
            if (!_weapon.Ready) return;
            if (on != null && ShotCheck != null && !ShotCheck(on, best))
            {
                // A miss: aim past its edge (a random side, across the line of sight) and on downrange.
                var side = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), eye.forward) * eye.up;
                var past = on.bounds.extents.magnitude + 0.35f;
                target = eye.position + (target + side * past - eye.position).normalized * 80f;
            }
            if (_weapon.TryFire(target, _player.Body.transform)) _kick += _weapon.Recoil;
        }

        void Release(Vector3 velocity, bool stow = false)
        {
            var rb = _held;
            _held = null;
            if (rb == null) return;
            Grasp(rb, false, Left, Right);
            _arms.SetTriggerFinger(Right, false);
            if (_melee != null)
            {
                _melee.Take(null);
                _melee = null;
                _arms.SetPose(Right, null);
            }
            if (_weapon != null)
            {
                _weapon = null;
                _arms.ReachTo(Left, Vector3.zero, 0f);
                _arms.SetPose(Left, null);
                _arms.SetPose(Right, null);
            }
            if (stow)
            {
                // Holstered: back under its parking root, out of the world (no body to fall, nothing to pick up).
                rb.transform.SetParent(_equipRoot, false);
                rb.transform.localScale = Vector3.one;
                ArmsView.SetLayer(rb.transform, 0);
                _arms.SetPose(Right, null);
                rb.gameObject.SetActive(false);
                Debug.Log($"[Hands] holstered {rb.name}");
                Toast(rb, "holstered", "E draws it");
                _reportAt = float.MaxValue;
                return;
            }
            rb.transform.SetParent(null, true);
            rb.transform.localScale = Vector3.one;   // a held gun is shrunk for the view
            ArmsView.SetLayer(rb.transform, 0);
            if (rb.TryGetComponent<Collider>(out var c)) c.enabled = true;
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = velocity + _player.Body.velocity;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 8f;
            if (velocity == Vector3.zero) _arms.SetPose(Right, null);
            Debug.Log(velocity == Vector3.zero ? $"[Hands] dropped {rb.name}" : $"[Hands] threw {rb.name} at {velocity.magnitude:0.0} m/s");
            Toast(rb, velocity == Vector3.zero ? "dropped" : "threw", velocity == Vector3.zero ? "" : $"{velocity.magnitude:0.0} m/s");
            _reportAt = float.MaxValue;
        }

        // ---- the equipped gun -------------------------------------------------------------------------------------

        Transform _equipRoot;
        bool _equipRootLooked;

        /// <summary>
        /// Follow the pack's weapon slot: a new weapon (or none) puts the old model away and takes the new one's; the
        /// hands draw it whenever they're free, unless E holstered it.
        /// </summary>
        void EquippedGun(Transform eye)
        {
            var model = _pack.EquippedIn(EquipSlot.Weapon)?.Model ?? "";
            if (model != _equippedModel)
            {
                if (_equipped != null) Holster();
                _equippedModel = model;
                _equipped = EquipModel(model);
                _holstered = false;
                if (_equipped != null && _equipped.TryGetComponent<WeaponView>(out var gun))
                    gun.Source = _pack.EquippedIn(EquipSlot.Weapon)?.Id ?? "default";
            }
            if (_equipped != null && !_holstered && _held == null && _reaching == null && _throwT < 0f) Draw(eye);
        }

        /// <summary>The parked model for an item model name (SceneBuilder: "Weapon &lt;model&gt;" under "Equipped weapons").</summary>
        Rigidbody EquipModel(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            if (!_equipRootLooked)
            {
                _equipRootLooked = true;
                _equipRoot = GameObject.Find("Equipped weapons")?.transform;
                if (_equipRoot == null) Debug.LogWarning("[Hands] no 'Equipped weapons' in this scene (rebuild the scenes): equipped guns stay in the pack");
            }
            var t = _equipRoot != null ? _equipRoot.Find($"Weapon {model}") : null;
            if (t == null)
            {
                if (_equipRoot != null) Debug.LogWarning($"[Hands] no equipped model 'Weapon {model}' (rebuild the scenes)");
                return null;
            }
            return t.GetComponent<Rigidbody>();
        }

        /// <summary>Bring the equipped gun up from the hip: the second half of a pickup (no crouch, no reach down).</summary>
        void Draw(Transform eye)
        {
            var rb = _equipped;
            var t = rb.transform;
            t.SetParent(null, false);
            t.SetPositionAndRotation(eye.position - eye.up * 0.45f + eye.right * 0.2f + eye.forward * 0.15f, eye.rotation);
            t.localScale = Vector3.one;
            rb.isKinematic = true;
            rb.gameObject.SetActive(true);
            _reaching = rb;
            _reachT = 0.5f;
            _crouchFor = 0f;
            _drawing = true;
        }

        /// <summary>Put the equipped gun away (out of the hands, or mid-draw), back where it's parked.</summary>
        void Holster()
        {
            var rb = _equipped;
            if (rb == null) return;
            if (_reaching == rb) { _held = rb; _reaching = null; _reachFrom = Vector3.zero; }
            _drawing = false;
            if (_held == rb) Release(Vector3.zero, stow: true);
            else if (rb.gameObject.activeSelf) rb.gameObject.SetActive(false);
        }

        /// <summary>A held thing's exact-shape colliders on (the fingers close onto them) or off, and those hands' grasp.</summary>
        void Grasp(Rigidbody rb, bool on, params int[] hands)
        {
            foreach (var c in rb.GetComponentsInChildren<MeshCollider>(true)) c.enabled = on;
            var gun = rb.GetComponent<WeaponView>();
            var blade = rb.GetComponent<MeleeView>();
            foreach (var s in hands)
                _arms.SetGrasp(s, on, rb.name, gun != null ? gun.BakedGrip(s) : blade != null ? blade.BakedGrip(s) : null);
        }

        /// <summary>Tell the page (a toast) what the hands did with something: its model's name, not "Weapon x".</summary>
        void Toast(Component thing, string action, string detail)
        {
            var name = thing.name.StartsWith("Weapon ") ? thing.name.Substring("Weapon ".Length) : thing.name;
            _handled.Publish(new ItemHandled(name, action, detail));
            if (action == "holding") Debug.Log($"[Hands] holding {name}: {detail}");   // marker for ln.py webshot
        }

        static float Smooth(float x) { x = math.saturate(x); return x * x * (3f - 2f * x); }
        static string Name(int s) => s == 0 ? "left" : "right";

        public void Dispose() => _subscription?.Dispose();
    }
}
