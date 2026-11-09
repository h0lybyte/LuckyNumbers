using UnityEditor;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Import rules for Art/Models (Web-sized): static meshes only (no rig, animation, cameras, lights,
    /// blend shapes or file materials; our shaders are assigned in code), compressed meshes, and skins
    /// capped at 512 with crunch so the Web download stays small. Applies to anything dropped in there.
    /// </summary>
    public sealed class ArtImport : AssetPostprocessor
    {
        const string Models = "Assets/_Project/Art/Models/";

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Models)) return;
            var m = (ModelImporter)assetImporter;
            m.animationType = ModelImporterAnimationType.None;
            m.importAnimation = false;
            m.importCameras = false;
            m.importLights = false;
            m.importBlendShapes = false;
            m.importVisibility = false;
            m.materialImportMode = ModelImporterMaterialImportMode.None;
            m.meshCompression = ModelImporterMeshCompression.Medium;
            m.meshOptimizationFlags = MeshOptimizationFlags.Everything;
            m.isReadable = false;
            m.addCollider = false;
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Models)) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.maxTextureSize = 512;
            t.mipmapEnabled = true;
            t.isReadable = false;
            t.anisoLevel = 2;
            t.textureCompression = TextureImporterCompression.Compressed;
            t.crunchedCompression = true;
            t.compressionQuality = 50;
        }
    }
}
