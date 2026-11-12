using System;
using System.Collections.Generic;
using System.IO;
using LuckyNumbers.Model;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Builds the holds' Texture2DArray (Art/Textures/HoldLayers.asset) from <c>GameContent.Hold.Textures</c>: layer i
    /// of <c>layers</c> is slice i. Sources are the raw tile PNGs under Art/Textures/&lt;source&gt;/ (ArtImport keeps them
    /// readable and uncompressed); any not <c>size</c> x <c>size</c> is scaled to it nearest-neighbour (crisp texels).
    /// Mips are box-filtered in linear space, then each slice is compressed to DXT1 (DXT5 if any layer has alpha): the
    /// same S3TC/BC family the Web build already ships (crunched textures decode to it), on WebGL 2 and WebGPU alike.
    /// Rebuilding keeps the asset's GUID, so HoldKit.mat stays wired. Batch: -executeMethod
    /// LuckyNumbers.Editor.HoldTextureBuilder.Build (tools/ln.py hold-textures).
    /// </summary>
    public static class HoldTextureBuilder
    {
        public const string OutPath = "Assets/_Project/Art/Textures/HoldLayers.asset";
        public const string MaterialPath = "Assets/_Project/Art/Materials/HoldKit.mat";
        const string Root = "Assets/_Project/Art/Textures/";

        [UnityEditor.MenuItem("LuckyNumbers/Holds/Build Texture Array")]
        public static void Build()
        {
            var cfg = GameContent.Hold.Textures;
            int size = cfg.Size;
            if (size < 4 || (size & (size - 1)) != 0)
                throw new InvalidOperationException($"[HoldTextures] size {size} must be a power of two >= 4");
            if (cfg.Layers.Count == 0) throw new InvalidOperationException("[HoldTextures] no layers");

            var problems = new List<string>();
            var known = new HashSet<string>(cfg.Layers);
            var seen = new HashSet<string>();
            foreach (var l in cfg.Layers)
                if (!seen.Add(l)) problems.Add($"layer listed twice: {l}");
            foreach (var p in cfg.Palettes)
                foreach (var (slot, name) in new[] { ("floor", p.Floor), ("wall", p.Wall), ("ceiling", p.Ceiling),
                                                     ("trim", p.Trim), ("pillar", p.Pillar) })
                    if (string.IsNullOrEmpty(name) || !known.Contains(name))
                        problems.Add($"palette '{p.Name}' {slot}: '{name}' is not in layers");

            var sources = new Texture2D[cfg.Layers.Count];
            for (int i = 0; i < sources.Length; i++)
            {
                var path = $"{Root}{cfg.Source}/{cfg.Layers[i]}.png";
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) problems.Add($"missing layer {i}: {path}");
                else if (!tex.isReadable) problems.Add($"not readable (check ArtImport): {path}");
                sources[i] = tex;
            }
            if (problems.Count > 0)
                throw new InvalidOperationException("[HoldTextures] can't build:\n  " + string.Join("\n  ", problems));

            int mips = 1 + (int)Math.Log(size, 2);
            var levels = new Color32[sources.Length][][];
            bool alpha = false;
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                var top = Upscale(src.GetPixels32(), src.width, src.height, size);
                foreach (var c in top) if (c.a < 255) { alpha = true; break; }
                levels[i] = MipChain(top, size, mips);
                if (src.width != size || src.height != size)
                    Debug.Log($"[HoldTextures] {cfg.Layers[i]}: {src.width}x{src.height} scaled to {size}x{size}");
            }

            var format = alpha ? TextureFormat.DXT5 : TextureFormat.DXT1;
            var array = new Texture2DArray(size, size, sources.Length, format, mips, false)
            {
                name = "HoldLayers",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 0,
            };
            for (int i = 0; i < sources.Length; i++)
            {
                var slice = new Texture2D(size, size, TextureFormat.RGBA32, mips, false);
                for (int m = 0; m < mips; m++) slice.SetPixels32(levels[i][m], m);
                slice.Apply(false);
                EditorUtility.CompressTexture(slice, format, TextureCompressionQuality.Best);
                for (int m = 0; m < mips; m++)
                    array.SetPixelData(slice.GetPixelData<byte>(m), m, i);
                UnityEngine.Object.DestroyImmediate(slice);
            }
            array.Apply(false, false);

            var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(OutPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(array, existing);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(array);
            }
            else AssetDatabase.CreateAsset(array, OutPath);
            // No CPU copy at runtime (the data stays serialized; Texture2DArray has no isReadable setter).
            var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<Texture2DArray>(OutPath));
            so.FindProperty("m_IsReadable").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            EnsureMaterial();
            VerifyShader();

            long payload = 0;
            for (int m = 0, s = size; m < mips; m++, s = Math.Max(1, s / 2))
                payload += (long)((s + 3) / 4) * ((s + 3) / 4) * (alpha ? 16 : 8);
            payload *= sources.Length;
            var file = new FileInfo(Path.Combine(Application.dataPath, "..", OutPath)).Length;
            Debug.Log($"[HoldTextures] wrote {OutPath}: {sources.Length} layers {size}x{size} {format}, {mips} mips, " +
                      $"texture data {payload} bytes ({payload / 1024f:0.0} KB), asset file {file} bytes");
        }

        /// <summary>Art/Materials/HoldKit.mat: the HoldKit shader on the built array (created once, then rewired).</summary>
        static void EnsureMaterial()
        {
            var shader = Shader.Find("LuckyNumbers/HoldKit");
            if (shader == null) throw new InvalidOperationException("[HoldTextures] shader LuckyNumbers/HoldKit not found");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "HoldKit" };
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            mat.shader = shader;
            mat.SetTexture("_Layers", AssetDatabase.LoadAssetAtPath<Texture2DArray>(OutPath));
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Compiles every HoldKit variant for the Web backends (WebGL 2 = GLES3x, WebGPU); throws on errors.</summary>
        public static void VerifyShader()
        {
            var shader = Shader.Find("LuckyNumbers/HoldKit");
            var data = ShaderUtil.GetShaderData(shader);
            var errors = new List<string>();
            int compiled = 0;
            var sets = new[] { new string[0], new[] { "_ADDITIONAL_LIGHTS" }, new[] { "_CLUSTER_LIGHT_LOOP" },
                               new[] { "_ALPHATEST_ON" }, new[] { "_ALPHATEST_ON", "_ADDITIONAL_LIGHTS" },
                               new[] { "_ALPHATEST_ON", "_CLUSTER_LIGHT_LOOP" } };
            for (int s = 0; s < data.SubshaderCount; s++)
            {
                var sub = data.GetSubshader(s);
                for (int p = 0; p < sub.PassCount; p++)
                {
                    var pass = sub.GetPass(p);
                    foreach (var platform in new[] { ShaderCompilerPlatform.GLES3x, ShaderCompilerPlatform.WebGPU })
                        foreach (var type in new[] { ShaderType.Vertex, ShaderType.Fragment })
                            foreach (var keys in sets)
                            {
                                var r = pass.CompileVariant(type, keys, platform, BuildTarget.WebGL);
                                compiled++;
                                foreach (var m in r.Messages)
                                    if (m.severity == ShaderCompilerMessageSeverity.Error)
                                        errors.Add($"{platform} {type} [{string.Join(" ", keys)}]: {m.message} (line {m.line})");
                                if (!r.Success && r.Messages.Length == 0)
                                    errors.Add($"{platform} {type} [{string.Join(" ", keys)}]: failed");
                            }
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException("[HoldTextures] HoldKit doesn't compile for Web:\n  " + string.Join("\n  ", errors));
            Debug.Log($"[HoldTextures] HoldKit: {compiled} variant stages compile for GLES3x + WebGPU");
        }

        /// <summary>Nearest-neighbour scale of a w x h image to size x size.</summary>
        static Color32[] Upscale(Color32[] px, int w, int h, int size)
        {
            if (w == size && h == size) return px;
            var o = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                int sy = y * h / size;
                for (int x = 0; x < size; x++) o[y * size + x] = px[sy * w + x * w / size];
            }
            return o;
        }

        /// <summary>Box-filtered mip chain (2x2 averages in linear space; alpha averaged as is).</summary>
        static Color32[][] MipChain(Color32[] top, int size, int mips)
        {
            var chain = new Color32[mips][];
            chain[0] = top;
            var lin = new Color[top.Length];
            for (int i = 0; i < top.Length; i++) lin[i] = ((Color)top[i]).linear;
            for (int m = 1, s = size / 2; m < mips; m++, s /= 2)
            {
                var next = new Color[s * s];
                int ps = s * 2;
                for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                        next[y * s + x] = (lin[2 * y * ps + 2 * x] + lin[2 * y * ps + 2 * x + 1] +
                                           lin[(2 * y + 1) * ps + 2 * x] + lin[(2 * y + 1) * ps + 2 * x + 1]) * 0.25f;
                chain[m] = new Color32[s * s];
                for (int i = 0; i < next.Length; i++)
                {
                    var g = next[i].gamma;
                    g.a = next[i].a;
                    chain[m][i] = g;
                }
                lin = next;
            }
            return chain;
        }
    }
}
