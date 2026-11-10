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
    /// the wreck (their ship, PlayerShip) lies nose-down nearby. Then the wake-up: lying on the ground, fade in, stand,
    /// face the wreck, hand control to the player.
    /// </summary>
    /// <summary>What the crash site was built from (for the map).</summary>
    public interface ICrashSite
    {
        bool Built { get; }
        uint Seed { get; }
        string Body { get; }
        PlanetLook Look { get; }
        /// <summary>The whole system (regenerated from the seed), for the map's star and moons.</summary>
        System.Collections.Generic.IReadOnlyList<CelestialBody> Bodies { get; }
        /// <summary>Towards the sun, in the ground's frame (x east, y up, z north).</summary>
        Vector3 SunDirection { get; }
        /// <summary>The ground streams around `target` and the sun sits beyond `eye` (null: the player, the default).</summary>
        void Follow(Transform target, Transform eye);
    }

    /// <summary>How you arrive once the crash site is built (LandingPresenter: you fly in and land).</summary>
    public interface IArrival
    {
        /// <summary>Called once the site is built (passed in, rather than injected, to avoid a dependency loop).</summary>
        UniTask ArriveAsync(ICrashSite site, CancellationToken ct);
    }

    [Presenter]
    public sealed class CrashSitePresenter : IStartable, ITickable, IDisposable, ICrashSite
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");

        readonly PlanetScene _config;
        readonly GroundView _ground;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly IArrival _arrival;
        Transform _follow, _eye;
        readonly IFirstPerson _firstPerson;
        readonly LandingSite _landing;
        readonly PlayerShip _playerShip;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly CancellationTokenSource _cts = new();
        IDisposable _subscription;
        Vector3 _sunDirection = new(0.4f, 0.6f, 0.3f);
        bool _built;

        public bool Built => _built;
        public uint Seed { get; private set; }
        public string Body { get; private set; }
        public PlanetLook Look { get; private set; }
        public System.Collections.Generic.IReadOnlyList<CelestialBody> Bodies { get; private set; }
        public Vector3 SunDirection => _sunDirection;
        public void Follow(Transform target, Transform eye) { _follow = target; _eye = eye; }
        ShipView _wreck => _view.Wreck;

        [Inject]
        public CrashSitePresenter(PlanetScene config, GroundView ground, PlanetView view, PlayerView player, IArrival arrival,
                                  IFirstPerson firstPerson, LandingSite landing, IBufferedSubscriber<UniverseSeedChanged> seeds,
                                  PlayerShip playerShip)
        {
            _playerShip = playerShip;
            _config = config;
            _ground = ground;
            _view = view;
            _player = player;
            _arrival = arrival;
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
                Build(roll.Seed == 0 ? new UniverseSeedChanged(1u, -1f, -1f, -1f, -1f) : roll);
                if (_config.Landing != null) _arrival.ArriveAsync(this, _cts.Token).Forget();   // you fly in and land
                else WakeAsync(_cts.Token).Forget();                                       // the old arrival
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
            Seed = roll.Seed;
            Body = bodyId;
            Look = planet?.Planet;
            Bodies = bodies;
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
            m.SetFloat("_HazeDistance", _config.ChunkSize * math.max(1, _config.ViewChunks) * 1.1f);   // thick before the last chunks
            _ground.Configure(_config.ChunkSize, _config.ChunkResolution, _config.ViewChunks, _config.GroundHeight,
                              _config.GroundNoiseScale, rng.NextFloat2(0f, 100f), _config.WreckDistance + _config.WreckLength * 0.6f);   // flat out past the wreck
            _ground.Stream(Vector3.zero, int.MaxValue);   // everything around the spawn, now

            // Boulders: squashed, tilted spheres half sunk into the ground; the first right beside your path.
            for (var i = 0; i < _config.Boulders; i++)
            {
                var size = rng.NextFloat(_config.BoulderSizeMin, _config.BoulderSizeMax);
                var p = i == 0 ? new float2(0.9f, 1.4f)    // a standing stone just ahead-right of where you wake
                              : rng.NextFloat2Direction() * rng.NextFloat(6f, math.max(8f, _config.BoulderSpread));
                if (i > 0 && math.distance(p, new float2(0f, _config.WreckDistance)) < _config.WreckLength * 0.6f) continue;   // keep the wreck clear
                var rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                rock.name = $"Boulder {i}";
                var scale = i == 0 ? new Vector3(1.5f, 2.6f, 1.3f)
                                   : new Vector3(size, size * rng.NextFloat(0.55f, 0.9f), size * rng.NextFloat(0.8f, 1.2f));
                rock.transform.localScale = scale;
                rock.transform.SetPositionAndRotation(new Vector3(p.x, _ground.HeightAt(new Vector3(p.x, 0f, p.y)) + scale.y * 0.32f, p.y),
                                                      Quaternion.Euler(rng.NextFloat(-15f, 15f), rng.NextFloat(0f, 360f), rng.NextFloat(-15f, 15f)));
                rock.GetComponent<MeshRenderer>().sharedMaterial = _ground.Material;   // same rock as the ground
                // Name the collider type in code: otherwise build stripping drops SphereCollider (no scene uses it)
                // and CreatePrimitive silently makes rocks you can walk and reach through.
                if (!rock.TryGetComponent<SphereCollider>(out _)) rock.AddComponent<SphereCollider>();
            }

            // Pebbles: loose stones you can pick up and throw; the first just ahead of where you wake.
            var stone = new Material(_ground.Material);   // the ground's rock, darker, so loose stones stand out
            if (look != null) stone.SetColor("_PlanetColor", look.PlanetColor.ToColor() * 0.45f);
            for (var i = 0; i < _config.Pebbles; i++)
            {
                var p = i == 0 ? new float2(0.35f, -0.6f) : rng.NextFloat2Direction() * rng.NextFloat(1.5f, 7f) + new float2(0f, -1.5f);
                var pebble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pebble.name = $"Pebble {i}";
                var size = _config.PebbleSize * rng.NextFloat(0.8f, 1.25f);
                pebble.transform.localScale = new Vector3(size, size * rng.NextFloat(0.6f, 0.85f), size * rng.NextFloat(0.85f, 1.15f));
                pebble.transform.SetPositionAndRotation(new Vector3(p.x, _ground.HeightAt(new Vector3(p.x, 0f, p.y)) + size * 0.5f, p.y),
                                                        Quaternion.Euler(0f, rng.NextFloat(0f, 360f), 0f));
                pebble.GetComponent<MeshRenderer>().sharedMaterial = stone;
                if (!pebble.TryGetComponent<SphereCollider>(out _)) pebble.AddComponent<SphereCollider>();
                var body = pebble.AddComponent<Rigidbody>();   // a rigidbody marks it as something you can carry
                body.mass = 0.4f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;   // thrown fast
            }

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
            _wreck.Show(null, _config.WreckLength);   // the museum freighter (its own look)
            var at = new Vector3(0f, 0f, _config.WreckDistance);
            at.y = _ground.HeightAt(at) + _config.WreckLength * 0.04f;            // nose dug in, hull above ground
            _wreck.Pose(at, Quaternion.Euler(14f, rng.NextFloat(-160f, -110f), rng.NextFloat(-18f, 18f)));
            _wreck.SetHeat(0f, 0.4f);                                               // hull cooled (any heat tints the dark hull orange), lights on
        }

        /// <summary>Lying on your back, eyes open (fade in), sit up, stand, turn to the wreck; then you're in control.</summary>
        async UniTaskVoid WakeAsync(CancellationToken ct)
        {
            _firstPerson.Active = false;
            var body = _player.Body.transform;
            body.position = new Vector3(0f, _ground.HeightAt(Vector3.zero) + 0.05f, -1.5f);
            var toWreck = Quaternion.LookRotation(new Vector3(0f, 0f, _config.WreckDistance) - body.position).eulerAngles.y;
            var duration = math.max(0.5f, _config.WakeSeconds);
            var talking = SayAsync(ct).Preserve();   // first words as you come to, alongside the wake-up
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
            var dbg = _config.DebugSpawn;
            if (dbg != null && (dbg.X != 0f || dbg.Y != 0f || dbg.Z != 0f))
            {
                _player.Body.enabled = false;
                body.SetPositionAndRotation(new Vector3(dbg.X, _ground.HeightAt(new Vector3(dbg.X, 0f, dbg.Z)) + 0.05f, dbg.Z),
                                            Quaternion.Euler(0f, dbg.Y, 0f));
                _player.Body.enabled = true;
                Debug.Log($"[Planet] debug spawn at ({dbg.X}, {dbg.Z}) facing {dbg.Y}");
            }
            _firstPerson.Look(body.eulerAngles.y, 8f);
            _firstPerson.Active = true;
            Debug.Log("[Planet] awake");
            await talking;   // the hint waits for the lines
            for (var t = 0f; t < 7f; t += Time.deltaTime)
            {
                _view.Hint(_config.Hint, math.smoothstep(0f, 0.5f, t) * (1f - math.smoothstep(5.5f, 7f, t)));
                await UniTask.Yield(ct);
            }
        }

        async UniTask SayAsync(CancellationToken ct)
        {
            if (_config.WakeLines.Count == 0) return;
            await UniTask.Delay(TimeSpan.FromSeconds(math.max(0f, _config.WakeLinesDelay)), cancellationToken: ct);
            foreach (var line in _config.WakeLines)
            {
                Debug.Log($"[Planet] {line.Speaker}: {line.Text}");
                await DialogueLine.Play(line, _view.Dialogue, ct);
            }
        }

        public void Tick()
        {
            if (_built && _follow != null) _ground.Stream(_follow.position, 3);   // flying: around the ship
            else if (_built)
            {
                var body = _player.Body.transform;
                _ground.Stream(body.position, 2);   // a couple of chunks per frame as you walk
                // Safety net: never end up under the world (e.g. a chunk collider not baked yet).
                var floor = _ground.HeightAt(body.position);
                if (body.position.y < floor - 2f)
                {
                    // Back onto the mesh's own surface (HeightAt can sit under it), so it doesn't fall again.
                    _player.Body.enabled = false;
                    body.position = new Vector3(body.position.x, _ground.SurfaceAt(body.position) + 0.3f, body.position.z);
                    _player.Body.enabled = true;
                    Debug.LogWarning($"[CrashSite] under the ground at {body.position}: put back on it");
                }
            }
            // Sun at "infinity": a fixed direction from wherever the player is.
            var eye = (_eye != null ? _eye : _player.Camera.transform).position;
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
