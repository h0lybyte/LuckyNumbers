using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Builds UI Toolkit font assets (TextCore SDF, dynamic) from the Exo 2 TTFs and links Bold/Italic as
    /// weight variants of Regular, so USS -unity-font-style: bold/italic uses the real cuts. Idempotent.
    /// </summary>
    public static class FontSetup
    {
        const string Dir = "Assets/_Project/Art/Fonts/Exo2";

        [MenuItem("LuckyNumbers/Fonts/Rebuild Font Assets")]
        public static void Build()
        {
            var regular = Asset("Exo2-Regular");
            var bold = Asset("Exo2-Bold");
            var italic = Asset("Exo2-Italic");

            var table = regular.fontWeightTable; // index = weight / 100
            table[7].regularTypeface = bold;     // 700 = bold
            table[4].italicTypeface = italic;    // 400 italic
            EditorUtility.SetDirty(regular);
            AssetDatabase.SaveAssets();
            // USS resolves url() at import time: re-import the theme now that the font assets exist.
            foreach (var guid in AssetDatabase.FindAssets("t:StyleSheet", new[] { "Assets/_Project/UI" }))
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
            Debug.Log($"[FontSetup] {Dir}: Exo2-Regular (+Bold, Italic) font assets ready");
        }

        static FontAsset Asset(string name)
        {
            var path = $"{Dir}/{name}-SDF.asset"; // no spaces: USS url() references must resolve
            var existing = AssetDatabase.LoadAssetAtPath<FontAsset>(path);
            if (existing != null) return existing;

            var font = AssetDatabase.LoadAssetAtPath<Font>($"{Dir}/{name}.ttf")
                       ?? throw new System.Exception($"font not found: {Dir}/{name}.ttf");
            var asset = FontAsset.CreateFontAsset(font) ?? throw new System.Exception($"could not create font asset for {name}");
            asset.name = $"{name}-SDF";
            AssetDatabase.CreateAsset(asset, path);
            // Atlas textures and material live inside the font asset file.
            foreach (var tex in asset.atlasTextures)
                if (tex != null) { tex.name = $"{name} Atlas"; AssetDatabase.AddObjectToAsset(tex, asset); }
            if (asset.material != null) { asset.material.name = $"{name} Material"; AssetDatabase.AddObjectToAsset(asset.material, asset); }
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<FontAsset>(path);
        }
    }
}
