using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The Flight School (content/school.txtpb): a training deck in space, reached from the station's Docking Bay
    /// console, to test-fly your ship and watch its jets. It flies like the planet's hover ship (the same
    /// PlanetScene.landing.hover numbers and the skin's stats, so what you learn here holds down there), holding
    /// itself up the same way, so the lift jets and cold gas work as they do over a planet; but no fuel, no
    /// crashing, and the deck is a floor you can't go through.
    ///
    /// V cycles the camera (chase, side, below, front, orbit), M swaps your fighter for the museum freighter (its
    /// own, slower handling), R tries the next skin (just here: your ship stays as you set it in the hangar), Esc
    /// fades back to the station. The HUD shows speed, height and which jets are firing (ShipView.JetReadout).
    /// </summary>
    public sealed class FlightSchoolPresenter : IStartable, ITickable, IDisposable
    {
        enum View { Chase, Side, Below, Front, Orbit }
        static readonly string[] ViewNames = { "chase", "side", "below", "front", "orbit" };

        readonly PlanetView _view;
        readonly PlayerInput _input;
        readonly PlayerShip _playerShip;
        readonly PlayerVoyage _voyage;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly List<IDisposable> _subscriptions = new();

        ShipView _ship;
        bool _freighter, _leaving, _cycleView, _swapShip, _nextSkin, _exit;
        int _skin;
        View _camera;
        Vector3 _position, _velocity, _spin, _jetSpin, _camPos;
        Quaternion _rotation = Quaternion.identity;
        float _orbit, _fade = 1f, _readoutAt;
        ShipStats _stats = new();

        static FlightSchoolScene S => GameContent.School;
        static FlightLanding L => GameContent.Planet.Landing;
        static FlightHover H => L.Hover;
        float Length => _freighter ? S.FreighterLength : S.FighterLength;
        float Speed => _freighter ? S.FreighterSpeed : math.max(0.1f, _stats.Speed);
        float Agility => _freighter ? S.FreighterAgility : math.max(0.1f, _stats.Agility);

        [Inject]
        public FlightSchoolPresenter(PlanetView view, PlayerInput input, PlayerShip playerShip, PlayerVoyage voyage,
                                     ISubscriber<InputActionPerformed> actions, IPublisher<SceneChangeRequested> scenes)
        {
            _view = view;
            _input = input;
            _playerShip = playerShip;
            _voyage = voyage;
            _actions = actions;
            _scenes = scenes;
        }

        public void Start()
        {
            _view.Bind();
            _view.Fade(1f);
            _view.Dialogue(null, null, 0);
            _view.MapHint(null);
            _view.Hint(S.Hint, 1f);
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                switch (e.Action)
                {
                    case InputAction.SideView: _cycleView = true; break;   // V
                    case InputAction.Map: _swapShip = true; break;         // M
                    case InputAction.UseRelic: _nextSkin = true; break;    // R
                    case InputAction.Cancel: _exit = true; break;          // Esc
                }
            }));

            // Space: your universe's stars, and a sun off to one side so the hull has a lit and a dark side.
            if (RenderSettings.skybox != null && GameContent.Title.Backdrop?.Stars != null)
            {
                RenderSettings.skybox = new Material(RenderSettings.skybox);
                GameContent.Title.Backdrop.Stars.ApplyTo(RenderSettings.skybox);
            }
            Shader.SetGlobalVector("_LnSunPosition", new Vector4(4000f, 2600f, -3000f, 1f));
            Shader.SetGlobalVector("_LnSunColor", new Color(1f, 0.95f, 0.88f));
            Shader.SetGlobalVector("_LnSkyColor", Vector4.zero);
            Shader.SetGlobalVector("_LnGroundColor", Vector4.zero);

            var skin = PlayerShipSkin.Current(_playerShip);
            var fighter = _view.Fighter;
            for (var i = 0; i < fighter.SkinCount; i++) if (fighter.SkinAt(i) == skin) _skin = i;
            _view.Wreck.Hide();
            ShowShip(false);
            _position = new Vector3(0f, S.StartHeight, 0f);
            _camPos = _position - Vector3.forward * (Length * S.ChaseDistance) + Vector3.up * (Length * S.ChaseHeight);
            _view.FlightCamera.gameObject.SetActive(true);
            Debug.Log($"[School] ready: {_stats.Name} ({SkinName}), {fighter.SkinCount} skins");   // marker for ln.py webshot
        }

        string SkinName => _view.Fighter.SkinCount > 0 ? _view.Fighter.SkinAt(_skin) : "";

        /// <summary>Fly the fighter (in skin `_skin`) or the freighter, from where the last one was.</summary>
        void ShowShip(bool freighter)
        {
            _ship?.Hide();
            _freighter = freighter;
            _ship = freighter ? _view.Wreck : _view.Fighter;
            _stats = PlayerShipStats.Of(SkinName);
            _ship.Show(freighter ? null : SkinName, Length);
            _ship.Pose(_position, _rotation);
            _velocity = _spin = _jetSpin = Vector3.zero;
            _position.y = Mathf.Max(_position.y, Floor + Length * 0.5f);
        }

        float Floor => Length * math.max(0.05f, S.FloorClearance);

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (_cycleView) _camera = (View)(((int)_camera + 1) % ViewNames.Length);
            if (_swapShip) ShowShip(!_freighter);
            if (_nextSkin && !_freighter && _view.Fighter.SkinCount > 0)
            {
                _skin = (_skin + 1) % _view.Fighter.SkinCount;
                ShowShip(false);
            }
            if (_exit && !_leaving) Leave().Forget();
            _cycleView = _swapShip = _nextSkin = _exit = false;

            _fade = Mathf.MoveTowards(_fade, _leaving ? 1f : 0f, dt / (_leaving ? 0.5f : 0.8f));
            _view.Fade(_fade);
            if (!_leaving) Fly(dt);
            _ship.Pose(_position, _rotation);
            Aim(dt);

            if (Time.time >= _readoutAt)
            {
                _readoutAt = Time.time + math.max(0.05f, S.ReadoutSeconds);
                var name = _freighter ? "Museum freighter" : $"{_stats.Name} ({SkinName})";
                var jets = _ship.JetReadout();
                _view.Flight($"{S.Title}   {name}   {_velocity.magnitude:0} m/s   height {_position.y:0} m   {ViewNames[(int)_camera]} camera\n" +
                             (jets == "" ? "-" : jets), null);
            }
        }

        /// <summary>The planet's hover flight (LandingPresenter.HoverMove) without the ground, fuel or crashes.</summary>
        void Fly(float dt)
        {
            var pressed = _input.PointerPressed.Value;
            if (pressed && Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            var steering = Cursor.lockState == CursorLockMode.Locked || pressed;
            var look = steering ? new Vector2(_input.Look.Value.X, _input.Look.Value.Y) : Vector2.zero;
            var move = _input.Move.Value;
            var roll = _input.Roll.Value;
            var sp = Speed;
            var ag = Agility;
            var boost = _input.Sprinting.Value ? math.max(1f, H.Boost) : 1f;

            // Aim.
            var settle = 1f - math.exp(-math.max(0.5f, H.TurnSmoothing) * dt);
            var turn = H.TurnRate * ag;
            var want = new Vector3(math.clamp(-look.y * L.TurnDegreesPerPixel / dt, -turn, turn),
                                   math.clamp(look.x * L.TurnDegreesPerPixel / dt, -turn, turn),
                                   -roll * H.RollRate * ag);
            _spin = Vector3.Lerp(_spin, want, settle);
            _rotation = _rotation * Quaternion.Euler(_spin * dt);
            var height = _position.y;
            var turning = math.abs(look.x) + math.abs(look.y) > 0.5f || math.abs(roll) > 0.1f;
            if (!turning && height < H.LevelHeight)
            {
                // Low over the deck with no steering, it levels itself (as over the ground).
                var nose = Vector3.ProjectOnPlane(_rotation * Vector3.forward, Vector3.up);
                if (nose.sqrMagnitude > 1e-4f)
                    _rotation = Quaternion.RotateTowards(_rotation, Quaternion.LookRotation(nose, Vector3.up), H.LevelRate * ag * dt);
            }

            // Travel: the velocity the keys ask for, in the ship's frame; the assist eases towards it.
            var forward = move.Y > 0.1f ? H.MaxSpeed * sp * boost * move.Y : move.Y < -0.1f ? H.ReverseSpeed * sp * move.Y : 0f;
            var side = math.abs(move.X) > 0.1f ? H.StrafeSpeed * sp * move.X : 0f;
            var vertical = _input.Lifting.Value ? H.ClimbSpeed * sp * boost : _input.Descending.Value ? -H.DescendSpeed * sp : 0f;
            var target = _input.Braking.Value ? Vector3.zero : _rotation * new Vector3(side, vertical, forward);
            var change = target - _velocity;
            var pushing = target.sqrMagnitude > 0.01f && Vector3.Dot(change, target) > 0f;
            var rate = (_input.Braking.Value ? H.HardBrake : pushing ? H.Accel : H.Brake) * ag * dt;
            if (change.magnitude > rate) change = change.normalized * rate;
            _velocity += change;
            _position += _velocity * dt;

            // The deck is a floor; its edge (and a ceiling well above) keep you over it.
            var floor = Floor;
            if (_position.y < floor)
            {
                _position.y = floor;
                if (_velocity.y < 0f) _velocity.y = 0f;
            }
            var edge = S.DeckSize * 0.45f;
            _position.x = Mathf.Clamp(_position.x, -edge, edge);
            _position.z = Mathf.Clamp(_position.z, -edge, edge);
            _position.y = Mathf.Min(_position.y, S.DeckSize * 0.5f);

            // Jets, as on the planet: this frame's change of velocity, plus holding the ship up and its speed.
            _ship.SetHeat(0f, 0.6f + 0.9f * (_velocity.magnitude / math.max(1f, H.MaxSpeed * sp)) * boost + (pushing ? 0.5f : 0.2f));
            var toShip = Quaternion.Inverse(_rotation);
            var ask = toShip * (change / math.max(rate, 1e-4f));
            var hold = toShip * (Vector3.up * 0.3f + _velocity / math.max(1f, H.MaxSpeed * sp) * 0.5f);
            _ship.SetThrust(ask + hold, JetTurn(dt, H.TurnRate * ag), boost);
        }

        /// <summary>The jets' turn (as LandingPresenter.JetTurn): kick to start and stop a turn, a little burn during it.</summary>
        Vector3 JetTurn(float dt, float rate)
        {
            rate = math.max(1f, rate);
            var change = (_spin - _jetSpin) / math.max(dt, 1e-4f);
            _jetSpin = _spin;
            var t = change / (rate * 4f) + _spin / rate * 0.4f;
            return new Vector3(math.clamp(t.x, -1f, 1f), math.clamp(t.y, -1f, 1f), math.clamp(t.z, -1f, 1f));
        }

        /// <summary>The camera for the current view, eased towards its spot, never under the deck.</summary>
        void Aim(float dt)
        {
            var cam = _view.FlightCamera.transform;
            var l = Length;
            var up = _rotation * Vector3.up;
            var fwd = _rotation * Vector3.forward;
            var right = _rotation * Vector3.right;
            Vector3 want, at = _position;
            var camUp = Vector3.up;
            switch (_camera)
            {
                case View.Side:
                    want = _position + right * (l * S.SideDistance) + up * (l * 0.15f);
                    break;
                case View.Below:
                    want = _position - up * (l * S.SideDistance * 0.8f) - fwd * (l * 0.35f);
                    camUp = fwd;
                    break;
                case View.Front:
                    want = _position + fwd * (l * S.SideDistance) + up * (l * 0.25f);
                    break;
                case View.Orbit:
                    _orbit += S.OrbitDegreesPerSecond * dt;
                    want = _position + Quaternion.Euler(18f, _orbit, 0f) * Vector3.back * (l * S.OrbitDistance);
                    break;
                default:
                {
                    var flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
                    if (flat.sqrMagnitude < 1e-4f) flat = up;
                    want = _position - flat.normalized * (l * S.ChaseDistance) + Vector3.up * (l * S.ChaseHeight);
                    at = _position + fwd * l;
                    break;
                }
            }
            _camPos = Vector3.Lerp(_camPos, want, 1f - math.exp(-6f * dt));
            if (_camPos.y < 1.5f) _camPos.y = 1.5f;   // the deck is opaque: stay above it
            var look = at - _camPos;
            if (look.sqrMagnitude < 1e-4f) look = fwd;
            if (Mathf.Abs(Vector3.Dot(look.normalized, camUp)) > 0.98f) camUp = fwd;
            cam.SetPositionAndRotation(_camPos, Quaternion.LookRotation(look, camUp));
        }

        async UniTaskVoid Leave()
        {
            _leaving = true;
            _ship.EnginesOff();
            Cursor.lockState = CursorLockMode.None;
            _view.Hint("", 0f);
            await UniTask.Delay(TimeSpan.FromSeconds(0.6));
            _voyage.Training.Value = true;   // the station puts you back beside the console
            Debug.Log("[School] back to the station");
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
