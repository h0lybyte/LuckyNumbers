using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Keeps a crowd of monsters from walking into one heap (CombatPresenter: a Defend job's horde all closing on one
    /// beacon). Each frame the presenter adds every body (its flat position and radius; a fixed one, like the beacon,
    /// pushes but isn't pushed), runs <see cref="SeparateJob"/> and moves each by its <see cref="Push"/>: of every
    /// pair that overlaps, each steps back half the overlap (all of it from a fixed one).
    ///
    /// Neighbours come from a grid of <see cref="Cell"/> m squares, counting-sorted into a hashed table each frame (no
    /// allocation: the arrays are persistent and only grow), so a body only looks at the 3 x 3 cells round its own:
    /// at 60 monsters a few hundred pair checks, not 3,600. One Burst job, run to completion on the spot: at this size a
    /// parallel schedule would cost more than the work.
    /// </summary>
    public sealed class MonsterSeparation : IDisposable
    {
        /// <summary>A grid cell's side (m): at least the widest pair's reach (two of the biggest bodies, side by side).</summary>
        public const float Cell = 2.5f;
        /// <summary>Hashed grid buckets (a power of two); a collision only costs a body a few extra checks.</summary>
        const int Buckets = 256;

        NativeArray<float4> _bodies;   // x, z, radius, fixed (1) or not (0)
        NativeArray<float2> _push;
        NativeArray<int> _order, _cellOf, _start;
        int _capacity;

        /// <summary>Bodies added since <see cref="Begin"/>.</summary>
        public int Count { get; private set; }

        /// <summary>A new frame: no bodies yet.</summary>
        public void Begin(int expected)
        {
            Grow(expected);
            Count = 0;
        }

        /// <summary>A body at `xz`, `radius` across half its width (0: not solid, neither pushes nor is pushed). Returns its index.</summary>
        public int Add(float2 xz, float radius, bool fixedInPlace = false)
        {
            if (Count == _capacity) Grow(Count + 1);
            _bodies[Count] = new float4(xz, radius, fixedInPlace ? 1f : 0f);
            return Count++;
        }

        /// <summary>Works out every body's push (<see cref="Push"/>).</summary>
        public void Run()
        {
            if (Count == 0) return;
            new SeparateJob
            {
                Bodies = _bodies, Count = Count, Push = _push, Order = _order, CellOf = _cellOf, Start = _start,
            }.Run();
        }

        /// <summary>How far body `i` should step (m, flat) to clear the ones it overlaps.</summary>
        public float2 Push(int i) => _push[i];

        void Grow(int n)
        {
            if (n <= _capacity) return;
            var capacity = math.max(64, math.ceilpow2(n));
            var bodies = new NativeArray<float4>(capacity, Allocator.Persistent);
            if (_bodies.IsCreated) NativeArray<float4>.Copy(_bodies, bodies, Count);
            Free();
            _bodies = bodies;
            _push = new NativeArray<float2>(capacity, Allocator.Persistent);
            _order = new NativeArray<int>(capacity, Allocator.Persistent);
            _cellOf = new NativeArray<int>(capacity, Allocator.Persistent);
            _start = new NativeArray<int>(Buckets + 1, Allocator.Persistent);
            _capacity = capacity;
        }

        void Free()
        {
            if (_bodies.IsCreated) _bodies.Dispose();
            if (_push.IsCreated) _push.Dispose();
            if (_order.IsCreated) _order.Dispose();
            if (_cellOf.IsCreated) _cellOf.Dispose();
            if (_start.IsCreated) _start.Dispose();
        }

        public void Dispose()
        {
            Free();
            _capacity = 0;
            Count = 0;
        }

        static int2 CellAt(float2 p) => (int2)math.floor(p / Cell);

        static int Bucket(int2 c) => (int)(math.hash(c) & (Buckets - 1));

        [BurstCompile]
        struct SeparateJob : IJob
        {
            [ReadOnly] public NativeArray<float4> Bodies;
            public int Count;
            public NativeArray<float2> Push;
            public NativeArray<int> Order, CellOf, Start;

            public void Execute()
            {
                // Counting sort by bucket: Start[b]..Start[b + 1] are bucket b's bodies in Order.
                for (var b = 0; b <= Buckets; b++) Start[b] = 0;
                for (var i = 0; i < Count; i++)
                {
                    var b = Bucket(CellAt(Bodies[i].xy));
                    CellOf[i] = b;
                    Start[b + 1]++;
                }
                for (var b = 0; b < Buckets; b++) Start[b + 1] += Start[b];
                Fill();
                for (var i = 0; i < Count; i++)
                {
                    var me = Bodies[i];
                    var push = float2.zero;
                    if (me.z > 0f && me.w == 0f)
                    {
                        var c = CellAt(me.xy);
                        for (var dz = -1; dz <= 1; dz++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var cell = c + new int2(dx, dz);
                            var b = Bucket(cell);
                            if (Seen(c, cell, b)) continue;   // two neighbour cells in one bucket: that bucket once
                            for (var k = Start[b]; k < Start[b + 1]; k++)
                            {
                                var j = Order[k];
                                if (j == i) continue;
                                var other = Bodies[j];
                                if (other.z <= 0f) continue;
                                var d = me.xy - other.xy;
                                var reach = me.z + other.z;
                                var d2 = math.lengthsq(d);
                                if (d2 >= reach * reach) continue;
                                var dist = math.sqrt(d2);
                                // Right on top of each other: apart along a direction from their indices (no dice).
                                var away = dist > 1e-4f ? d / dist : Spread(i, j);
                                push += away * ((reach - dist) * (other.w > 0f ? 1f : 0.5f));
                            }
                        }
                    }
                    Push[i] = push;
                }
            }

            /// <summary>Each body into its bucket's run of Order (a second pass, counting up from each bucket's start).</summary>
            void Fill()
            {
                for (var i = 0; i < Count; i++)
                {
                    var b = CellOf[i];
                    var slot = Start[b];
                    Start[b] = slot + 1;
                    Order[slot] = i;
                }
                // Start[b] now holds bucket b's end, which is bucket b + 1's start: shift back down.
                for (var b = Buckets; b > 0; b--) Start[b] = Start[b - 1];
                Start[0] = 0;
            }

            /// <summary>
            /// Whether bucket `b` (of `cell`) was already visited from an earlier cell of `centre`'s 3 x 3 (a hash
            /// collision between neighbours), so a pair isn't pushed twice.
            /// </summary>
            static bool Seen(int2 centre, int2 cell, int b)
            {
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var earlier = centre + new int2(dx, dz);
                    if (earlier.y > cell.y || (earlier.y == cell.y && earlier.x >= cell.x)) return false;
                    if (Bucket(earlier) == b) return true;
                }
                return false;
            }

            /// <summary>The pair's own direction, opposite ways for i and j.</summary>
            static float2 Spread(int i, int j)
            {
                var a = (math.min(i, j) * 0.618034f + math.max(i, j) * 0.414214f) * math.PI * 2f;
                var d = new float2(math.cos(a), math.sin(a));
                return i < j ? d : -d;
            }
        }
    }
}
