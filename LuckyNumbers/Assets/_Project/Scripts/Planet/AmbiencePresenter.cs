using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The crash site's ambient motion, from its universe's Ambience (content/ambience/*.txtpb, docs/ambience.md):
    /// mote layers round the camera, sparks off the wreck, cloud shadows on the ground (globals _LnWind / _LnClouds for
    /// the Ground shader), fliers circling far off. Built once the site is; each layer is one static mesh the shader
    /// moves (AmbientLayer). Per frame: one height lookup under the camera, for the fliers, and one global vector.
    /// </summary>
    [Presenter]
    public sealed class AmbiencePresenter : ITickable, IDisposable
    {
        static readonly int WindId = Shader.PropertyToID("_LnWind");
        static readonly int CloudsId = Shader.PropertyToID("_LnClouds");

        readonly ICrashSite _site;
        readonly GroundView _ground;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly PlanetScene _config;
        readonly UniverseState _universe;
        readonly List<AmbientLayer> _layers = new();
        Vector4 _clouds;
        bool _built, _fliers, _settled;

        [Inject]
        public AmbiencePresenter(ICrashSite site, GroundView ground, PlanetView view, PlayerView player, PlanetScene config,
                                 UniverseState universe)
        {
            _site = site;
            _ground = ground;
            _view = view;
            _player = player;
            _config = config;
            _universe = universe;
        }

        public void Tick()
        {
            if (!_built)
            {
                if (!_site.Built) return;
                _built = true;
                Build();
            }
            if (!_fliers) return;
            // The fliers circle a height above the ground under the camera, eased so they don't hop over a terrace.
            var cam = Eye();
            if (cam == null) return;
            var y = _ground.HeightAt(cam.position);
            _clouds.z = _settled ? Mathf.Lerp(_clouds.z, y, 1f - Mathf.Exp(-Time.deltaTime * 0.4f)) : y;
            _settled = true;
            Shader.SetGlobalVector(CloudsId, _clouds);
        }

        Transform Eye()
        {
            var flight = _view.FlightCamera;
            if (flight != null && flight.isActiveAndEnabled) return flight.transform;
            return _player.Camera != null ? _player.Camera.transform : null;
        }

        void Build()
        {
            var ambience = Multiverse.Find(_universe.Id.Value)?.Ambience;
            Shader.SetGlobalVector(WindId, Vector4.zero);
            Shader.SetGlobalVector(CloudsId, Vector4.zero);
            if (ambience == null) return;
            var seed = _site.Seed;

            var clouds = ambience.Clouds;
            var wind = clouds?.Wind.ToVector2() ?? Vector2.zero;
            if (clouds != null)
            {
                Shader.SetGlobalVector(WindId, new Vector4(wind.x, wind.y, math.max(1f, clouds.Scale), math.saturate(clouds.Shadow)));
                _clouds = new Vector4(math.saturate(clouds.Cover), math.saturate(clouds.Softness), 0f, 0f);
                Shader.SetGlobalVector(CloudsId, _clouds);
            }

            for (var i = 0; i < ambience.Motes.Count; i++)
                Add(AmbientLayer.Motes(ambience.Motes[i], $"[Ambience] motes {i}", seed + (uint)i * 7919u));

            if (_site.HasWreck && ambience.Wreck != null)
            {
                var at = new Vector3(0f, 0f, _config.WreckDistance);
                at.y = _ground.HeightAt(at);
                Add(AmbientLayer.Motes(ambience.Wreck, "[Ambience] wreck sparks", seed ^ 0x5bd1e995u, at));
            }

            var haze = (_site.Look?.AtmosphereColor.ToColor() ?? Color.gray) * 0.7f;
            var flock = AmbientLayer.Fliers(ambience.Flock, "[Ambience] fliers", seed ^ 0x9e3779b9u, haze, _ground.Reach * 0.8f);
            _fliers = flock != null;
            Add(flock);
            Debug.Log($"[Ambience] {_layers.Count} layers ({_universe.Id.Value}), cloud shadow {clouds?.Shadow ?? 0f:0.##}");
        }

        void Add(AmbientLayer layer)
        {
            if (layer != null) _layers.Add(layer);
        }

        public void Dispose()
        {
            foreach (var l in _layers) l.Dispose();
            _layers.Clear();
            Shader.SetGlobalVector(WindId, Vector4.zero);   // other scenes: no clouds, no gusts
            Shader.SetGlobalVector(CloudsId, Vector4.zero);
        }
    }
}
