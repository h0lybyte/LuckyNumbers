using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// One level of a grid's way to a goal cell: a breadth-first cost from the goal out over roamable cells
    /// (HoldNav.Roamable, 4-connected), so every creature on that level reads its next step from its own cell instead of
    /// searching its own path. One search per goal for all of them (ported from bevy_pathfinder's FlowField, the one
    /// the uniti crate wraps). The search is a Burst job; it stops after `maxVisit` cells, and a cell it never reached
    /// has no step (the caller walks straight, as when a path search came up empty).
    /// </summary>
    public sealed class FlowField : IDisposable
    {
        public const int Unreached = int.MaxValue;

        NativeArray<int> _cost;
        NativeArray<int> _queue;
        int _w, _d;

        /// <summary>The level (local y) and goal cell (local xz) of the last Build; Goal is (-1, -1) before one.</summary>
        public int Level { get; private set; } = -1;
        public int2 Goal { get; private set; } = new(-1, -1);
        /// <summary>Bumped by every Build: a creature holding steps from an older version takes fresh ones.</summary>
        public int Version { get; private set; }

        public bool IsCreated => _cost.IsCreated;

        /// <summary>Search outward from `goal` on `level` of `g` (local cells). Runs to completion (Burst) before it returns.</summary>
        public void Build(in HoldGrid g, int level, int2 goal, int maxVisit)
        {
            var n = g.Size.x * g.Size.z;
            if (!_cost.IsCreated || _cost.Length != n)
            {
                Dispose();
                _cost = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _queue = new NativeArray<int>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            _w = g.Size.x;
            _d = g.Size.z;
            Level = level;
            Goal = goal;
            Version++;
            new SearchJob
            {
                Cells = g.Cells, Size = g.Size, Level = level, Goal = goal, MaxVisit = math.max(1, maxVisit), Cost = _cost, Queue = _queue,
            }.Run();
        }

        /// <summary>Steps from the goal to `c` (local xz), or Unreached.</summary>
        public int Cost(int2 c) => _cost.IsCreated && In(c) ? _cost[c.x + _w * c.y] : Unreached;

        /// <summary>The neighbour of `from` one step nearer the goal; false at the goal or where the search didn't reach.</summary>
        public bool Step(int2 from, out int2 next)
        {
            next = from;
            var here = Cost(from);
            if (here == Unreached || here == 0) return false;
            var best = here;
            for (var k = 0; k < 4; k++)
            {
                var n = from + Four(k);
                var c = Cost(n);
                if (c < best) { best = c; next = n; }
            }
            return best < here;
        }

        bool In(int2 c) => c.x >= 0 && c.y >= 0 && c.x < _w && c.y < _d;

        static int2 Four(int k) => k switch { 0 => new int2(1, 0), 1 => new int2(-1, 0), 2 => new int2(0, 1), _ => new int2(0, -1) };

        public void Dispose()
        {
            if (_cost.IsCreated) _cost.Dispose();
            if (_queue.IsCreated) _queue.Dispose();
        }

        [BurstCompile]
        struct SearchJob : IJob
        {
            [ReadOnly] public NativeArray<HoldCell> Cells;
            public int3 Size;
            public int Level;
            public int2 Goal;
            public int MaxVisit;
            public NativeArray<int> Cost;
            public NativeArray<int> Queue;   // cell indexes (x + w * z), head..tail

            public void Execute()
            {
                int w = Size.x, d = Size.z;
                for (var i = 0; i < Cost.Length; i++) Cost[i] = Unreached;
                if (Level < 0 || Level >= Size.y || Goal.x < 0 || Goal.y < 0 || Goal.x >= w || Goal.y >= d) return;
                if (!Roam(Goal.x, Goal.y)) return;
                var start = Goal.x + w * Goal.y;
                Cost[start] = 0;
                Queue[0] = start;
                int head = 0, tail = 1;
                while (head < tail && head < MaxVisit)
                {
                    var c = Queue[head++];
                    int cx = c % w, cz = c / w;
                    var next = Cost[c] + 1;
                    Visit(cx + 1, cz, next, w, d, ref tail);
                    Visit(cx - 1, cz, next, w, d, ref tail);
                    Visit(cx, cz + 1, next, w, d, ref tail);
                    Visit(cx, cz - 1, next, w, d, ref tail);
                }
            }

            void Visit(int x, int z, int cost, int w, int d, ref int tail)
            {
                if (x < 0 || z < 0 || x >= w || z >= d) return;
                var i = x + w * z;
                if (Cost[i] != Unreached || !Roam(x, z)) return;
                Cost[i] = cost;
                Queue[tail++] = i;
            }

            bool Roam(int x, int z) => HoldNav.Roamable(Cells[x + Size.x * (z + Size.z * Level)]);
        }
    }
}
