using UnityEditor;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Import rules (Web-sized), applied to anything dropped in:
    /// - Art/Models: static meshes (no rig, animation, cameras, lights, blend shapes or file materials;
    ///   our shaders are assigned in code), compressed.
    /// - Art/Characters: same, but keeps the Generic rig and every animation take as a clip.
    /// - Textures in either: capped at 512 with crunch so the download stays small.
    /// - Modular kits (Art/Models/*Kit/, the PSX station and starship kits): meshes stay readable (StationView
    ///   merges them into chunks at runtime) and uncompressed (pieces must meet exactly on the 2 m grid); the
    ///   atlas keeps its full size, point-filtered without mipmaps (crisp PS1 texels; mostly empty, crunches small).
    /// </summary>
    public sealed class ArtImport : AssetPostprocessor
    {
        const string Models = "Assets/_Project/Art/Models/";
        const string Characters = "Assets/_Project/Art/Characters/";

        bool Ours => assetPath.StartsWith(Models) || assetPath.StartsWith(Characters);
        bool Kit => assetPath.StartsWith(Models) && assetPath.Substring(Models.Length).Split('/')[0].EndsWith("Kit");

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
            if (Kit)
            {
                t.maxTextureSize = 2048;
                t.mipmapEnabled = false;
                t.filterMode = FilterMode.Point;
                t.anisoLevel = 0;
                t.wrapMode = TextureWrapMode.Clamp;
            }
        }
    }
}
