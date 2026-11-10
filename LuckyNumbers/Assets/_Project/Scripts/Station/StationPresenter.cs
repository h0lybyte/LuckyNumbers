using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The player's station: builds it from content/station.txtpb, puts the player on its 'P' cell and hands them
    /// first-person control, and hangs it in their universe. Two scales, two cameras: the universe (SpacePresenter,
    /// its camera the base of the stack) is seen from a fixed point in orbit, turned with the player's head every
    /// frame; the station and the arms draw over it (overlay cameras), so its windows look out at the real system.
    /// Walking onto an 'E' cell goes on to StationScene.next. Lights follow StationLightsRequested (the page can
    /// send it).
    /// </summary>
    [Presenter]
    public sealed class StationPresenter : IStartable, IPostLateTickable, IDisposable
    {
        readonly StationScene _config;
        readonly StationView _station;
        readonly PlayerView _player;
        readonly IFirstPerson _firstPerson;
        readonly ISpaceDirector _space;
        readonly PlanetView _ui;
        readonly ISceneFlow _flow;
        readonly StationState _state;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly ISubscriber<StationLightsRequested> _lightRequests;
        readonly CancellationTokenSource _cts = new();
        IDisposable _subscription;

        bool _inSpace, _leaving;
        Vector3 _orbitPosition;
        Quaternion _stationInSpace = Quaternion.identity;
        float _fade = 1f, _hintAlpha;

        [Inject]
        public StationPresenter(StationScene config, StationView station, PlayerView player, IFirstPerson firstPerson,
                                ISpaceDirector space, PlanetView ui, ISceneFlow flow, StationState state,
                                IPublisher<SceneChangeRequested> scenes, ISubscriber<StationLightsRequested> lightRequests)
        {
            _config = config;
            _station = station;
            _player = player;
            _firstPerson = firstPerson;
            _space = space;
            _ui = ui;
            _flow = flow;
            _state = state;
            _scenes = scenes;
            _lightRequests = lightRequests;
        }

        public void Start()
        {
            _station.Build(_config);
            SetLights(StationLightMode.On);
            _subscription = _lightRequests.Subscribe(e => SetLights(e.Mode));

            var spawn = StationPlan.Centre(_station.Plan.Spawn);
            var body = _player.Body;
            body.enabled = false;   // a CharacterController ignores transform moves while enabled
            body.transform.SetPositionAndRotation(spawn, Quaternion.Euler(0f, _config.SpawnYaw, 0f));
            body.enabled = true;
            _firstPerson.Look(_config.SpawnYaw, 0f);
            _firstPerson.Active = true;

            _ui.Bind();
            _ui.Fade(1f);
            _ui.Dialogue(null, null, 0);
            _ui.MapHint(null);
            _hintAlpha = 1f;
            _ui.Hint(_config.Hint, _hintAlpha);
            _flow.Preload(_config.Next);
            PlaceInSpace(_cts.Token).Forget();
        }

        /// <summary>Wait for the universe, then park its camera in orbit around the station's body.</summary>
        async UniTaskVoid PlaceInSpace(CancellationToken ct)
        {
            while (_space.Worlds.Count == 0) await UniTask.Yield(ct);
            var target = string.IsNullOrEmpty(_config.Orbit) ? SpacePresenter.CrashSite(_space.Worlds, _space.Seed) : _config.Orbit;
            float3 at = default;
            var diameter = 1f;
            while (!_space.TryGetBody(target, out at, out diameter)) await UniTask.Yield(ct);
            _space.TakeCamera();

            // Sunward side (lit), a little above the orbit plane; the station's north faces the body.
            var toSun = _space.TryGetBody("sun", out var sun, out _) ? math.normalizesafe(sun - at, new float3(1, 0, 0)) : new float3(1, 0, 0);
            var side = math.normalizesafe(math.cross(new float3(0, 1, 0), toSun), new float3(0, 0, 1));
            var away = math.normalize(toSun * 0.6f + side * 0.75f + new float3(0, 0.2f, 0));
            _orbitPosition = (Vector3)(at + away * diameter * math.max(0.6f, _config.OrbitDiameters));
            _stationInSpace = Quaternion.LookRotation((Vector3)(at - (float3)_orbitPosition), Vector3.up);
            var cam = _space.Camera;
            cam.nearClipPlane = math.max(0.001f, diameter * 0.01f);
            _inSpace = true;
            Debug.Log($"[Station] ready: orbiting {target} (seed {_space.Seed})"); // marker for ln.py webshot
        }

        public void PostLateTick()
        {
            var dt = Time.deltaTime;
            if (_inSpace)
            {
                // The universe seen from the station's point in orbit, turned as the player's head turns.
                var eyes = _player.Camera;
                var cam = _space.Camera;
                cam.transform.SetPositionAndRotation(_orbitPosition, _stationInSpace * eyes.transform.rotation);
                cam.fieldOfView = eyes.fieldOfView;
                _fade = Mathf.MoveTowards(_fade, 0f, dt / 0.8f);
            }
            _hintAlpha = Mathf.MoveTowards(_hintAlpha, 0f, dt / 8f);
            _ui.Hint(_config.Hint, Mathf.Clamp01(_hintAlpha * 2f));
            if (_leaving) _fade = Mathf.MoveTowards(_fade, 1f, dt / 0.6f);
            _ui.Fade(_fade);

            if (!_leaving && _station.Plan.Exits.Contains(StationPlan.CellOf(_player.Body.transform.position)))
            {
                _leaving = true;
                _firstPerson.Active = false;
                Leave(_cts.Token).Forget();
            }
        }

        async UniTaskVoid Leave(CancellationToken ct)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.6), cancellationToken: ct);
            Debug.Log($"[Station] to {_config.Next}");
            _scenes.Publish(new SceneChangeRequested(_config.Next));
        }

        void SetLights(StationLightMode mode)
        {
            if (mode == StationLightMode.Unspecified) mode = StationLightMode.On;
            _station.SetLights(mode);
            _state.Lights.Value = mode;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _subscription?.Dispose();
        }
    }
}
