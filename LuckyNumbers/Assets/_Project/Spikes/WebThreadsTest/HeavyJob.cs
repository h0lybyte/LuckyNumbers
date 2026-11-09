using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace LuckyNumbers.Spikes.WebThreadsTest
{
    /// <summary>
    /// Deliberately heavy per-item math so the work is measurable, plus a per-thread
    /// hit counter so we can see how many threads actually executed it.
    /// </summary>
    [BurstCompile]
    public struct HeavyJob : IJobParallelFor
    {
        public float dt;
        public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;

        // Each thread only writes its own slot, so this is race-free.
        [NativeDisableParallelForRestriction] public NativeArray<int> threadHits;
        [NativeSetThreadIndex] int _threadIndex;

        public void Execute(int i)
        {
            var p = positions[i];
            var v = velocities[i];
            for (int k = 0; k < 32; k++)
            {
                p += math.sin(p + v) * dt;
                p = math.clamp(p, -100f, 100f);
            }
            positions[i] = p;
            threadHits[_threadIndex] += 1;
        }
    }
}
