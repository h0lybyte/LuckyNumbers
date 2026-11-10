using UnityEditor;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Import rules (Web-sized), applied to anything dropped in:
    /// - Art/Models: static meshes (no rig, animation, cameras, lights, blend shapes or file materials;
    ///   our shaders are assigned in code), compressed.
    /// - Art/Characters: same, but keeps the Generic rig and every animation take as a clip.
    /// - Textures in either: capped at 512 with crunch so the download stays small, except the ships seen up close
    ///   (CargoShip, Fighter): 1024, full-quality crunch, anisotropic.
    /// - Modular kits (Art/Models/*Kit/, the PSX station and starship kits): meshes stay readable (StationView
    ///   merges them into chunks at runtime) and uncompressed (pieces must meet exactly on the 2 m grid); the
    ///   atlas keeps its full size, bilinear with mipmaps: the StationKit shader samples it "sharp bilinear"
    ///   (crisp PS1 texels up close, one anti-aliased pixel at texel edges, mips far off: no shimmer).
    /// </summary>
    public sealed class ArtImport : AssetPostprocessor
    {
        const string Models = "Assets/_Project/Art/Models/";
        const string Characters = "Assets/_Project/Art/Characters/";

        /// <summary>Bump when these rules change: Unity re-imports what they cover (otherwise old settings stick).</summary>
        public override uint GetVersion() => 5;   // 5: hero ship meshes uncompressed (3: ship textures kept sharp)

        bool Ours => assetPath.StartsWith(Models) || assetPath.StartsWith(Characters);
        bool Kit => assetPath.StartsWith(Models) && assetPath.Substring(Models.Length).Split('/')[0].EndsWith("Kit");
        /// <summary>Ships you see up close, big on screen (your fighter, the freighter wreck): textures kept sharp.</summary>
        bool Hero => assetPath.StartsWith(Models + "CargoShip/") || assetPath.StartsWith(Models + "Fighter/");

        void OnPreprocessModel()
        {
            if (!Ours) return;
            var m = (ModelImporter)assetImporter;
            var character = assetPath.StartsWith(Characters);
            m.animationType = character ? ModelImporterAnimationType.Generic : ModelImporterAnimationType.None;
            m.importAnimation = character;
            if (character)
            {
                m.animationCompression = ModelImporterAnimationCompression.Optimal;
                m.resampleCurves = true;
                m.optimizeGameObjects = false;   // code finds bones (e.g. hands) by name
            }
            m.importCameras = character;   // first-person rigs ship their viewmodel camera
            m.importLights = false;
            m.importBlendShapes = false;
            m.importVisibility = false;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.meshCompression = ModelImporterMeshCompression.Medium;
            m.meshOptimizationFlags = MeshOptimizationFlags.Everything;
            m.isReadable = false;
            m.addCollider = false;
            if (Hero) m.meshCompression = ModelImporterMeshCompression.Off;   // seen up close: no vertex quantising
            if (Kit)
            {
                m.isReadable = true;
                m.meshCompression = ModelImporterMeshCompression.Off;
                m.bakeAxisConversion = true;   // Blender Z-up baked into the vertices: pieces' roots are identity
            }
        }

        void OnPreprocessTexture()
        {
            if (!Ours) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.maxTextureSize = 512;
            t.mipmapEnabled = true;
            t.isReadable = false;
            t.anisoLevel = 2;
            t.textureCompression = TextureImporterCompression.Compressed;
            t.crunchedCompression = true;
            t.compressionQuality = 50;
            if (Hero)
            {
                // Up to 1024, crunch at full quality (quality 50 smeared the panel detail; plain DXT/ETC adds ~5 MB to
                // the download), trilinear, and anisotropic so hull plates stay sharp at the grazing angles you see them from.
                t.maxTextureSize = 1024;
                t.compressionQuality = 100;
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 8;
            }
            if (Kit)
            {
                t.maxTextureSize = 2048;
                t.mipmapEnabled = true;
                t.mipmapFilter = TextureImporterMipFilter.BoxFilter;
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 0;
                t.wrapMode = TextureWrapMode.Clamp;
            }
        }
    }
}
