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
        readonly ShipYard _yard;
        readonly SceneCurtain _curtain;
        readonly List<IDisposable> _subscriptions = new();

        ShipView _ship;
        /// <summary>Key presses since the last Tick, latched by the input subscription; Tick takes and clears them in one go.</summary>
        [Flags] enum Press : byte { None = 0, CycleView = 1 << 0, SwapShip = 1 << 1, NextSkin = 1 << 2, Exit = 1 << 3 }

        Press _press;
        bool _freighter, _leaving;
        int _skin;
        View _camera;
        readonly HoverFlight _flight = new();
        readonly ChaseCamera _chase = new();   // the cameras' spring, lead, roll and rumble (landing.feel)
        readonly ShipSway _sway = new();       // the hull's bank and bob
        Vector3 _position;
        Quaternion _rotation = Quaternion.identity;
        float _orbit, _fade = 1f, _readoutAt;
        ShipStats _stats = new();

        static FlightSchoolScene S => GameContent.School;
        static FlightLanding L => GameContent.Planet.Landing;
        static FlightHover H => L.Hover;
        static FlightFeel Feel => L.Feel;
        float Length => _freighter ? S.FreighterLength : S.FighterLength;
        float Speed => _freighter ? S.FreighterSpeed : math.max(0.1f, _stats.Speed);
        float Agility => _freighter ? S.FreighterAgility : math.max(0.1f, _stats.Agility);

        [Inject]
        public FlightSchoolPresenter(PlanetView view, PlayerInput input, PlayerShip playerShip, PlayerVoyage voyage,
                                     ISubscriber<InputActionPerformed> actions, IPublisher<SceneChangeRequested> scenes,
                                     ShipYard yard, SceneCurtain curtain)
        {
            _yard = yard;
            _curtain = curtain;
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
            _view.Hint(Controls.Pick(S.Hint, S.TouchHint), 1f);
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                switch (e.Action)
                {
                    case InputAction.SideView: _press |= Press.CycleView; break;   // V
                    case InputAction.Map: _press |= Press.SwapShip; break;         // M
                    case InputAction.UseRelic: _press |= Press.NextSkin; break;    // R
                    case InputAction.Cancel: _press |= Press.Exit; break;          // Esc
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
            _curtain.Hold("freighter", _yard.Fit(_view.Wreck));   // its hull from Addressables (M swaps to it)
            ShowShip(false);
            _position = new Vector3(0f, S.StartHeight, 0f);
            _chase.Snap(_position - Vector3.forward * (Length * S.ChaseDistance) + Vector3.up * (Length * S.ChaseHeight), Vector3.up);
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
            // Your own ship flies with your upgrades; the others as they come.
            _stats = SkinName == PlayerShipSkin.Current(_playerShip) ? PlayerShipStats.Of(_playerShip) : PlayerShipStats.Of(SkinName);
            _ship.Show(freighter ? null : SkinName, Length);
            _ship.Pose(_position, _rotation);
            _flight.Stop();
            _position.y = Mathf.Max(_position.y, Floor + Length * 0.5f);
        }

        float Floor => Length * math.max(0.05f, S.FloorClearance);

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;
            var press = _press;
            _press = Press.None;
            if ((press & Press.CycleView) != 0) _camera = (View)(((int)_camera + 1) % ViewNames.Length);
            if ((press & Press.SwapShip) != 0) ShowShip(!_freighter);
            if ((press & Press.NextSkin) != 0 && !_freighter && _view.Fighter.SkinCount > 0)
            {
                _skin = (_skin + 1) % _view.Fighter.SkinCount;
                ShowShip(false);
            }
            if ((press & Press.Exit) != 0 && !_leaving) Leave().Forget();

            if (_curtain.IsOpen || _leaving)   // black until the curtain opens (the freighter's hull is in)
                _fade = Mathf.MoveTowards(_fade, _leaving ? 1f : 0f, dt / (_leaving ? 0.5f : 0.8f));
            _view.Fade(_fade);
            if (!_leaving) Fly(dt);
            // The hull rolls (barrel roll), banks into turns and bobs when slow (low over the deck, less); the cameras don't.
            _sway.Step(_rotation, _flight.Velocity, _flight.Spin, H.StrafeSpeed * Speed, HoverFlight.TopSpeed(Speed), Length,
                       math.saturate((_position.y - Floor) / Length), dt, Feel);
            _ship.Pose(_position + _sway.Offset, _flight.PoseWith(_sway.Tilt));
            Aim(dt);

            if (Time.time >= _readoutAt)
            {
                _readoutAt = Time.time + math.max(0.05f, S.ReadoutSeconds);
                var name = _freighter ? "Museum freighter" : $"{_stats.Name} ({SkinName})";
                var jets = _ship.JetReadout();
                _view.Flight($"{S.Title}   {name}   {_flight.Velocity.magnitude:0} m/s   height {_position.y:0} m   {ViewNames[(int)_camera]} camera\n" +
                             (jets == "" ? "-" : jets), null);
            }
        }

        /// <summary>The planet's hover flight (HoverFlight), over the deck: it's a floor, and its edge keeps you over it.</summary>
        void Fly(float dt)
        {
            _flight.Position = _position;
            _flight.Rotation = _rotation;
            _flight.Step(_input, dt, Speed, Agility, level: _position.y < H.LevelHeight, _ship);
            if (_flight.BarrelStarted) Debug.Log($"[School] barrel roll {(_flight.BarrelDirection < 0f ? "left" : "right")}");
            _position = _flight.Position;
            _rotation = _flight.Rotation;
            var floor = Floor;
            if (_position.y < floor)
            {
                _position.y = floor;
                if (_flight.Velocity.y < 0f) _flight.Velocity.y = 0f;
            }
            var edge = S.DeckSize * 0.45f;
            _position.x = Mathf.Clamp(_position.x, -edge, edge);
            _position.z = Mathf.Clamp(_position.z, -edge, edge);
            _position.y = Mathf.Min(_position.y, S.DeckSize * 0.5f);
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
            var chase = _camera == View.Chase;
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
            var camPos = _chase.Follow(want, dt, Feel);
            if (camPos.y < 1.5f) camPos.y = 1.5f;   // the deck is opaque: stay above it
            _chase.Place(camPos);
            var top = HoverFlight.TopSpeed(Speed);
            _chase.Fov(_view.FlightCamera, chase ? _flight.Velocity.magnitude / top : 0f, 0f, dt, Feel);
            if (chase)
            {
                // Level, taking a share of the ship's roll (faded out past its side, where there's no one way to tilt);
                // led into turns, rumbling under boost and hard thrust.
                var share = math.saturate(Feel?.RollFollow ?? 0f) * math.saturate(Vector3.Dot(up, Vector3.up) * 2f);
                _chase.Aim(cam, at, _rotation, _flight.Spin, H.TurnRate * Agility, Vector3.Slerp(Vector3.up, up, share), l,
                           _leaving ? 0f : _flight.Rumble, dt, Feel);
                return;
            }
            var look = at - camPos;
            if (look.sqrMagnitude < 1e-4f) look = fwd;
            if (Mathf.Abs(Vector3.Dot(look.normalized, camUp)) > 0.98f) camUp = fwd;
            cam.SetPositionAndRotation(camPos, Quaternion.LookRotation(look, camUp));
        }

        async UniTaskVoid Leave()
        {
            _leaving = true;
            _ship.EnginesOff();
            PointerLock.Release();
            _view.Hint("", 0f);
            await UniTask.Delay(TimeSpan.FromSeconds(0.6));
            _voyage.Arrival.Value = StationArrival.FlightSchool;   // the station puts you back beside the console
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
