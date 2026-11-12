using System.Collections.Generic;
using System.IO;
using LuckyNumbers.Planet;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Bakes a monster model's clips into an animation texture (<see cref="MonsterVat"/>) for MonsterCrowd: run by
    /// SceneBuilder.BakeMonsters (tools/ln.py scenes) on the model template it's about to save, so the bake always
    /// matches the prefab. Each clip is posed through the rig's own PlayableGraph (MonsterRig.Pose, as the game plays
    /// it) at <see cref="Fps"/>, its start to its end inclusive, and every bone's skinning matrix (bone to model space x
    /// bind pose) is written down: the rig's root at its parent's origin, its turn and scale applied, as MonsterView
    /// places a copy. Each pose is also skinned by Unity (BakeMesh) for the clip's bounds, and to check the bake: the
    /// log's "error" is how far the texture's skinning lands from Unity's, worst vertex, after half-float rounding.
    ///
    /// Bones, not vertices: the aberration's 19 344 vertices x 384 frames would be 77 MB of positions and normals;
    /// its bones' matrices are half a megabyte, and the vertex shader skins them as Unity's GPU skinning would.
    ///
    /// Out, in Art/Vat (outside ArtImport's folders, so its Web rules don't crunch them):
    ///   &lt;name&gt;_bones.exr  RGBA half: a row per frame, 3 texels (matrix rows) per bone (uncompressed, point, no mips)
    ///   &lt;name&gt;.asset      the MonsterVat (clip table) + its mesh (the skin in bind pose; bones in uv1, weights in uv2)
    /// The material (Art/Materials/MonsterVat_&lt;name&gt;) is the skinned model's own, on LuckyNumbers/MonsterVat.
    /// </summary>
    public static class VatBaker
    {
        public const string Dir = "Assets/_Project/Art/Vat";
        /// <summary>The clips' key rate (tools/blender/export_aberration.py bakes at 30): every key is a frame.</summary>
        public const float Fps = 30f;
        /// <summary>Texture sides stay within what every WebGL 2 device takes.</summary>
        const int MaxSide = 4096;

        /// <param name="rig">The model template (at the scene root; off is fine).</param>
        /// <param name="clips">The clips to bake (short names); ones the rig lacks are skipped.</param>
        /// <param name="material">LuckyNumbers/MonsterVat material asset to fill from `skin`.</param>
        public static MonsterVat Bake(MonsterRig rig, string name, IEnumerable<string> clips, Material skin, Material material)
        {
            // A copy, posed and thrown away: the template (about to be saved as the prefab) keeps its rest pose.
            var go = Object.Instantiate(rig.gameObject);
            go.name = rig.name;
            go.transform.SetLocalPositionAndRotation(Vector3.zero, rig.transform.localRotation);
            go.transform.localScale = rig.transform.localScale;
            go.SetActive(true);
            var copy = go.GetComponent<MonsterRig>();
            var animator = go.GetComponent<Animator>();
            if (animator != null) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;   // edit mode: nothing's been seen, so it'd cull
            try
            {
                return BakeNow(copy, name, clips, skin, material);
            }
            finally
            {
                copy.Release();
                Object.DestroyImmediate(go);
            }
        }

        static MonsterVat BakeNow(MonsterRig rig, string name, IEnumerable<string> clipNames, Material skin, Material material)
        {
            var skins = rig.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skins.Length == 0) throw new System.Exception($"[VatBaker] {name}: no skinned mesh");
            var vertices = 0;
            var bones = 0;
            foreach (var s in skins) { vertices += s.sharedMesh.vertexCount; bones += s.bones.Length; }
            var width = bones * 3;
            if (width > MaxSide) throw new System.Exception($"[VatBaker] {name}: {bones} bones > {MaxSide / 3}");

            var clips = new List<MonsterVat.Clip>();
            var frames = 0;
            var seen = new HashSet<string>();
            foreach (var c in clipNames)
            {
                if (string.IsNullOrEmpty(c) || !seen.Add(c) || !rig.Has(c)) continue;
                var length = rig.Length(c);
                var n = Mathf.Max(2, Mathf.RoundToInt(length * Fps) + 1);
                clips.Add(new MonsterVat.Clip { Name = c, Start = frames, Frames = n, Length = length, Loop = rig.Find(c).isLooping, Speed = rig.Stride(c) });
                frames += n;
            }
            if (clips.Count == 0) throw new System.Exception($"[VatBaker] {name}: no clips to bake");
            if (frames > MaxSide) throw new System.Exception($"[VatBaker] {name}: {frames} frames > {MaxSide}; bake fewer clips or a lower rate");

            var data = new ushort[width * frames * 4];
            var baked = new Mesh();
            var unity = new List<Vector3>();
            var binds = new List<Vector3>[skins.Length];
            var weights = new BoneWeight[skins.Length][];
            for (var k = 0; k < skins.Length; k++)
            {
                binds[k] = new List<Vector3>();
                skins[k].sharedMesh.GetVertices(binds[k]);
                weights[k] = skins[k].sharedMesh.boneWeights;
            }
            var matrices = new Matrix4x4[bones];
            var worst = 0f;
            for (var c = 0; c < clips.Count; c++)
            {
                var clip = clips[c];
                var bounds = new Bounds();
                var any = false;
                for (var f = 0; f < clip.Frames; f++)
                {
                    rig.Pose(clip.Name, clip.Length * f / (clip.Frames - 1));
                    var row = clip.Start + f;
                    var b0 = 0;
                    for (var k = 0; k < skins.Length; k++)
                    {
                        var s = skins[k];
                        var bindposes = s.sharedMesh.bindposes;
                        for (var b = 0; b < s.bones.Length; b++)
                        {
                            var m = s.bones[b] != null ? s.bones[b].localToWorldMatrix * bindposes[b] : Matrix4x4.identity;
                            // Half floats, as the GPU will read them (so the error check below sees what it gets).
                            for (var r = 0; r < 3; r++)
                            for (var col = 0; col < 4; col++)
                            {
                                var h = Mathf.FloatToHalf(m[r, col]);
                                data[((row * width) + (b0 + b) * 3 + r) * 4 + col] = h;
                                m[r, col] = Mathf.HalfToFloat(h);
                            }
                            matrices[b0 + b] = m;
                        }
                        // Unity's own skinning of this pose: the clip's bounds, and how far ours lands from it.
                        s.BakeMesh(baked, true);
                        baked.GetVertices(unity);
                        var toWorld = Matrix4x4.TRS(s.transform.position, s.transform.rotation, Vector3.one);
                        for (var i = 0; i < unity.Count; i++)
                        {
                            var p = toWorld.MultiplyPoint3x4(unity[i]);
                            if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                            else bounds.Encapsulate(p);
                            if (i % 7 != 0) continue;   // the check: every 7th vertex
                            var w = weights[k][i];
                            var v = binds[k][i];
                            var ours = matrices[b0 + w.boneIndex0].MultiplyPoint3x4(v) * w.weight0 + matrices[b0 + w.boneIndex1].MultiplyPoint3x4(v) * w.weight1 +
                                       matrices[b0 + w.boneIndex2].MultiplyPoint3x4(v) * w.weight2 + matrices[b0 + w.boneIndex3].MultiplyPoint3x4(v) * w.weight3;
                            worst = Mathf.Max(worst, (ours - p).magnitude);
                        }
                        b0 += s.bones.Length;
                    }
                }
                clip.Bounds = bounds;
                clips[c] = clip;
            }
            Object.DestroyImmediate(baked);

            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/_Project/Art", "Vat");
            var texPath = $"{Dir}/{name}_bones.exr";
            var tex = new Texture2D(width, frames, TextureFormat.RGBAHalf, false, true);
            tex.SetPixelData(data, 0);
            tex.Apply(false);
            WriteIfChanged(texPath, tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP));
            Object.DestroyImmediate(tex);
            ImportData(texPath, TextureImporterFormat.RGBAHalf);

            // The asset and its mesh (refilled in place, so their ids and the prefab's reference stay put).
            var assetPath = $"{Dir}/{name}.asset";
            var vat = AssetDatabase.LoadAssetAtPath<MonsterVat>(assetPath);
            if (vat == null)
            {
                vat = ScriptableObject.CreateInstance<MonsterVat>();
                AssetDatabase.CreateAsset(vat, assetPath);
            }
            Mesh mesh = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath)) if (o is Mesh m) mesh = m;
            if (mesh == null)
            {
                mesh = new Mesh();
                AssetDatabase.AddObjectToAsset(mesh, vat);
            }
            FillMesh(mesh, $"{name}_vat", skins, clips);

            material.CopyMatchingPropertiesFromMaterial(skin);
            material.SetTexture("_VatBones", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);

            vat.Mesh = mesh;
            vat.Material = material;
            vat.Bones = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            vat.BoneCount = bones;
            vat.Vertices = vertices;
            vat.Fps = Fps;
            vat.Clips = clips.ToArray();
            EditorUtility.SetDirty(vat);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VatBaker] {name}: {vertices} vertices, {bones} bones, {clips.Count} clips, {frames} frames at {Fps} fps -> " +
                      $"{width}x{frames} ({width * frames * 8 / 1024} KB on the GPU, {new FileInfo(texPath).Length / 1024} KB on disk); " +
                      $"error vs Unity's skinning {worst * 1000f:0.00} mm");
            return vat;
        }

        /// <summary>One skinned vertex as the mesh holds it (MonsterVat.shader's Attributes).</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Vertex
        {
            public Vector3 Position, Normal;
            public Vector2 Uv;
            public ushort B0, B1, B2, B3;   // bone indices, half floats
            public byte W0, W1, W2, W3;     // weights, 0..255 (the shader renormalises)
        }

        /// <summary>
        /// Every skin as one mesh in bind pose (mesh space), one submesh (they share the model's one material): its
        /// normals and uvs, its 4 bones (indices offset per skin) and weights. Bounds: every clip's, in model space.
        /// </summary>
        static void FillMesh(Mesh mesh, string meshName, SkinnedMeshRenderer[] skins, List<MonsterVat.Clip> clips)
        {
            mesh.Clear();
            mesh.name = meshName;
            var verts = new List<Vertex>();
            var tris = new List<int>();
            var p = new List<Vector3>();
            var n = new List<Vector3>();
            var uv = new List<Vector2>();
            var vbase = 0;
            var b0 = 0;
            Material first = null;
            static byte W(float w) => (byte)Mathf.Clamp(Mathf.RoundToInt(w * 255f), 0, 255);
            foreach (var s in skins)
            {
                var src = s.sharedMesh;
                src.GetVertices(p);
                src.GetNormals(n);
                src.GetUVs(0, uv);
                var bw = src.boneWeights;
                for (var i = 0; i < src.vertexCount; i++)
                {
                    var w = bw[i];
                    verts.Add(new Vertex
                    {
                        Position = p[i], Normal = i < n.Count ? n[i] : Vector3.up, Uv = i < uv.Count ? uv[i] : Vector2.zero,
                        B0 = Mathf.FloatToHalf(b0 + w.boneIndex0), B1 = Mathf.FloatToHalf(b0 + w.boneIndex1),
                        B2 = Mathf.FloatToHalf(b0 + w.boneIndex2), B3 = Mathf.FloatToHalf(b0 + w.boneIndex3),
                        W0 = W(w.weight0), W1 = W(w.weight1), W2 = W(w.weight2), W3 = W(w.weight3),
                    });
                }
                for (var sub = 0; sub < src.subMeshCount; sub++)
                    foreach (var index in src.GetTriangles(sub)) tris.Add(vbase + index);
                foreach (var m in s.sharedMaterials)
                {
                    if (first == null) first = m;
                    else if (m != first) Debug.LogWarning($"[VatBaker] {meshName}: skins use more than one material; all drawn with the first");
                }
                vbase += src.vertexCount;
                b0 += s.bones.Length;
            }
            mesh.SetVertexBufferParams(verts.Count,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.UNorm8, 4));
            mesh.SetVertexBufferData(verts, 0, 0, verts.Count);
            mesh.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetIndexBufferParams(tris.Count, mesh.indexFormat);
            if (mesh.indexFormat == IndexFormat.UInt16)
            {
                var idx = new ushort[tris.Count];
                for (var i = 0; i < idx.Length; i++) idx[i] = (ushort)tris[i];
                mesh.SetIndexBufferData(idx, 0, 0, idx.Length);
            }
            else mesh.SetIndexBufferData(tris, 0, 0, tris.Count);
            var b = clips[0].Bounds;
            foreach (var c in clips) b.Encapsulate(c.Bounds);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, tris.Count), MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = b;   // model space (the draw's matrices), not the bind pose's mesh space: it only culls
            EditorUtility.SetDirty(mesh);
        }

        /// <summary>Only rewrite the file when the bake changed (no churn in git, no reimport).</summary>
        static void WriteIfChanged(string assetPath, byte[] bytes)
        {
            var full = Path.GetFullPath(assetPath);
            if (File.Exists(full) && System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes(full), bytes)) return;
            File.WriteAllBytes(full, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>Data, not a picture: linear, exact (uncompressed, point, no mips, no resize), not readable.</summary>
        static void ImportData(string path, TextureImporterFormat format)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            var changed = false;
            void Set<T>(T now, T want, System.Action<T> apply)
            {
                if (EqualityComparer<T>.Default.Equals(now, want)) return;
                apply(want);
                changed = true;
            }
            Set(ti.textureType, TextureImporterType.Default, v => ti.textureType = v);
            Set(ti.sRGBTexture, false, v => ti.sRGBTexture = v);
            Set(ti.mipmapEnabled, false, v => ti.mipmapEnabled = v);
            Set(ti.filterMode, FilterMode.Point, v => ti.filterMode = v);
            Set(ti.wrapMode, TextureWrapMode.Clamp, v => ti.wrapMode = v);
            Set(ti.npotScale, TextureImporterNPOTScale.None, v => ti.npotScale = v);
            Set(ti.isReadable, false, v => ti.isReadable = v);
            Set(ti.alphaSource, TextureImporterAlphaSource.FromInput, v => ti.alphaSource = v);
            Set(ti.textureCompression, TextureImporterCompression.Uncompressed, v => ti.textureCompression = v);
            Set(ti.maxTextureSize, MaxSide, v => ti.maxTextureSize = v);
            foreach (var platform in new[] { "WebGL", "Standalone" })
            {
                var p = ti.GetPlatformTextureSettings(platform);
                if (p.overridden && p.format == format && p.maxTextureSize == MaxSide) continue;
                p.overridden = true;
                p.format = format;
                p.maxTextureSize = MaxSide;
                p.textureCompression = TextureImporterCompression.Uncompressed;
                ti.SetPlatformTextureSettings(p);
                changed = true;
            }
            if (changed) ti.SaveAndReimport();
        }
    }
}
