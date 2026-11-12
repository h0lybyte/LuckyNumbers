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

    /// <summary>Texture formats a Web build carries.</summary>
    public enum WebTextures
    {
        /// <summary>DXT for desktop GPUs plus an ASTC copy of the data and bundles for phones (project default).</summary>
        Both,
        /// <summary>DXT only: one pass, for quick local builds. Phones decompress every texture on the CPU.</summary>
        Dxt,
    }

    /// <summary>
    /// Web (itch.io) player settings and build, usable from the menu or headless:
    ///   Unity -batchmode -quit -buildTarget WebGL -projectPath LuckyNumbers \
    ///         -executeMethod LuckyNumbers.Editor.WebBuild.Build [-lnGfx webgl2|webgpu|both] [-lnTex both|dxt]
    /// Default (committed project setting) is WebGPU + WebGL 2 fallback: WebGPU fixed Safari, and
    /// browsers without WebGPU fall back to WebGL 2. Other variants are applied for the build only,
    /// restored afterwards, and go to their own output folder.
    /// </summary>
    public static class WebBuild
    {
        public const string DefaultOutputDir = "Builds/WebGL";
        const string GfxArg = "-lnGfx";
        const string VersionArg = "-lnVersion";
        const string TexArg = "-lnTex";
        /// <summary>The template's switch (index.html) for the ASTC copy; set true once it's in the build.</summary>
        const string AstcFlagOff = "var lnAstc = false;", AstcFlagOn = "var lnAstc = true;";
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

            // Size: strip unused managed code (Medium: VContainer [Inject] ctors are [Preserve]d), IL2CPP
            // optimised for size, wasm built for disk size with LTO.
            PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium);
            PlayerSettings.SetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.WebGL, UnityEditor.Build.Il2CppCodeGeneration.OptimizeSize);
            UnityEditor.WebGL.UserBuildSettings.codeOptimization = UnityEditor.WebGL.WasmCodeOptimization.DiskSizeLTO;

            // Assets/WebGLTemplates/LuckyNumbers: Unity's Default template + an errorHandler that keeps a
            // refused pointer lock from stopping the game.
            PlayerSettings.WebGL.template = "PROJECT:LuckyNumbers";

            // No Unity splash (allowed on every license since Unity 6): its logo texture was the build's single
            // biggest asset (2.7 MB uncompressed), and the title screen already covers the load.
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, ApisFor(gfx));

            AssetDatabase.SaveAssets();
            Debug.Log($"[WebBuild] Settings applied: threads=on, gzip + decompression fallback, 1280x720, no splash, strip=Medium, wasm=DiskSizeLTO, " +
                      $"graphics={string.Join("+", ApisFor(gfx))}");
        }

        static GraphicsDeviceType[] ApisFor(WebGraphics gfx) => gfx switch
        {
            WebGraphics.WebGPU => new[] { GraphicsDeviceType.WebGPU },
            WebGraphics.Both => new[] { GraphicsDeviceType.WebGPU, GraphicsDeviceType.OpenGLES3 },
            _ => new[] { GraphicsDeviceType.OpenGLES3 },
        };

        [MenuItem("LuckyNumbers/Web/Build (WebGPU + WebGL 2)")]
        public static void Build() => Build(GraphicsFromCommandLine(), TexturesFromCommandLine());

        [MenuItem("LuckyNumbers/Web/Build (WebGL 2 only)")]
        public static void BuildWebGL2() => Build(WebGraphics.WebGL2);

        public static void Build(WebGraphics gfx) => Build(gfx, WebTextures.Both);

        public static void Build(WebGraphics gfx, WebTextures textures)
        {
            var output = OutputDirFor(gfx);
            // Release version comes from CHANGELOG.md via "-lnVersion x.y.z" (tools/ln.py, CI).
            // Stamped into the build only, so ProjectSettings doesn't churn on every release.
            var version = ArgValue(VersionArg);
            var projectVersion = PlayerSettings.bundleVersion;
            try
            {
                ApplySettings(gfx);
                if (!string.IsNullOrEmpty(version)) PlayerSettings.bundleVersion = version;

                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0) throw new BuildFailedException("No enabled scenes in Build Settings.");

                // Addresses from AddressablesSetup.Rules (the player compiles the AssetKeys.g.cs this writes), and no
                // asset both addressable and in a scene. Addressables then builds its bundles with the player.
                if (AddressablesSetup.Apply()) Debug.Log("[WebBuild] AssetKeys.g.cs regenerated");
                AddressablesSetup.CheckDuplicates(scenes);

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                });

                var s = report.summary;
                Debug.Log($"[WebBuild] {s.result} ({gfx}, v{PlayerSettings.bundleVersion}): {s.totalErrors} errors, " +
                          $"{s.totalSize / (1024f * 1024f):F1} MB, " +
                          $"{s.totalTime.TotalSeconds:F0}s -> {Path.GetFullPath(output)}");
                if (s.result != BuildResult.Succeeded)
                    throw new BuildFailedException($"Web build {s.result}");
                if (textures == WebTextures.Both) BuildAstc(scenes, output);
            }
            finally
            {
                // Keep the committed project settings stable (WebGPU + WebGL 2, project version).
                if (gfx != WebGraphics.Both) ApplySettings(WebGraphics.Both);
                if (PlayerSettings.bundleVersion != projectVersion)
                {
                    PlayerSettings.bundleVersion = projectVersion;
                    AssetDatabase.SaveAssets();
                }
            }
        }

        /// <summary>
        /// The same build again with ASTC textures, keeping only what differs: its data file (as Build/&lt;name&gt;-astc.data*)
        /// and its bundles (StreamingAssets-astc). index.html loads those instead when the GPU has ASTC and no DXT
        /// (phones and tablets), so their textures stay compressed on the GPU instead of being unpacked on the CPU at
        /// load into 4-8x the memory. The code (wasm, framework) doesn't depend on the texture format: checked here.
        /// </summary>
        static void BuildAstc(string[] scenes, string output)
        {
            var temp = output + "-astc-tmp";
            var was = EditorUserBuildSettings.webGLBuildSubtarget;
            try
            {
                EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.ASTC;
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = temp,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new BuildFailedException($"Web build (ASTC) {report.summary.result}");

                var name = Path.GetFileName(output);
                foreach (var code in Directory.GetFiles(Path.Combine(temp, "Build")).Where(f => !Path.GetFileName(f).Contains(".data")))
                {
                    var ext = Path.GetFileName(code).Substring(Path.GetFileName(temp).Length);   // ".wasm.gz", ".framework.js.gz", ...
                    var mine = Path.Combine(output, "Build", name + ext);
                    if (File.Exists(mine) && !Unpacked(mine).AsSpan().SequenceEqual(Unpacked(code)))
                        throw new BuildFailedException($"ASTC pass: {name + ext} differs from the DXT build's, its data can't share the code");
                }
                var data = Directory.GetFiles(Path.Combine(temp, "Build"), Path.GetFileName(temp) + ".data*").Single();
                var dataExt = Path.GetFileName(data).Substring(Path.GetFileName(temp).Length);   // ".data.gz"
                File.Copy(data, Path.Combine(output, "Build", name + "-astc" + dataExt), true);

                var bundles = Path.Combine(output, "StreamingAssets-astc");
                if (Directory.Exists(bundles)) Directory.Delete(bundles, true);
                if (Directory.Exists(Path.Combine(temp, "StreamingAssets"))) Directory.Move(Path.Combine(temp, "StreamingAssets"), bundles);

                var index = Path.Combine(output, "index.html");
                var html = File.ReadAllText(index);
                if (!html.Contains(AstcFlagOff)) throw new BuildFailedException($"index.html has no '{AstcFlagOff}' (WebGLTemplates/LuckyNumbers)");
                File.WriteAllText(index, html.Replace(AstcFlagOff, AstcFlagOn));
                Debug.Log($"[WebBuild] ASTC copy: Build/{name}-astc{dataExt} ({new FileInfo(data).Length / (1024f * 1024f):F1} MB)" +
                          $" + StreamingAssets-astc, {report.summary.totalTime.TotalSeconds:F0}s");
            }
            finally
            {
                EditorUserBuildSettings.webGLBuildSubtarget = was;
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
        }

        /// <summary>A build file's bytes, un-gzipped (the gzip header carries a timestamp).</summary>
        static byte[] Unpacked(string path)
        {
            if (!path.EndsWith(".gz")) return File.ReadAllBytes(path);
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
            using var bytes = new MemoryStream();
            gz.CopyTo(bytes);
            return bytes.ToArray();
        }

        /// <summary>Reads "-lnTex both|dxt" from the command line (default Both).</summary>
        static WebTextures TexturesFromCommandLine()
        {
            var value = ArgValue(TexArg);
            if (value == null) return WebTextures.Both;
            if (Enum.TryParse(value, ignoreCase: true, out WebTextures tex)) return tex;
            throw new BuildFailedException($"Unknown {TexArg} value '{value}' (use both or dxt)");
        }

        /// <summary>Reads "-lnGfx webgl2|webgpu|both" from the command line (default Both).</summary>
        static WebGraphics GraphicsFromCommandLine()
        {
            var value = ArgValue(GfxArg);
            if (value == null) return WebGraphics.Both;
            if (Enum.TryParse(value, ignoreCase: true, out WebGraphics gfx)) return gfx;
            throw new BuildFailedException($"Unknown {GfxArg} value '{value}' (use webgl2, webgpu or both)");
        }

        /// <summary>Value following <paramref name="name"/> on the command line, or null.</summary>
        static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
