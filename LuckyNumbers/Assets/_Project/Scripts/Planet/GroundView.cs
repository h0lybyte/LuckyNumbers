using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Endless ground: square chunks (mesh + collider) streamed around a focus point. Heights and normals
    /// come from one pure function of world position, so chunks always meet without seams; chunks that fall
    /// behind are reused ahead. Building is spread over frames. Beyond them a far ring of big, coarse chunks (no
    /// collider) runs out to the horizon, sunk in the shader where it lies under the detailed ones.
    /// </summary>
    [View]
    public sealed class GroundView : MonoBehaviour
    {
        [SerializeField] Material material;   // LuckyNumbers/Ground (instanced once, shared by every chunk)

        static readonly int NearCentreId = Shader.PropertyToID("_NearCentre");
        static readonly int SinkId = Shader.PropertyToID("_Sink");

        readonly Layer _near = new(), _far = new();
        Material _material, _farMaterial;
        float _height, _scale, _flatRadius, _siteRelief = 0.35f, _swellHeight, _swellScale = 0.25f;
        float2 _offset;

        sealed class Layer
        {
            public readonly Dictionary<int2, Chunk> Chunks = new();
            public readonly Stack<Chunk> Pool = new();
            public readonly List<int2> Wanted = new();
            public float Size;
            public int Resolution, Radius;
            public bool Collide;
        }

        sealed class Chunk
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
        }

        public Material Material => _material ??= new Material(material);

        public void Configure(float chunkSize, int resolution, int viewChunks, float height, float noiseScale, float2 offset, float flatRadius)
        {
            _near.Size = math.max(4f, chunkSize);
            _near.Resolution = math.clamp(resolution, 4, 128);
            _near.Radius = math.clamp(viewChunks, 1, 6);
            _near.Collide = true;
            _height = height;
            _scale = noiseScale;
            _offset = offset;
            _flatRadius = flatRadius;
        }

        /// <summary>The horizon ring (scale x the near chunk size; radius 0 = none) and the terrain's shape.</summary>
        public void ConfigureFar(int scale, int resolution, int viewChunks, float siteRelief, float swellHeight, float swellScale)
        {
            _far.Size = _near.Size * math.max(2, scale);
            _far.Resolution = math.clamp(resolution, 4, 64);
            _far.Radius = math.clamp(viewChunks, 0, 8);
            _far.Collide = false;
            _siteRelief = math.saturate(siteRelief);
            _swellHeight = swellHeight;
            _swellScale = swellScale > 0f ? swellScale : 0.25f;
            if (_far.Radius > 0 && _farMaterial == null)
            {
                _farMaterial = new Material(Material);
                _farMaterial.SetFloat(SinkId, 8f);   // under the detailed chunks: never pokes through them
            }
        }

        /// <summary>How far the horizon ring reaches from its centre (metres; the near ring's if there is no far ring).</summary>
        public float Reach => _far.Radius > 0 ? (_far.Radius + 0.5f) * _far.Size : (_near.Radius + 0.5f) * _near.Size;

        /// <summary>Keep chunks around `focus`; build at most `budget` new ones (int.MaxValue = all now).</summary>
        public void Stream(Vector3 focus, int budget)
        {
            var nearCentre = Stream(_near, focus, ref budget);
            if (_far.Radius <= 0) return;
            // The sunk square: where detailed chunks exist (centre and half-size, world xz).
            var half = (_near.Radius + 0.5f) * _near.Size;
            Material.SetFloat(SinkId, 0f);
            _farMaterial.SetVector(NearCentreId, new Vector4(nearCentre.x * _near.Size, nearCentre.y * _near.Size, half, _near.Size));
            var farBudget = budget == int.MaxValue ? int.MaxValue : 1;   // the horizon fills in behind the near ground
            Stream(_far, focus, ref farBudget);
        }

        int2 Stream(Layer layer, Vector3 focus, ref int budget)
        {
            var size = layer.Size;
            var centre = new int2((int)math.floor(focus.x / size + 0.5f), (int)math.floor(focus.z / size + 0.5f));
            var radius = layer.Radius;
            layer.Wanted.Clear();
            for (var dz = -radius; dz <= radius; dz++)
            for (var dx = -radius; dx <= radius; dx++)
                layer.Wanted.Add(centre + new int2(dx, dz));
            layer.Wanted.Sort((a, b) => math.lengthsq(a - centre).CompareTo(math.lengthsq(b - centre)));   // nearest first

            // Recycle chunks that are out of range.
            var drop = new List<int2>();
            foreach (var kv in layer.Chunks)
                if (math.any(math.abs(kv.Key - centre) > radius)) drop.Add(kv.Key);
            foreach (var key in drop)
            {
                var c = layer.Chunks[key];
                c.Go.SetActive(false);
                layer.Pool.Push(c);
                layer.Chunks.Remove(key);
            }

            foreach (var key in layer.Wanted)
            {
                if (budget <= 0) break;
                if (layer.Chunks.ContainsKey(key)) continue;
                layer.Chunks[key] = Build(layer, key);
                budget--;
            }
            return centre;
        }

        Chunk Build(Layer layer, int2 key)
        {
            var c = layer.Pool.Count > 0 ? layer.Pool.Pop() : Create(layer);
            var n = layer.Resolution + 1;
            var origin = new float2(key.x - 0.5f, key.y - 0.5f) * layer.Size;
            var step = layer.Size / layer.Resolution;
            var verts = new Vector3[n * n];
            var normals = new Vector3[n * n];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var p = origin + new float2(x, z) * step;
                verts[z * n + x] = new Vector3(p.x, Height(p), p.y);
                normals[z * n + x] = Normal(p, step);   // from the function, so borders match exactly
            }
            var tris = new int[layer.Resolution * layer.Resolution * 6];
            for (int z = 0, t = 0; z < layer.Resolution; z++)
            for (var x = 0; x < layer.Resolution; x++, t += 6)
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
            c.Go.name = $"{(layer.Collide ? "Chunk" : "Far")} {key.x},{key.y}";
            c.Go.SetActive(true);
            if (c.Collider == null) return c;
            // Re-bake once active: baked while a recycled chunk was still inactive, it ended up with no collision
            // (you could land out there and step straight through the ground).
            c.Collider.sharedMesh = null;
            c.Collider.sharedMesh = c.Mesh;
            return c;
        }

        Chunk Create(Layer layer)
        {
            var go = new GameObject(layer.Collide ? "Chunk" : "Far");
            go.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GroundChunk" };
            mesh.MarkDynamic();
            if (layer.Resolution + 1 > 255) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = layer.Collide ? Material : _farMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return new Chunk { Go = go, Mesh = mesh, Collider = layer.Collide ? go.AddComponent<MeshCollider>() : null };
        }

        /// <summary>
        /// Ground height anywhere (no collider needed): big slow swells plus two octaves of hills. Round the crash
        /// site the hills ease down to site_relief of their height (gentle dunes, not a dead-flat disc).
        /// </summary>
        public float Height(float2 p)
        {
            var q = p * _scale + _offset;
            var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var swell = noise.snoise(q * _swellScale - 7.7f);
            var site = math.lerp(_siteRelief, 1f, math.smoothstep(_flatRadius * 0.6f, _flatRadius * 2f, math.length(p)));
            var far = math.smoothstep(_flatRadius, _flatRadius * 4f, math.length(p));   // swells only away from the site
            return hills * _height * site + swell * _swellHeight * far;
        }

        public float HeightAt(Vector3 world) => Height(new float2(world.x, world.z));

        /// <summary>
        /// The ground you actually stand on: the chunk mesh's surface (a ray down onto its collider). Between its
        /// vertices the mesh runs a little above the smooth height function on a hill's curve, so placing a body by
        /// HeightAt alone can start it inside the mesh, where it falls through. Falls back to HeightAt.
        /// </summary>
        /// <param name="missMargin">If no chunk is there yet (still streaming in), HeightAt plus this: placing things a
        /// little above where the mesh could be beats placing them inside it.</param>
        public float SurfaceAt(Vector3 world, float missMargin = 0.6f)
        {
            var h = HeightAt(world);
            var from = new Vector3(world.x, h + 40f, world.z);
            var hits = Physics.RaycastAll(from, Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore);
            var best = float.MinValue;
            foreach (var hit in hits)
                if (hit.collider.GetComponentInParent<GroundView>() == this) best = Mathf.Max(best, hit.point.y);
            return best > float.MinValue ? best : h + missMargin;
        }
        /// <summary>Ground normal under a world point (from the height function, like the chunk normals).</summary>
        public Vector3 NormalAt(Vector3 world) => Normal(new float2(world.x, world.z), 0.5f);

        Vector3 Normal(float2 p, float e)
        {
            var dx = Height(p + new float2(e, 0f)) - Height(p - new float2(e, 0f));
            var dz = Height(p + new float2(0f, e)) - Height(p - new float2(0f, e));
            return math.normalize(new float3(-dx, 2f * e, -dz));
        }
    }
}
