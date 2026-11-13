using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// The hold's ambient motion (HoldConfig.ambience, content/ambience/hold.txtpb, docs/ambience.md): dust motes
    /// hanging round you in the lantern light (AmbientLayer: one static mesh, moved by its shader), and the baked
    /// lantern light flickering (global _LnLanternFlicker, read by the HoldKit shader per vertex). Nothing per frame.
    /// </summary>
    [Presenter]
    public sealed class HoldAmbiencePresenter : IStartable, IDisposable
    {
        static readonly int FlickerId = Shader.PropertyToID("_LnLanternFlicker");
        static readonly int WindId = Shader.PropertyToID("_LnWind");

        readonly HoldConfig _config;
        readonly List<AmbientLayer> _layers = new();

        [Inject]
        public HoldAmbiencePresenter(HoldConfig config) => _config = config;

        public void Start()
        {
            Shader.SetGlobalVector(WindId, Vector4.zero);   // no wind down here (no gusts in the dust)
            var ambience = _config.Ambience;
            if (ambience == null)
            {
                Shader.SetGlobalVector(FlickerId, Vector4.zero);
                return;
            }
            Shader.SetGlobalVector(FlickerId, new Vector4(Mathf.Max(0f, ambience.LanternFlicker),
                                                          Mathf.Max(0f, ambience.LanternFlickerSpeed), _config.Ambient, 0f));
            for (var i = 0; i < ambience.Motes.Count; i++)
            {
                var layer = AmbientLayer.Motes(ambience.Motes[i], $"[Ambience] hold motes {i}", 0x2545f491u + (uint)i * 7919u);
                if (layer != null) _layers.Add(layer);
            }
        }

        public void Dispose()
        {
            foreach (var l in _layers) l.Dispose();
            _layers.Clear();
            Shader.SetGlobalVector(FlickerId, Vector4.zero);   // the planet's gate (HoldKit too) stays steady
        }
    }
}
