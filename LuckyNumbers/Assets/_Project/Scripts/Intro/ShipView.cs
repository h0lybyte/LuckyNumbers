using UnityEngine;

namespace LuckyNumbers.Intro
{
    /// <summary>The player's ship: a pivot (+Z forward, +Y up) around the fighter model, and its skins.</summary>
    [View]
    public sealed class ShipView : MonoBehaviour
    {
        static readonly int Heat = Shader.PropertyToID("_Heat");
        static readonly int Glow = Shader.PropertyToID("_Glow");

        [SerializeField] Transform pivot;
        [SerializeField] Renderer[] renderers;
        [SerializeField] Texture2D[] skins;   // Art/Models/Fighter/Skins, set by the scene builder
        [SerializeField] float modelLength = 0.49f;
        Material _material;
        float _baseGlow;

        public Transform Pivot => pivot;
        public int SkinCount => skins.Length;
        public string SkinName => _material != null && _material.mainTexture != null ? _material.mainTexture.name : "";

        /// <summary>Show the ship with a skin (file name; unknown names fall back to the first skin) at a world length.</summary>
        public void Show(string skin, float length)
        {
            _material ??= new Material(renderers[0].sharedMaterial);
            var tex = skins[0];
            foreach (var s in skins) if (s.name == skin) tex = s;
            if (tex.name != skin) Debug.LogWarning($"[Ship] no skin '{skin}', using {tex.name}");
            _material.mainTexture = tex;
            _baseGlow = _material.GetFloat(Glow);
            foreach (var r in renderers) r.sharedMaterial = _material;
            pivot.localScale = Vector3.one * (length / Mathf.Max(modelLength, 1e-4f));
            pivot.gameObject.SetActive(true);
            Debug.Log($"[Ship] skin {tex.name}");
        }

        public void Hide() => pivot.gameObject.SetActive(false);

        public void Pose(Vector3 position, Quaternion rotation) => pivot.SetPositionAndRotation(position, rotation);

        /// <summary>Entry heat 0..1, engine throttle (glow multiplier).</summary>
        public void SetHeat(float heat, float throttle)
        {
            _material.SetFloat(Heat, heat);
            _material.SetFloat(Glow, _baseGlow * throttle);
        }
    }
}
