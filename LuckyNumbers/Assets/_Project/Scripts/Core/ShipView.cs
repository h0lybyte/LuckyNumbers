using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// A ship: a pivot (+Z forward, +Y up) around its model. With skins (the player's fighter) one shared material
    /// swaps textures; without (the museum freighter) every part keeps its own material. Heat and engine glow
    /// apply to all of them. Shared by every scene that shows a ship, so each scope registers it itself (no [View]).
    /// </summary>
    public sealed class ShipView : MonoBehaviour
    {
        static readonly int Heat = Shader.PropertyToID("_Heat");
        static readonly int Glow = Shader.PropertyToID("_Glow");

        [SerializeField] Transform pivot;
        [SerializeField] Renderer[] renderers;
        [SerializeField] Texture2D[] skins;   // Art/Models/Fighter/Skins, set by the scene builder
        [SerializeField] float modelLength = 0.49f;
        [Tooltip("Gun muzzles (the pivot's space, model units: +Z the nose): the fighter's twin barrels, left then right.")]
        [SerializeField] Vector3[] muzzles = { new(-0.077f, -0.0075f, 0.175f), new(0.077f, -0.0075f, 0.175f) };
        Material _material;                                         // skinned ships: the one shared material
        readonly System.Collections.Generic.List<(Material material, float glow)> _parts = new();

        public Transform Pivot => pivot;
        public int SkinCount => skins.Length;
        public string SkinAt(int index) => skins[index].name;
        public string SkinName => _material != null && _material.mainTexture != null ? _material.mainTexture.name : "";

        /// <summary>Show the ship at a world length; `skin` (file name) picks a skin if it has any (unknown: the first).</summary>
        public void Show(string skin, float length)
        {
            if (_parts.Count == 0)
            {
                if (skins.Length > 0)
                {
                    _material = new Material(renderers[0].sharedMaterial);
                    foreach (var r in renderers) r.sharedMaterial = _material;
                    _parts.Add((_material, _material.GetFloat(Glow)));
                }
                else
                    foreach (var r in renderers) { var m = r.material; _parts.Add((m, m.GetFloat(Glow))); }   // own copies, for heat
            }
            if (skins.Length > 0)
            {
                var tex = skins[0];
                foreach (var s in skins) if (s.name == skin) tex = s;
                if (tex.name != skin) Debug.LogWarning($"[Ship] no skin '{skin}', using {tex.name}");
                _material.mainTexture = tex;
                Debug.Log($"[Ship] {name}: skin {tex.name}");
            }
            pivot.localScale = Vector3.one * (length / Mathf.Max(modelLength, 1e-4f));
            pivot.gameObject.SetActive(true);
        }

        public int MuzzleCount => muzzles.Length;

        /// <summary>A barrel's muzzle in the world (where its gun's bolts leave).</summary>
        public Vector3 Muzzle(int index) => pivot.TransformPoint(muzzles[index]);

        public void Hide() => pivot.gameObject.SetActive(false);

        public void Pose(Vector3 position, Quaternion rotation) => pivot.SetPositionAndRotation(position, rotation);

        /// <summary>Entry heat 0..1, engine throttle (glow multiplier).</summary>
        public void SetHeat(float heat, float throttle)
        {
            foreach (var (m, glow) in _parts)
            {
                m.SetFloat(Heat, heat);
                m.SetFloat(Glow, glow * throttle);
            }
        }
    }
}
