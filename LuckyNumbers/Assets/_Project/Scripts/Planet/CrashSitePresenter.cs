using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Builds the crash site from the player's universe: the planet they hit (LandingSite.body, regenerated
    /// from the lucky number) colours the ground and haze, its star is the sun, the roll's sky is overhead,
    /// the wreck (their skin) lies nose-down nearby. Then the wake-up: lying on the ground, fade in, stand,
    /// face the wreck, hand control to the player.
    /// </summary>
    [Presenter]
    public sealed class CrashSitePresenter : IStartable, ITickable, IDisposable
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");

        readonly PlanetScene _config;
        readonly GroundView _ground;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly ShipView _wreck;
        readonly IFirstPerson _firstPerson;
        readonly LandingSite _landing;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly CancellationTokenSource _cts = new();
        IDisposable _subscription;
        Vector3 _sunDirection = new(0.4f, 0.6f, 0.3f);
        bool _built;

        [Inject]
        public CrashSitePresenter(PlanetScene config, GroundView ground, PlanetView view, PlayerView player, ShipView wreck,
                                  IFirstPerson firstPerson, LandingSite landing, IBufferedSubscriber<UniverseSeedChanged> seeds)
        {
            _config = config;
            _ground = ground;
            _view = view;
            _player = player;
            _wreck = wreck;
            _firstPerson = firstPerson;
            _landing = landing;
            _seeds = seeds;
        }

        public void Start()
        {
            _view.Bind();
            _view.Fade(1f);
            _view.Hint(_config.Hint, 0f);
            _subscription = _seeds.Subscribe(roll =>
            {
                if (_built) return;
                _built = true;
                Build(roll.Seed == 0 ? new UniverseSeedChanged(1u, -1f, -1f, -1f) : roll);
                WakeAsync(_cts.Token).Forget();
            });
        }

        void Build(UniverseSeedChanged roll)
        {
            var rules = _config.Backdrop.Generator;
            var bodyId = _landing.Valid.Value && !string.IsNullOrEmpty(_landing.Body.Value) ? _landing.Body.Value : "home";
            var bodies = UniverseGenerator.Generate(rules, roll);
            CelestialBody planet = null, star = null;
            foreach (var b in bodies)
            {
                if (b.Id == bodyId) planet = b;
                if (b.Star != null && star == null) star = b;
            }
            Debug.Log($"[Planet] crash site on {bodyId} (lucky number {roll.Seed})");
            var rng = new Random(roll.Seed | 1u);

            // Ground: the planet's colours and noise, at walking scale.
            var look = planet?.Planet;
            var m = _ground.Material;
            if (look != null)
            {
                m.SetColor("_PlanetColor", look.PlanetColor.ToColor());
                m.SetColor("_AtmosphereColor", look.AtmosphereColor.ToColor());
                var o = look.NoiseOffset;
                m.SetVector("_NoiseOffset", new Vector4(o.X, o.Y, o.Z, 0f));
                m.SetFloat("_NoiseRemap", look.NoiseRemap);
            }
            m.SetFloat("_SurfaceScale", _config.SurfaceNoiseScale);
            m.SetFloat("_Haze", _config.Haze);
            _ground.Build(_config.GroundSize, _config.GroundHeight, _config.GroundNoiseScale, _config.GroundResolution,
                          rng.NextFloat2(0f, 100f), _config.WreckDistance + 6f);

            // Sky: the roll's nebula sky, hazed by this planet's atmosphere near the horizon.
            var sky = UniverseGenerator.Sky(rules, roll);
            if (RenderSettings.skybox != null)
            {
                RenderSettings.skybox = new Material(RenderSettings.skybox);
                sky?.ApplyTo(RenderSettings.skybox);
                RenderSettings.skybox.SetColor("_HazeColor", look?.AtmosphereColor.ToColor() ?? Color.gray);
                RenderSettings.skybox.SetFloat("_Haze", _config.Haze);
            }

            // Sun: the system's star, high in the sky.
            var azimuth = rng.NextFloat(0f, 360f);
            _sunDirection = Quaternion.Euler(-_config.SunElevationDegrees, azimuth, 0f) * Vector3.forward * -1f;
            _sunDirection.y = math.abs(_sunDirection.y);
            _sunDirection.Normalize();
            if (star != null)
            {
                star.Star.ApplyTo(_view.Sun.material);
                Shader.SetGlobalVector(SunColor, star.Star.LightColor.ToColor());
            }

            // Wreck: nose-down in front of where you wake, half dug in, still smouldering.
            var skin = _landing.Valid.Value ? _landing.ShipSkin.Value : "";
            _wreck.Show(skin, (int)(roll.Seed % (uint)math.max(1, _wreck.SkinCount)), _config.WreckLength);
            var at = new Vector3(0f, 0f, _config.WreckDistance);
            at.y = _ground.HeightAt(at) + _config.WreckLength * 0.04f;            // nose dug in, hull above ground
            _wreck.Pose(at, Quaternion.Euler(14f, rng.NextFloat(-160f, -110f), rng.NextFloat(-18f, 18f)));
            _wreck.SetHeat(0.04f, 0.4f);                                            // hull cooled, lights flickering on
        }

        /// <summary>Lying on your back, eyes open (fade in), sit up, stand, turn to the wreck; then you're in control.</summary>
        async UniTaskVoid WakeAsync(CancellationToken ct)
        {
            _firstPerson.Active = false;
            var body = _player.Body.transform;
            body.position = new Vector3(0f, _ground.HeightAt(Vector3.zero) + 0.05f, -1.5f);
            var toWreck = Quaternion.LookRotation(new Vector3(0f, 0f, _config.WreckDistance) - body.position).eulerAngles.y;
            var duration = math.max(0.5f, _config.WakeSeconds);
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                var x = t / duration;
                _view.Fade(1f - math.smoothstep(0f, 0.3f, x) + 0.35f * math.max(0f, math.sin(x * 9f)) * (1f - x)); // blinking awake
                var rise = math.smoothstep(0.35f, 1f, x);
                var head = _player.Head;
                head.localPosition = new Vector3(0f, math.lerp(0.25f, _config.EyeHeight, rise), 0f);
                var pitch = math.lerp(-75f, 8f, math.smoothstep(0.25f, 0.95f, x));       // from the sky down to the wreck
                var roll = math.lerp(25f, 0f, math.smoothstep(0.3f, 0.9f, x));
                body.rotation = Quaternion.Euler(0f, math.lerp(toWreck + 40f, toWreck, rise), 0f);
                head.localRotation = Quaternion.Euler(pitch, 0f, roll);
                await UniTask.Yield(ct);
            }
            _view.Fade(0f);
            _firstPerson.Look(body.eulerAngles.y, 8f);
            _firstPerson.Active = true;
            Debug.Log("[Planet] awake");
            for (var t = 0f; t < 7f; t += Time.deltaTime)
            {
                _view.Hint(_config.Hint, math.smoothstep(0f, 0.5f, t) * (1f - math.smoothstep(5.5f, 7f, t)));
                await UniTask.Yield(ct);
            }
        }

        public void Tick()
        {
            // Sun at "infinity": a fixed direction from wherever the player is.
            var eye = _player.Camera.transform.position;
            var sun = eye + _sunDirection * 900f;
            _view.Sun.transform.position = sun;
            Shader.SetGlobalVector(SunPosition, new Vector4(sun.x, sun.y, sun.z, 1f));
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
