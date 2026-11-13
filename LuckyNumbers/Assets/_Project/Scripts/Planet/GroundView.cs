using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Endless ground: square chunks (mesh + collider) streamed around a focus point. Heights and normals
    /// come from one pure function of world position, so chunks always meet without seams; chunks that fall
    /// behind are reused ahead. Building is spread over frames. Beyond them a far ring of big, coarse chunks (no
    /// collider) runs out to the horizon, sunk in the shader where it lies under the detailed ones. Each frame's new chunks
    /// are built together: vertices and normals in Burst jobs across the worker threads (Web threads on a Web build),
    /// then the colliders cooked in a job too.
    /// </summary>
    [View]
    public sealed class GroundView : MonoBehaviour
    {
        [SerializeField] Material material;   // LuckyNumbers/Ground (instanced once, shared by every chunk)
        [SerializeField] Material holdKit;    // LuckyNumbers/HoldKit, for the surface gate (SurfaceGatePresenter)

        static readonly int NearCentreId = Shader.PropertyToID("_NearCentre");
        static readonly int SinkId = Shader.PropertyToID("_Sink");

        readonly Layer _near = new(), _far = new();
        Material _material, _farMaterial;
        GroundShape _shape = new() { SiteRelief = 0.35f, SwellScale = 0.25f };
        readonly List<int2> _pending = new();

        sealed class Layer
        {
            public readonly Dictionary<int2, Chunk> Chunks = new();
            public readonly Stack<Chunk> Pool = new();
            public readonly List<int2> Wanted = new();
            public readonly List<int2> Drop = new();
            public readonly NearestFirst Order = new();
            /// <summary>The centre and radius Wanted was laid out for, and whether every chunk in it is built: then a
            /// frame in the same chunk has nothing to do (no re-sort, no scan).</summary>
            public int2 WantedCentre;
            public int WantedRadius = -1;
            public bool Complete;
            public float Size;
            public int Resolution, Radius;
            public bool Collide;
            public int[] Triangles;   // the same for every chunk in the layer
        }

        /// <summary>Chunk keys nearest a centre first (one instance per layer: no closure per sort).</summary>
        sealed class NearestFirst : IComparer<int2>
        {
            public int2 Centre;
            public int Compare(int2 a, int2 b) => math.lengthsq(a - Centre).CompareTo(math.lengthsq(b - Centre));
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
            _shape.Height = height;
            _shape.Scale = noiseScale;
            _shape.Offset = offset;
            _shape.FlatRadius = flatRadius;
        }

        /// <summary>The horizon ring (scale x the near chunk size; radius 0 = none) and the terrain's shape.</summary>
        public void ConfigureFar(int scale, int resolution, int viewChunks, float siteRelief, float swellHeight, float swellScale)
        {
            _far.Size = _near.Size * math.max(2, scale);
            _far.Resolution = math.clamp(resolution, 4, 64);
            _far.Radius = math.clamp(viewChunks, 0, 8);
            _far.Collide = false;
            _shape.SiteRelief = math.saturate(siteRelief);
            _shape.SwellHeight = swellHeight;
            _shape.SwellScale = swellScale > 0f ? swellScale : 0.25f;
            if (_far.Radius > 0 && _farMaterial == null)
            {
                _farMaterial = new Material(Material);
                _farMaterial.SetFloat(SinkId, 8f);   // under the detailed chunks: never pokes through them
            }
        }

        /// <summary>Terraces and ridges from the universe's profile (all zero: smooth land). Before Stream.</summary>
        public void ConfigureRelief(float terraceHeight, float terraceSharpness, float terraceBreakScale, float cliffHeight, float cliffScale)
        {
            _shape.TerraceHeight = math.max(0f, terraceHeight);
            _shape.TerraceSharpness = terraceSharpness;
            _shape.TerraceBreakScale = terraceBreakScale;
            _shape.CliffHeight = math.max(0f, cliffHeight);
            _shape.CliffScale = cliffScale > 0f ? cliffScale : 1f;
        }

        /// <summary>
        /// A second flat pad (the hold's gate): flat at the ground's own height at `centre` within `radius`, eased back
        /// over as far again. Returns that height. radius 0 removes it. Before Stream (chunks already built keep theirs).
        /// </summary>
        public float Pad(float2 centre, float radius)
        {
            _shape.PadRadius = 0f;
            var h = _shape.HeightAt(centre);
            _shape.PadCentre = centre;
            _shape.PadHeight = h;
            _shape.PadRadius = math.max(0f, radius);
            return h;
        }

        /// <summary>HoldKit (Art/Materials/HoldKit.mat): the dwarven stone the surface gate is built from.</summary>
        public Material HoldKit => holdKit;

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
            // Same chunk as last frame, everything around it built: nothing to recycle or build (most frames).
            if (layer.Complete && radius == layer.WantedRadius && centre.Equals(layer.WantedCentre)) return centre;
            if (radius != layer.WantedRadius || !centre.Equals(layer.WantedCentre))
            {
                layer.WantedCentre = centre;
                layer.WantedRadius = radius;
                layer.Wanted.Clear();
                for (var dz = -radius; dz <= radius; dz++)
                for (var dx = -radius; dx <= radius; dx++)
                    layer.Wanted.Add(centre + new int2(dx, dz));
                layer.Order.Centre = centre;
                layer.Wanted.Sort(layer.Order);   // nearest first

                // Recycle chunks that are out of range.
                var drop = layer.Drop;
                drop.Clear();
                foreach (var kv in layer.Chunks)
                    if (math.any(math.abs(kv.Key - centre) > radius)) drop.Add(kv.Key);
                foreach (var key in drop)
                {
                    var c = layer.Chunks[key];
                    c.Go.SetActive(false);
                    layer.Pool.Push(c);
                    layer.Chunks.Remove(key);
                }
            }

            _pending.Clear();
            var missing = 0;
            foreach (var key in layer.Wanted)
            {
                if (layer.Chunks.ContainsKey(key)) continue;
                missing++;
                if (budget <= 0) continue;
                _pending.Add(key);
                budget--;
            }
            layer.Complete = missing == _pending.Count;   // this frame builds the last of them
            if (_pending.Count > 0) Build(layer, _pending);
            return centre;
        }

        /// <summary>Build `keys` at once: one vertex job per chunk, all running together, then one collider bake job.</summary>
        void Build(Layer layer, List<int2> keys)
        {
            var n = layer.Resolution + 1;
            var count = n * n;
            var step = layer.Size / layer.Resolution;
            var triangles = Triangles(layer);
            var k = keys.Count;
            var vertices = new NativeArray<float3>[k];
            var normals = new NativeArray<float3>[k];
            var handles = new NativeArray<JobHandle>(k, Allocator.Temp);
            for (var i = 0; i < k; i++)
            {
                vertices[i] = new NativeArray<float3>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                normals[i] = new NativeArray<float3>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                handles[i] = new GroundChunkJob
                {
                    Shape = _shape,
                    Origin = new float2(keys[i].x - 0.5f, keys[i].y - 0.5f) * layer.Size,
                    Step = step,
                    N = n,
                    Vertices = vertices[i],
                    Normals = normals[i],
                }.Schedule(count, 64);
            }
            JobHandle.ScheduleBatchedJobs();
            JobHandle.CombineDependencies(handles).Complete();
            handles.Dispose();

            var built = new Chunk[k];
            for (var i = 0; i < k; i++)
            {
                var c = built[i] = layer.Pool.Count > 0 ? layer.Pool.Pop() : Create(layer);
                c.Mesh.Clear();
                c.Mesh.SetVertices(vertices[i]);
                c.Mesh.SetNormals(normals[i]);
                c.Mesh.SetTriangles(triangles, 0, false);
                c.Mesh.RecalculateBounds();
                vertices[i].Dispose();
                normals[i].Dispose();
                c.Go.name = $"{(layer.Collide ? "Chunk" : "Far")} {keys[i].x},{keys[i].y}";
                c.Go.SetActive(true);
                layer.Chunks[keys[i]] = c;
            }
            if (!layer.Collide) return;

            var ids = new NativeArray<EntityId>(k, Allocator.TempJob);
            for (var i = 0; i < k; i++) ids[i] = built[i].Mesh.GetEntityId();
            new GroundBakeJob { MeshIds = ids, Options = built[0].Collider.cookingOptions }.Schedule(k, 1).Complete();
            ids.Dispose();
            // Re-bake once active: baked while a recycled chunk was still inactive, it ended up with no collision
            // (you could land out there and step straight through the ground). With the bake above this reuses it.
            foreach (var c in built)
            {
                c.Collider.sharedMesh = null;
                c.Collider.sharedMesh = c.Mesh;
            }
        }

        static int[] Triangles(Layer layer)
        {
            var r = layer.Resolution;
            if (layer.Triangles != null && layer.Triangles.Length == r * r * 6) return layer.Triangles;
            var n = r + 1;
            var tris = new int[r * r * 6];
            for (int z = 0, t = 0; z < r; z++)
            for (var x = 0; x < r; x++, t += 6)
            {
                var i = z * n + x;
                tris[t] = i; tris[t + 1] = i + n; tris[t + 2] = i + 1;
                tris[t + 3] = i + 1; tris[t + 4] = i + n; tris[t + 5] = i + n + 1;
            }
            return layer.Triangles = tris;
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
        public float Height(float2 p) => _shape.HeightAt(p);

        public float HeightAt(Vector3 world) => Height(new float2(world.x, world.z));

        /// <summary>The terrain's height function, for Burst jobs (BlobShadows bends its blobs to it).</summary>
        public GroundShape Shape => _shape;

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
            // The near chunk under the point, on its own: one heightfield collider, one hit (a neighbour on a shared
            // edge gives the same height), and no scene query through every monster, ship and prop. It runs per
            // monster per frame, so this is most of what placing them costs. A miss (edge rounding) asks the scene.
            if (_near.Size > 0f)
            {
                var key = new int2((int)math.floor(world.x / _near.Size + 0.5f), (int)math.floor(world.z / _near.Size + 0.5f));
                if (_near.Chunks.TryGetValue(key, out var chunk) && chunk.Collider != null &&
                    chunk.Collider.Raycast(new Ray(from, Vector3.down), out var hit, 80f))
                    return hit.point.y;
            }
            // Every hit (unordered), as RaycastAll gave: a full buffer may have dropped some, so it grows and retries.
            int count;
            while ((count = Physics.RaycastNonAlloc(from, Vector3.down, _hits, 80f, ~0, QueryTriggerInteraction.Ignore)) == _hits.Length)
                _hits = new RaycastHit[_hits.Length * 2];
            var best = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                var c = _hits[i].collider;
                if (c.transform.parent == transform || c.GetComponentInParent<GroundView>() == this) best = Mathf.Max(best, _hits[i].point.y);
            }
            return best > float.MinValue ? best : h + missMargin;
        }

        /// <summary>SurfaceAt's ray hits (main thread only; grows if a ray fills it).</summary>
        static RaycastHit[] _hits = new RaycastHit[16];
        /// <summary>Ground normal under a world point (from the height function, like the chunk normals).</summary>
        public Vector3 NormalAt(Vector3 world) => Normal(new float2(world.x, world.z), 0.5f);

        Vector3 Normal(float2 p, float e) => _shape.NormalAt(p, e);
    }
}
