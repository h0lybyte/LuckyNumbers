using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.HoldScene
{
    /// <summary>Where in a region a cache sits: a chamber's wall, or the far end of a dead-end spur.</summary>
    public enum LifeSpot : byte { Chamber, DeadEnd, Locked }

    /// <summary>A strongbox: its world cell, the wall it's pushed against (xz unit step), and what's inside.</summary>
    public struct CachePlan
    {
        public int3 Cell;
        public int2 Wall;
        public LifeSpot Spot;
        /// <summary>Index into HoldLife.tiers, and into that tier's items (-1: no tier at this depth).</summary>
        public int Tier, Item;
    }

    /// <summary>A creature: its world cell (a chamber's open middle) and which HoldLife.creatures entry it is.</summary>
    public struct CreaturePlan
    {
        public int3 Cell;
        public int Creature;
    }

    /// <summary>
    /// Plain placement rules for HoldLife (no Unity objects, so the editor check runs them headless): from a region's
    /// grid and hash(seed, region) alone, where its caches and creatures go. Chambers are found from the grid (cells
    /// whose eight neighbours are all open, flooded into groups), dead ends are mine cells with one way out. Nothing
    /// is placed on or beside a shaft, or within two cells of the region's edge (where the openings are).
    /// </summary>
    public static class HoldLifePlan
    {
        const uint CacheTag = 0xCAC4Eu, CreatureTag = 0xC4EA7u, RewardTag = 0x4E3A4Du;
        static readonly int2[] Four = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        /// <summary>Regions down from the top (region y 0: 0, y -3: 3).</summary>
        public static int Depth(int3 region) => math.max(0, -region.y);

        /// <summary>Walkable, not a pillar, not a shaft: something can stand (or sit) here.</summary>
        public static bool Standable(in HoldGrid g, int3 c)
        {
            var cell = g.Get(c);
            return cell.Walkable && cell.Kind != CellKind.ShaftDown && cell.Kind != CellKind.ShaftUp && (cell.Flags & CellFlags.Pillar) == 0;
        }

        /// <summary>A creature may step from one cell to the next (same level, both standable). Not through a
        /// doorway: the puzzles' doors and portcullises (MinePasses.Puzzles) stand in Door cells.</summary>
        public static bool Passable(HoldCell cell) =>
            cell.Walkable && cell.Kind != CellKind.ShaftDown && cell.Kind != CellKind.ShaftUp && cell.Kind != CellKind.Door &&
            (cell.Flags & CellFlags.Pillar) == 0;

        /// <summary>Clear of shafts (Chebyshev 1, this level and the ones above / below) and two cells in from the edge.</summary>
        public static bool Clear(in HoldGrid g, int3 c)
        {
            if (c.x < 2 || c.z < 2 || c.x > g.Size.x - 3 || c.z > g.Size.z - 3) return false;
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var k = g.Get(c + new int3(dx, dy, dz)).Kind;
                if (k == CellKind.ShaftDown || k == CellKind.ShaftUp) return false;
            }
            return true;
        }

        static bool Mine(in HoldGrid g, int3 c) => g.Get(c).Kind == CellKind.Mine;

        /// <summary>A chamber's middle: a mine cell whose eight neighbours on its level are all walkable.</summary>
        static bool Open(in HoldGrid g, int3 c)
        {
            if (!Mine(g, c)) return false;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
                if (!g.Get(c + new int3(dx, 0, dz)).Walkable) return false;
            return true;
        }

        /// <summary>The chambers of a grid: groups of open cells (local), in scan order.</summary>
        public static List<List<int3>> Chambers(in HoldGrid g)
        {
            var result = new List<List<int3>>();
            var seen = new HashSet<int3>();
            var queue = new Queue<int3>();
            for (var l = 0; l < g.Size.y; l++)
            for (var z = 1; z < g.Size.z - 1; z++)
            for (var x = 1; x < g.Size.x - 1; x++)
            {
                var start = new int3(x, l, z);
                if (seen.Contains(start) || !Open(g, start)) continue;
                var group = new List<int3>();
                seen.Add(start);
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    group.Add(c);
                    foreach (var d in Four)
                    {
                        var n = c + new int3(d.x, 0, d.y);
                        if (seen.Contains(n) || !Open(g, n)) continue;
                        seen.Add(n);
                        queue.Enqueue(n);
                    }
                }
                group.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));
                result.Add(group);
            }
            return result;
        }

        /// <summary>Dead ends: standable mine cells with exactly one walkable neighbour on their level (local), in scan order.</summary>
        public static List<(int3 cell, int2 wall)> DeadEnds(in HoldGrid g)
        {
            var result = new List<(int3, int2)>();
            for (var l = 0; l < g.Size.y; l++)
            for (var z = 0; z < g.Size.z; z++)
            for (var x = 0; x < g.Size.x; x++)
            {
                var c = new int3(x, l, z);
                if (!Mine(g, c) || !Standable(g, c)) continue;
                var ways = 0;
                var way = int2.zero;
                foreach (var d in Four)
                    if (g.Get(c + new int3(d.x, 0, d.y)).Walkable) { ways++; way = d; }
                if (ways == 1) result.Add((c, -way));
            }
            return result;
        }

        static float Chance(float at0, float perDepth, int depth, float max) => math.clamp(at0 + perDepth * depth, 0f, max);

        /// <summary>A tier for this depth by weight (roll in [0, 1)); -1 if none has any weight here.</summary>
        public static int PickTier(HoldLife life, int depth, float roll)
        {
            var total = 0f;
            for (var i = 0; i < life.Tiers.Count; i++) total += TierWeight(life.Tiers[i], depth);
            if (total <= 0f) return -1;
            roll *= total;
            for (var i = 0; i < life.Tiers.Count; i++)
            {
                var w = TierWeight(life.Tiers[i], depth);
                if (w <= 0f) continue;
                roll -= w;
                if (roll < 0f) return i;
            }
            return life.Tiers.Count - 1;
        }

        public static float TierWeight(CacheTier t, int depth) =>
            depth < t.MinDepth || t.Items.Count == 0 ? 0f : math.max(0f, t.Weight + t.WeightPerDepth * depth);

        public static float CreatureWeight(HoldCreature c, int depth) =>
            depth < c.MinDepth ? 0f : math.max(0f, c.Weight + c.WeightPerDepth * depth);

        static int PickCreature(HoldLife life, int depth, float roll)
        {
            var total = 0f;
            foreach (var c in life.Creatures) total += CreatureWeight(c, depth);
            if (total <= 0f) return -1;
            roll *= total;
            for (var i = 0; i < life.Creatures.Count; i++)
            {
                var w = CreatureWeight(life.Creatures[i], depth);
                if (w <= 0f) continue;
                roll -= w;
                if (roll < 0f) return i;
            }
            return life.Creatures.Count - 1;
        }

        static Random Rng(uint seed, uint tag, int3 region)
        {
            var h = MineGenerator.Hash(seed, tag, region.x, region.y, region.z, 0);
            return new Random(h == 0 ? 0x9E3779B9u : h);
        }

        /// <summary>Where `region`'s caches and creatures go (world cells). Mine cells only: other sources' areas get none.</summary>
        public static void Plan(uint seed, int3 region, in HoldGrid g, HoldLife life, List<CachePlan> caches, List<CreaturePlan> creatures)
        {
            caches.Clear();
            creatures.Clear();
            if (life == null || !g.IsCreated) return;
            var depth = Depth(region);
            var chambers = Chambers(g);
            var taken = new HashSet<int3>();
            var max = math.max(0, life.CachesMax);

            // Nothing on a fixture's cell (doors, keys, levers: MinePasses). Behind each locked door, first, the
            // pass's reward spot gets a strongbox rolled as if deeper (its own hash, so the others roll as before).
            if (g.Fixtures.IsCreated)
            {
                foreach (var f in g.Fixtures) taken.Add(f.Cell);
                var rewards = Rng(seed, RewardTag, region);
                foreach (var f in g.Fixtures)
                {
                    if (!MinePasses.PuzzleReward(g, f, out var spot, out var bonus)) continue;
                    var loot = rewards.NextFloat();
                    var item = rewards.NextUInt();
                    if (caches.Count >= max || !Mine(g, spot) || !Standable(g, spot) || !Clear(g, spot) || taken.Contains(spot)) continue;
                    var wall = int2.zero;
                    foreach (var d in Four)
                        if (!g.Get(spot + new int3(d.x, 0, d.y)).Walkable) { wall = d; break; }
                    if (wall.Equals(int2.zero)) continue;
                    taken.Add(spot);
                    caches.Add(Cache(life, depth + bonus, g.ToWorld(spot), wall, LifeSpot.Locked, loot, item));
                }
            }

            // Caches: each chamber, then each dead end, rolls once; a chamber's box goes against its wall.
            var rng = Rng(seed, CacheTag, region);
            var chamberChance = Chance(life.CacheChamberChance, life.CacheChancePerDepth, depth, 0.95f);
            var endChance = Chance(life.CacheDeadEndChance, life.CacheChancePerDepth, depth, 0.95f);
            var wallCells = new List<(int3, int2)>();
            foreach (var chamber in chambers)
            {
                var roll = rng.NextFloat();
                var pick = rng.NextFloat();
                var loot = rng.NextFloat();
                var item = rng.NextUInt();
                if (caches.Count >= max || roll >= chamberChance) continue;
                wallCells.Clear();
                var inside = new HashSet<int3>(chamber);
                // The chamber's ring: cells beside its open middle that touch rock.
                var ring = new HashSet<int3>();
                foreach (var c in chamber)
                    for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        ring.Add(c + new int3(dx, 0, dz));
                foreach (var c in ring)
                {
                    if (inside.Contains(c) || !Mine(g, c) || !Standable(g, c) || !Clear(g, c) || taken.Contains(c)) continue;
                    foreach (var d in Four)
                        if (!g.Get(c + new int3(d.x, 0, d.y)).Walkable) { wallCells.Add((c, d)); break; }
                }
                if (wallCells.Count == 0) continue;
                wallCells.Sort((a, b) => Order(a.Item1, b.Item1));
                var (cell, wall) = wallCells[(int)(pick * wallCells.Count) % wallCells.Count];
                taken.Add(cell);
                caches.Add(Cache(life, depth, g.ToWorld(cell), wall, LifeSpot.Chamber, loot, item));
            }
            foreach (var (cell, wall) in DeadEnds(g))
            {
                var roll = rng.NextFloat();
                var loot = rng.NextFloat();
                var item = rng.NextUInt();
                if (caches.Count >= max || roll >= endChance || !Clear(g, cell) || taken.Contains(cell)) continue;
                taken.Add(cell);
                caches.Add(Cache(life, depth, g.ToWorld(cell), wall, LifeSpot.DeadEnd, loot, item));
            }

            // Creatures: chambers only, from creatures_from_depth down, denser deeper.
            if (depth < life.CreaturesFromDepth || life.Creatures.Count == 0) return;
            var crng = Rng(seed, CreatureTag, region);
            var below = depth - life.CreaturesFromDepth;
            var chance = Chance(life.CreatureChamberChance, life.CreatureChancePerDepth, below, math.max(0f, life.CreatureChanceMax));
            var cap = math.max(0, life.CreaturesMax);
            foreach (var chamber in chambers)
            {
                var roll = crng.NextFloat();
                var which = crng.NextFloat();
                var at = crng.NextInt(chamber.Count);
                if (creatures.Count >= cap || roll >= chance) continue;
                var cell = chamber[at];
                if (!Standable(g, cell) || !Clear(g, cell) || taken.Contains(cell)) continue;
                var type = PickCreature(life, depth, which);
                if (type < 0) continue;
                taken.Add(cell);
                creatures.Add(new CreaturePlan { Cell = g.ToWorld(cell), Creature = type });
            }
        }

        static int Order(int3 a, int3 b) => a.y != b.y ? a.y.CompareTo(b.y) : a.z != b.z ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x);

        static CachePlan Cache(HoldLife life, int depth, int3 cell, int2 wall, LifeSpot spot, float loot, uint item)
        {
            var tier = PickTier(life, depth, loot);
            var count = tier >= 0 ? life.Tiers[tier].Items.Count : 0;
            return new CachePlan { Cell = cell, Wall = wall, Spot = spot, Tier = tier, Item = count > 0 ? (int)(item % (uint)count) : -1 };
        }
    }
}
