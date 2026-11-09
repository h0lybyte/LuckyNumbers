using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Endless ground: square chunks (mesh + collider) streamed around a focus point. Heights and normals
    /// come from one pure function of world position, so chunks always meet without seams; chunks that fall
    /// behind are reused ahead. Building is spread over frames.
    /// </summary>
    [View]
    public sealed class GroundView : MonoBehaviour
    {
        [SerializeField] Material material;   // LuckyNumbers/Ground (instanced once, shared by every chunk)

        readonly Dictionary<int2, Chunk> _chunks = new();
        readonly Stack<Chunk> _pool = new();
        readonly List<int2> _wanted = new();
        Material _material;
        float _size, _height, _scale, _flatRadius;
        int _resolution, _radius;
        float2 _offset;

        sealed class Chunk
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
        }

        public Material Material => _material ??= new Material(material);

        public void Configure(float chunkSize, int resolution, int viewChunks, float height, float noiseScale, float2 offset, float flatRadius)
        {
            _size = math.max(4f, chunkSize);
            _resolution = math.clamp(resolution, 4, 128);
            _radius = math.clamp(viewChunks, 1, 6);
            _height = height;
            _scale = noiseScale;
            _offset = offset;
            _flatRadius = flatRadius;
        }

        /// <summary>Keep chunks around `focus`; build at most `budget` new ones (int.MaxValue = all now).</summary>
        public void Stream(Vector3 focus, int budget)
        {
            var centre = new int2((int)math.floor(focus.x / _size + 0.5f), (int)math.floor(focus.z / _size + 0.5f));
            _wanted.Clear();
            for (var dz = -_radius; dz <= _radius; dz++)
            for (var dx = -_radius; dx <= _radius; dx++)
                _wanted.Add(centre + new int2(dx, dz));
            _wanted.Sort((a, b) => math.lengthsq(a - centre).CompareTo(math.lengthsq(b - centre)));   // nearest first

            // Recycle chunks that are out of range.
            var drop = new List<int2>();
            foreach (var kv in _chunks)
                if (math.any(math.abs(kv.Key - centre) > _radius)) drop.Add(kv.Key);
            foreach (var key in drop)
            {
                var c = _chunks[key];
                c.Go.SetActive(false);
                _pool.Push(c);
                _chunks.Remove(key);
            }

            foreach (var key in _wanted)
            {
                if (budget <= 0) break;
                if (_chunks.ContainsKey(key)) continue;
                _chunks[key] = Build(key);
                budget--;
            }
        }

        Chunk Build(int2 key)
        {
            var c = _pool.Count > 0 ? _pool.Pop() : Create();
            var n = _resolution + 1;
            var origin = new float2(key.x - 0.5f, key.y - 0.5f) * _size;
            var step = _size / _resolution;
            var verts = new Vector3[n * n];
            var normals = new Vector3[n * n];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var p = origin + new float2(x, z) * step;
                verts[z * n + x] = new Vector3(p.x, Height(p), p.y);
                normals[z * n + x] = Normal(p, step);   // from the function, so borders match exactly
            }
            var tris = new int[_resolution * _resolution * 6];
            for (int z = 0, t = 0; z < _resolution; z++)
            for (var x = 0; x < _resolution; x++, t += 6)
            {
                var i = z * n + x;
                tris[t] = i; tris[t + 1] = i + n; tris[t + 2] = i + 1;
                tris[t + 3] = i + 1; tris[t + 4] = i + n; tris[t + 5] = i + n + 1;
            }
            c.Mesh.Clear();
            c.Mesh.vertices = verts;
            c.Mesh.normals = normals;
            c.Mesh.triangles = tris;
            c.Mesh.RecalculateBounds();
            c.Collider.sharedMesh = null;      // re-bake
            c.Collider.sharedMesh = c.Mesh;
            c.Go.name = $"Chunk {key.x},{key.y}";
            c.Go.SetActive(true);
            return c;
        }

        Chunk Create()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GroundChunk" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return new Chunk { Go = go, Mesh = mesh, Collider = go.AddComponent<MeshCollider>() };
        }

        /// <summary>Ground height anywhere (no collider needed): two octaves of hills, flat near the origin.</summary>
        public float Height(float2 p)
        {
            var q = p * _scale + _offset;
            var h = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var flat = math.smoothstep(_flatRadius, _flatRadius * 2.5f, math.length(p));
            return h * _height * flat;
        }

        public float HeightAt(Vector3 world) => Height(new float2(world.x, world.z));

        Vector3 Normal(float2 p, float e)
        {
            var dx = Height(p + new float2(e, 0f)) - Height(p - new float2(e, 0f));
            var dz = Height(p + new float2(0f, e)) - Height(p - new float2(0f, e));
            return math.normalize(new float3(-dx, 2f * e, -dz));
        }
    }
}
