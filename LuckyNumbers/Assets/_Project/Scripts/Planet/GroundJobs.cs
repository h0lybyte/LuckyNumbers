using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The ground's height function as plain data, so a Burst job and the main thread run the same code. GroundView
    /// keeps one and answers Height/NormalAt from it; chunk builds copy it into <see cref="GroundChunkJob"/>.
    /// </summary>
    public struct GroundShape
    {
        public float Height, Scale, FlatRadius, SiteRelief, SwellHeight, SwellScale;
        public float2 Offset;

        /// <summary>Big slow swells plus two octaves of hills, eased down to SiteRelief round the crash site.</summary>
        public readonly float HeightAt(float2 p)
        {
            var q = p * Scale + Offset;
            var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var swell = noise.snoise(q * SwellScale - 7.7f);
            var r = math.length(p);
            var site = math.lerp(SiteRelief, 1f, math.smoothstep(FlatRadius * 0.6f, FlatRadius * 2f, r));
            var far = math.smoothstep(FlatRadius, FlatRadius * 4f, r);   // swells only away from the site
            return hills * Height * site + swell * SwellHeight * far;
        }

        /// <summary>Normal from central differences of the height function (e = half the sample spacing).</summary>
        public readonly float3 NormalAt(float2 p, float e)
        {
            var dx = HeightAt(p + new float2(e, 0f)) - HeightAt(p - new float2(e, 0f));
            var dz = HeightAt(p + new float2(0f, e)) - HeightAt(p - new float2(0f, e));
            return math.normalize(new float3(-dx, 2f * e, -dz));
        }
    }

    /// <summary>One chunk's vertices and normals, one grid point per index (n x n, row-major in z).</summary>
    [BurstCompile]
    public struct GroundChunkJob : IJobParallelFor
    {
        public GroundShape Shape;
        public float2 Origin;
        public float Step;
        public int N;
        [WriteOnly] public NativeArray<float3> Vertices;
        [WriteOnly] public NativeArray<float3> Normals;

        public void Execute(int i)
        {
            var p = Origin + new float2(i % N, i / N) * Step;
            Vertices[i] = new float3(p.x, Shape.HeightAt(p), p.y);
            Normals[i] = Shape.NormalAt(p, Step);   // from the function, so borders match exactly
        }
    }

    /// <summary>
    /// PhysX cooking off the main thread: once a MeshCollider is given a mesh baked here with the same options, it
    /// reuses the bake instead of cooking on assignment.
    /// </summary>
    public struct GroundBakeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<EntityId> MeshIds;
        public MeshColliderCookingOptions Options;

        public void Execute(int i) => Physics.BakeMesh(MeshIds[i], false, Options);
    }
}
