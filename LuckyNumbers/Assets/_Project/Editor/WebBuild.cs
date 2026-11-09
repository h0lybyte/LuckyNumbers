using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Web (itch.io) player settings and build, usable from the menu or headless:
    ///   Unity -batchmode -quit -buildTarget WebGL -projectPath LuckyNumbers \
    ///         -executeMethod LuckyNumbers.Editor.WebBuild.Build
    /// </summary>
    public static class WebBuild
    {
        public const string OutputDir = "Builds/WebGL";
        const int Width = 1280, Height = 720;

        [MenuItem("LuckyNumbers/Web/Apply Settings")]
        public static void ApplySettings()
        {
            // Multithreading: Burst jobs run on worker threads. Needs COOP/COEP headers
            // (itch: "SharedArrayBuffer support" checkbox; local: tools/serve-webgl.py).
            PlayerSettings.WebGL.threadsSupport = true;

            // itch.io serves files without a Content-Encoding header, so ship gzip
            // and let Unity's loader decompress it in JS.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;

            // 720p canvas / window to match the internal render cap.
            PlayerSettings.defaultWebScreenWidth = Width;
            PlayerSettings.defaultWebScreenHeight = Height;
            PlayerSettings.defaultScreenWidth = Width;
            PlayerSettings.defaultScreenHeight = Height;

            AssetDatabase.SaveAssets();
            Debug.Log("[WebBuild] Settings applied: threads=on, gzip + decompression fallback, 1280x720");
        }

        [MenuItem("LuckyNumbers/Web/Build")]
        public static void Build()
        {
            ApplySettings();

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new BuildFailedException("No enabled scenes in Build Settings.");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutputDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });

            var s = report.summary;
            Debug.Log($"[WebBuild] {s.result}: {s.totalErrors} errors, {s.totalSize / (1024f * 1024f):F1} MB, " +
                      $"{s.totalTime.TotalSeconds:F0}s -> {Path.GetFullPath(OutputDir)}");
            if (s.result != BuildResult.Succeeded)
                throw new BuildFailedException($"Web build {s.result}");
        }
    }
}
