using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A blob shadow (<see cref="BlobShadows"/>) for whatever GameObject it's on, while it's enabled: added in code with
    /// <see cref="Attach"/>, it joins the scene's blobs on enable and leaves on disable (a hidden ship, a picked-up relic,
    /// a region's chest unloaded). Its ground is probed unless the spec says otherwise; <see cref="SetGround"/> pins it
    /// (things that never move, on a floor they know).
    /// </summary>
    public sealed class BlobCaster : MonoBehaviour
    {
        BlobSpec _spec;
        BlobShadows.Caster _caster;
        bool _ready, _hasGround, _hidden;
        float _ground;

        /// <summary>`go` casts a blob shadow with `spec` (its box, in metres at its scale now).</summary>
        public static BlobCaster Attach(GameObject go, BlobSpec spec)
        {
            if (go == null) return null;
            if (!go.TryGetComponent<BlobCaster>(out var caster)) caster = go.AddComponent<BlobCaster>();
            caster.Leave();
            caster._spec = spec;
            caster._ready = true;
            if (caster.isActiveAndEnabled) caster.Join();
            return caster;
        }

        /// <summary>Its ground is at `y` from now on (no probing).</summary>
        public void SetGround(float y)
        {
            var wasProbing = _caster != null && !_hasGround && _spec.Probe;
            _hasGround = true;
            _ground = y;
            if (wasProbing) Leave();   // rejoins told, not probed
            if (_caster == null && _ready && isActiveAndEnabled) Join();
            else _caster?.SetGround(y);
        }

        /// <summary>Hidden without leaving (e.g. you, while you're in your ship's seat).</summary>
        public void SetHidden(bool hidden)
        {
            if (hidden == _hidden) return;
            _hidden = hidden;
            _caster?.SetHidden(hidden);
        }

        void OnEnable()
        {
            if (_ready) Join();
        }

        void OnDisable() => Leave();

        void Join()
        {
            if (_caster != null || !Application.isPlaying) return;
            var spec = _spec;
            if (_hasGround) spec.Probe = false;
            _caster = BlobShadows.Current.Add(transform, spec);
            if (_caster == null) return;
            if (_hasGround) _caster.SetGround(_ground);
            if (_hidden) _caster.SetHidden(true);
        }

        void Leave()
        {
            _caster?.Remove();
            _caster = null;
        }
    }
}
