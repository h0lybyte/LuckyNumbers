using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>Graphics backend(s) compiled into a Web build.</summary>
    public enum WebGraphics
    {
        /// <summary>WebGL 2 only.</summary>
        WebGL2,
        /// <summary>WebGPU only: fails in browsers without WebGPU.</summary>
        WebGPU,
        /// <summary>WebGPU preferred, WebGL 2 fallback (project default).</summary>
        Both,
    }

    /// <summary>
    /// Web (itch.io) player settings and build, usable from the menu or headless:
    ///   Unity -batchmode -quit -buildTarget WebGL -projectPath LuckyNumbers \
    ///         -executeMethod LuckyNumbers.Editor.WebBuild.Build [-lnGfx webgl2|webgpu|both]
    /// Default (committed project setting) is WebGPU + WebGL 2 fallback: WebGPU fixed Safari, and
    /// browsers without WebGPU fall back to WebGL 2. Other variants are applied for the build only,
    /// restored afterwards, and go to their own output folder.
    /// </summary>
    public static class WebBuild
    {
        public const string DefaultOutputDir = "Builds/WebGL";
        const string GfxArg = "-lnGfx";
        const int Width = 1280, Height = 720;

        public static string OutputDirFor(WebGraphics gfx) =>
            gfx == WebGraphics.Both ? DefaultOutputDir : $"{DefaultOutputDir}-{gfx}";

        [MenuItem("LuckyNumbers/Web/Apply Settings")]
        public static void ApplySettings() => ApplySettings(WebGraphics.Both);

        public static void ApplySettings(WebGraphics gfx)
        {
            // Multithreading: Burst jobs run on worker threads. Needs COOP/COEP headers
            // (itch: "SharedArrayBuffer support" checkbox; local: tools/ln.py serve).
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

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, ApisFor(gfx));

            AssetDatabase.SaveAssets();
            Debug.Log($"[WebBuild] Settings applied: threads=on, gzip + decompression fallback, 1280x720, " +
                      $"graphics={string.Join("+", ApisFor(gfx))}");
        }

        static GraphicsDeviceType[] ApisFor(WebGraphics gfx) => gfx switch
        {
            WebGraphics.WebGPU => new[] { GraphicsDeviceType.WebGPU },
            WebGraphics.Both => new[] { GraphicsDeviceType.WebGPU, GraphicsDeviceType.OpenGLES3 },
            _ => new[] { GraphicsDeviceType.OpenGLES3 },
        };

        [MenuItem("LuckyNumbers/Web/Build (WebGPU + WebGL 2)")]
        public static void Build() => Build(GraphicsFromCommandLine());

        [MenuItem("LuckyNumbers/Web/Build (WebGL 2 only)")]
        public static void BuildWebGL2() => Build(WebGraphics.WebGL2);

        public static void Build(WebGraphics gfx)
        {
            var output = OutputDirFor(gfx);
            try
            {
                ApplySettings(gfx);

                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0) throw new BuildFailedException("No enabled scenes in Build Settings.");

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                });

                var s = report.summary;
                Debug.Log($"[WebBuild] {s.result} ({gfx}): {s.totalErrors} errors, {s.totalSize / (1024f * 1024f):F1} MB, " +
                          $"{s.totalTime.TotalSeconds:F0}s -> {Path.GetFullPath(output)}");
                if (s.result != BuildResult.Succeeded)
                    throw new BuildFailedException($"Web build {s.result}");
            }
            finally
            {
                // Keep the committed project setting stable (WebGPU + WebGL 2).
                if (gfx != WebGraphics.Both) ApplySettings(WebGraphics.Both);
            }
        }

        /// <summary>Reads "-lnGfx webgl2|webgpu|both" from the command line (default Both).</summary>
        static WebGraphics GraphicsFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, GfxArg);
            if (i < 0 || i + 1 >= args.Length) return WebGraphics.Both;
            if (Enum.TryParse(args[i + 1], ignoreCase: true, out WebGraphics gfx)) return gfx;
            throw new BuildFailedException($"Unknown {GfxArg} value '{args[i + 1]}' (use webgl2, webgpu or both)");
        }
    }
}
