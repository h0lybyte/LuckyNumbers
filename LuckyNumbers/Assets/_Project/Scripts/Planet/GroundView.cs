using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>The ground patch: a generated height-field mesh (rolling hills, flat where you wake) + collider.</summary>
    [View]
    public sealed class GroundView : MonoBehaviour
    {
        [SerializeField] MeshFilter meshFilter;
        [SerializeField] MeshCollider meshCollider;
        [SerializeField] MeshRenderer meshRenderer;

        public Material Material => meshRenderer.material;

        public void Build(float size, float height, float noiseScale, int resolution, float2 offset, float flatRadius)
        {
            var n = math.clamp(resolution, 8, 250);
            var verts = new Vector3[n * n];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var p = new float2((x / (n - 1f) - 0.5f) * size, (z / (n - 1f) - 0.5f) * size);
                verts[z * n + x] = new Vector3(p.x, Height(p, height, noiseScale, offset, flatRadius), p.y);
            }
            var tris = new int[(n - 1) * (n - 1) * 6];
            for (int z = 0, t = 0; z < n - 1; z++)
            for (var x = 0; x < n - 1; x++, t += 6)
            {
                var i = z * n + x;
                tris[t] = i; tris[t + 1] = i + n; tris[t + 2] = i + 1;
                tris[t + 3] = i + 1; tris[t + 4] = i + n; tris[t + 5] = i + n + 1;
            }
            var mesh = new Mesh { name = "Ground" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            meshFilter.sharedMesh = mesh;
            meshCollider.sharedMesh = mesh;
        }

        /// <summary>Two octaves of hills; flattened (smoothly) inside flatRadius so you wake on level ground.</summary>
        public static float Height(float2 p, float height, float scale, float2 offset, float flatRadius)
        {
            var q = p * scale + offset;
            var h = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var flat = math.smoothstep(flatRadius, flatRadius * 2.5f, math.length(p));
            return h * height * flat;
        }

        public float HeightAt(Vector3 world)
        {
            return Physics.Raycast(new Vector3(world.x, 500f, world.z), Vector3.down, out var hit, 1000f) ? hit.point.y : 0f;
        }
    }
}
