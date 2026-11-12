using System.Runtime.CompilerServices;
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
        // Terraces and ridges (GroundProfile; all 0 = the smooth land, the exact same heights as before they existed).
        public float TerraceHeight, TerraceSharpness, TerraceBreakScale, CliffHeight, CliffScale;
        // A second flat pad (the hold's surface gate): flat at PadHeight inside PadRadius, eased back over as far again.
        public float2 PadCentre;
        public float PadRadius, PadHeight;

        /// <summary>
        /// Big slow swells plus two octaves of hills, eased down to SiteRelief round the crash site; terraced and
        /// ridged if the profile says so; flattened round the pad if there is one.
        /// </summary>
        public readonly float HeightAt(float2 p)
        {
            if (TerraceHeight <= 0f && CliffHeight <= 0f && PadRadius <= 0f) return Smooth(p);   // e.g. home: as it always was
            var q = p * Scale + Offset;
            var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var swell = noise.snoise(q * SwellScale - 7.7f);
            var r = math.length(p);
            var site = math.lerp(SiteRelief, 1f, math.smoothstep(FlatRadius * 0.6f, FlatRadius * 2f, r));
            var far = math.smoothstep(FlatRadius, FlatRadius * 4f, r);
            var relief = hills * Height * site + swell * SwellHeight * far;
            var h = TerraceHeight > 0f || CliffHeight > 0f ? Shaped(q, relief, site) : relief;
            if (PadRadius > 0f) h = math.lerp(PadHeight, h, math.smoothstep(PadRadius, PadRadius * 2f, math.distance(p, PadCentre)));
            return h;
        }

        /// <summary>
        /// The smooth land, the original formula untouched (its own method, not inlined, so even the editor's JIT gives
        /// the same bits as before terraces existed; tools/ln.py deep-check).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        readonly float Smooth(float2 p)
        {
            var q = p * Scale + Offset;
            var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var swell = noise.snoise(q * SwellScale - 7.7f);
            var r = math.length(p);
            var site = math.lerp(SiteRelief, 1f, math.smoothstep(FlatRadius * 0.6f, FlatRadius * 2f, r));
            var far = math.smoothstep(FlatRadius, FlatRadius * 4f, r);   // swells only away from the site
            return hills * Height * site + swell * SwellHeight * far;
        }

        /// <summary>
        /// The whole relief (hills and swells) with ridges added, then cut into steps: flat plateaus, a riser (TerraceSharpness of each step) between.
        /// Slow noise breaks the cliffs into ramps in places; round the crash site (site near SiteRelief) the risers ease
        /// into ramps too, so you can always walk away from the wreck.
        /// </summary>
        readonly float Shaped(float2 q, float relief, float site)
        {
            if (CliffHeight > 0f)
            {
                var ridge = 1f - math.abs(noise.snoise(q * CliffScale + 31.7f));
                relief += ridge * ridge * ridge * CliffHeight * site;   // low round the crash site, like the hills
            }
            if (TerraceHeight <= 0f) return relief;
            var t = relief / TerraceHeight;
            var step = math.floor(t);
            var sharp = math.saturate(TerraceSharpness);
            if (TerraceBreakScale > 0f) sharp *= math.smoothstep(-0.45f, 0.05f, noise.snoise(q * TerraceBreakScale + 5.3f));
            sharp *= math.smoothstep(SiteRelief, math.lerp(SiteRelief, 1f, 0.6f), site);
            var riser = math.lerp(1f, 0.06f, sharp);   // share of the step that climbs
            return (step + math.smoothstep(1f - riser, 1f, t - step)) * TerraceHeight;
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
