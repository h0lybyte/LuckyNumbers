using System;
using LuckyNumbers.Model;
using LuckyNumbers.Rendering;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// One layer of ambient motion (docs/ambience.md): a static mesh built once, drawn by one renderer with its own copy
    /// of Resources/Ambient.mat (LuckyNumbers/Ambient). The shader moves everything from time and the camera, so after
    /// it's built a layer costs one draw and no CPU. Motes (pollen, ash, dust, embers) or a flock of fliers.
    /// The low-end tier (Rendering/LowEnd) draws Motes.low_end_share of the motes.
    /// </summary>
    public sealed class AmbientLayer : IDisposable
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BoxId = Shader.PropertyToID("_Box");
        static readonly int DriftId = Shader.PropertyToID("_Drift");
        static readonly int ShapeId = Shader.PropertyToID("_Shape");
        static readonly int ExtraId = Shader.PropertyToID("_Extra");
        static readonly int AnchorId = Shader.PropertyToID("_Anchor");
        static readonly int FlockId = Shader.PropertyToID("_Flock");
        static readonly int Flock2Id = Shader.PropertyToID("_Flock2");
        static readonly int Flock3Id = Shader.PropertyToID("_Flock3");
        static readonly int HazeId = Shader.PropertyToID("_HazeColor");

        /// <summary>A bird's two wing quads over its six vertices (tip, body, tip; trailing and leading edge each).</summary>
        static readonly int[] BirdTriangles = { 0, 1, 3, 0, 3, 2, 2, 3, 5, 2, 5, 4 };

        readonly GameObject _go;
        readonly Mesh _mesh;
        readonly Material _material;

        AmbientLayer(string name, Mesh mesh, Material material)
        {
            _mesh = mesh;
            _material = material;
            _go = new GameObject(name);
            _go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = _go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>
        /// Motes round the camera, or (anchor given) in a box standing on that point. Null if there are none to draw
        /// (count 0, the low-end share 0, or the material missing).
        /// </summary>
        public static AmbientLayer Motes(Motes m, string name, uint seed, Vector3? anchor = null)
        {
            if (m == null) return null;
            var count = m.Count;
            if (LowEnd.Active) count = (int)math.round(count * math.saturate(m.LowEndShare));
            count = math.min(count, 16000);   // 16-bit indices
            var material = NewMaterial();
            if (count <= 0 || material == null) return null;

            var rng = new Random(seed | 1u);
            var vertices = new Vector3[count * 4];
            var corners = new Vector2[count * 4];
            var seeds = new Vector4[count * 4];
            var triangles = new int[count * 6];
            for (var i = 0; i < count; i++)
            {
                var s = new Vector4(rng.NextFloat(), rng.NextFloat(), rng.NextFloat(), rng.NextFloat());
                var v = i * 4;
                corners[v] = new Vector2(-0.5f, -0.5f);
                corners[v + 1] = new Vector2(-0.5f, 0.5f);
                corners[v + 2] = new Vector2(0.5f, 0.5f);
                corners[v + 3] = new Vector2(0.5f, -0.5f);
                for (var k = 0; k < 4; k++) seeds[v + k] = s;
                var t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
            }
            var mesh = Build(name, vertices, corners, seeds, triangles);

            var box = m.Box.ToVector3();
            if (box == Vector3.zero) box = new Vector3(20f, 10f, 20f);
            material.SetColor(ColorId, m.Color.ToColor());
            material.SetVector(BoxId, box);
            material.SetVector(DriftId, m.Drift.ToVector3());
            material.SetVector(ShapeId, new Vector4(m.Size, m.Flutter, m.FlutterSpeed, m.Glow));
            material.SetVector(ExtraId, new Vector4(m.Twinkle, m.Gust, 0f, 0f));
            var a = anchor ?? Vector3.zero;
            material.SetVector(AnchorId, new Vector4(a.x, a.y, a.z, anchor.HasValue ? 1f : 0f));
            return new AmbientLayer(name, mesh, material);
        }

        /// <summary>Fliers circling far off, hazed toward `haze` by `hazeDistance` metres. Null if there are none.</summary>
        public static AmbientLayer Fliers(Flock f, string name, uint seed, Color haze, float hazeDistance)
        {
            if (f == null || f.Birds <= 0) return null;
            var material = NewMaterial();
            if (material == null) return null;
            var count = math.min(f.Birds, 2000);
            var flocks = math.max(1, f.Flocks);
            var rng = new Random(seed | 1u);
            var vertices = new Vector3[count * 6];
            var corners = new Vector2[count * 6];
            var seeds = new Vector4[count * 6];
            var triangles = new int[count * 12];
            for (var i = 0; i < count; i++)
            {
                var s = new Vector4(i % flocks, rng.NextFloat(), rng.NextFloat(), rng.NextFloat());
                var v = i * 6;
                for (var k = 0; k < 6; k++)
                {
                    corners[v + k] = new Vector2(k / 2 - 1, k % 2 == 0 ? -1f : 1f);   // wing tip, body, wing tip; trailing, leading
                    seeds[v + k] = s;
                }
                var t = i * 12;
                for (var k = 0; k < 12; k++) triangles[t + k] = v + BirdTriangles[k];
            }
            var mesh = Build(name, vertices, corners, seeds, triangles);

            material.SetColor(ColorId, f.Color.ToColor());
            material.SetVector(ExtraId, new Vector4(0f, 0f, 1f, 0f));
            material.SetVector(FlockId, new Vector4(f.DistanceMin, math.max(f.DistanceMin + 1f, f.DistanceMax), f.HeightMin, f.HeightMax));
            material.SetVector(Flock2Id, new Vector4(f.CircleRadius, f.Speed, f.Wingspan, f.FlapSpeed));
            material.SetVector(Flock3Id, new Vector4(flocks, 0f, 0f, 0f));
            material.SetVector(HazeId, new Vector4(haze.r, haze.g, haze.b, math.max(1f, hazeDistance)));
            return new AmbientLayer(name, mesh, material);
        }

        static Material NewMaterial()
        {
            var source = Resources.Load<Material>("Ambient");
            if (source != null) return new Material(source);
            Debug.LogWarning("[Ambience] Resources/Ambient.mat missing: no ambient motion");
            return null;
        }

        /// <summary>Positions are unused (the shader places everything): bounds that are never culled.</summary>
        static Mesh Build(string name, Vector3[] vertices, Vector2[] corners, Vector4[] seeds, int[] triangles)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, seeds);
            mesh.SetTriangles(triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            mesh.UploadMeshData(true);   // no CPU copy kept
            return mesh;
        }

        public void Dispose()
        {
            if (_go != null) Object.Destroy(_go);
            if (_mesh != null) Object.Destroy(_mesh);
            if (_material != null) Object.Destroy(_material);
        }
    }
}
