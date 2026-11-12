using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Hold
{
    /// <summary>DepthRules (hold_depth.proto) flattened for the Depth pass.</summary>
    public partial struct MineParams
    {
        public float DropChance;
        public int DropsFromDepth;
        public int CollapsesMin, CollapsesMax, CollapsesFromDepth;
        public int SealEvery, KeysPerBand, KeyArea;

        partial void FromDepth(HoldConfig config)
        {
            var r = config.Depth ?? new DepthRules();
            DropChance = math.saturate(r.DropChance);
            DropsFromDepth = math.max(0, r.DropsFromDepth);
            CollapsesMin = math.max(0, r.CollapsesMin);
            CollapsesMax = math.max(CollapsesMin, r.CollapsesMax);
            CollapsesFromDepth = math.max(0, r.CollapsesFromDepth);
            SealEvery = math.max(0, r.SealEvery);
            KeysPerBand = math.max(1, r.KeysPerBand);
            KeyArea = math.max(0, r.KeyArea);
        }
    }

    /// <summary>
    /// The Depth pass's marks on cells (HoldCell.Reserved bits, only ever on ShaftDown / ShaftUp cells), shared with
    /// the mesher (lip, seal frame, no ladder) and the climbing code (HoldRules.LadderSide asks NoLadder).
    /// </summary>
    public static class DepthCells
    {
        /// <summary>Part of a drop shaft: the upper region's ShaftDown lip, the lower region's ShaftUp cells (no ladder).</summary>
        public const byte DropBit = 0x80;
        /// <summary>A ShaftDown closed by a depth seal (its Seal fixture is the hatch; the mesher draws the frame).</summary>
        public const byte SealBit = 0x40;

        static bool Shaft(HoldCell c) => c.Kind == CellKind.ShaftDown || c.Kind == CellKind.ShaftUp;
        public static bool IsDrop(HoldCell c) => Shaft(c) && (c.Reserved & DropBit) != 0;
        public static bool IsDropLip(HoldCell c) => c.Kind == CellKind.ShaftDown && (c.Reserved & DropBit) != 0;
        public static bool IsSealed(HoldCell c) => c.Kind == CellKind.ShaftDown && (c.Reserved & SealBit) != 0;
        /// <summary>A ShaftUp with no ladder (a drop shaft's): HoldRules.LadderSide gives zero for it.</summary>
        public static bool NoLadder(HoldCell c) => c.Kind == CellKind.ShaftUp && (c.Reserved & DropBit) != 0;
    }

    /// <summary>
    /// Depth (docs/holds.md, "Depth"): drop shafts, depth seals and their keys, collapses. Everything two regions must
    /// agree on comes from hashes of shared coordinates: a drop from the (upper) region's id (as ShaftBelow), a band's
    /// key regions from (seed, band), a seal from the region's depth alone.
    /// </summary>
    public static partial class MinePasses
    {
        const uint TagDrop = 0x44524F50u, TagRubble = 0x52554242u, TagKey = 0x534B4559u, TagKeyCell = 0x4B43454Cu;

        /// <summary>Regions down from the top (region y 0: 0), as HoldLifePlan.Depth.</summary>
        public static int DepthOf(int3 region) => math.max(0, -region.y);

        /// <summary>The band a depth is in (0 when there are no seals).</summary>
        public static int BandOf(int depth, in MineParams p) => p.SealEvery > 0 ? depth / p.SealEvery : 0;

        /// <summary>Top depth of a band, and its bottom (whose ways down are sealed).</summary>
        public static int BandTop(int band, in MineParams p) => band * math.max(1, p.SealEvery);
        public static int BandBottom(int band, in MineParams p) => BandTop(band, p) + math.max(1, p.SealEvery) - 1;

        /// <summary>Every way down out of this region is sealed: it is the bottom depth of its band.</summary>
        public static bool SealedBelow(int3 region, in MineParams p) =>
            p.SealEvery > 0 && region.y <= 0 && DepthOf(region) % p.SealEvery == p.SealEvery - 1;

        /// <summary>
        /// Whether the shaft from `upper` into the region below (MineGenerator.ShaftBelow) is a drop: no ladder, and it
        /// falls on through the lower region's top storey onto the one under it (two storeys). Never out of a sealed
        /// depth, never above drops_from_depth, never where the landing would crowd the lower region's own shaft down.
        /// Both regions ask this with the same `upper`, so they always agree.
        /// </summary>
        public static bool DropBelow(uint seed, int3 upper, in MineParams p, out int2 cell)
        {
            if (!MineGenerator.ShaftBelow(seed, upper, p, out cell)) return false;
            if (upper.y > 0 || p.Levels < 2 || DepthOf(upper) < p.DropsFromDepth || SealedBelow(upper, p)) return false;
            var roll = (MineGenerator.Hash(seed, TagDrop, upper.x, upper.y, upper.z, 0) >> 8) / (float)(1 << 24);
            if (roll >= p.DropChance) return false;
            if (MineGenerator.ShaftBelow(seed, upper - new int3(0, 1, 0), p, out var next) && math.cmax(math.abs(next - cell)) < 3) return false;
            return true;
        }

        /// <summary>
        /// The i-th region holding band `band`'s seal key (some may repeat). Key 0 is always on the band's top depth in
        /// row z 0 (east-west seams always open on a region's lowest level, so that row is one walk from the hold's
        /// column); the others anywhere in the band within key_area of x, z 0.
        /// </summary>
        public static int3 KeyRegion(uint seed, int band, int i, in MineParams p)
        {
            var rng = new Random(MineGenerator.Hash(seed, TagKey, band, i, 0, 0) | 1u);
            var a = p.KeyArea;
            var x = rng.NextInt(-a, a + 1);
            var z = i == 0 ? 0 : rng.NextInt(-a, a + 1);
            var depth = BandTop(band, p) + (i == 0 ? 0 : rng.NextInt(math.max(1, p.SealEvery)));
            return new int3(x, -depth, z);
        }

        public static bool HoldsKey(uint seed, int3 region, in MineParams p)
        {
            if (p.SealEvery <= 0 || region.y > 0) return false;
            var band = BandOf(DepthOf(region), p);
            for (var i = 0; i < p.KeysPerBand; i++)
                if (KeyRegion(seed, band, i, p).Equals(region)) return true;
            return false;
        }

        static partial void Depth(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (region.y > 0) return;
            DropLanding(seed, region, p, ref grid);
            DropLip(seed, region, p, ref grid);
            Seals(region, p, ref grid);
            SealKey(seed, region, p, ref grid);
            Collapses(seed, region, p, ref grid);
        }

        // ------------------------------------------------------------------------------------------- drops

        /// <summary>The upper half: mark the ShaftDown and put a DropLip fixture on it (Data: storeys it falls).</summary>
        static void DropLip(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (!DropBelow(seed, region, p, out var col)) return;
            var c = new int3(col.x, 0, col.y);
            var cell = grid.Get(c);
            if (cell.Kind != CellKind.ShaftDown) return;
            cell.Reserved |= DepthCells.DropBit;
            grid.Set(c, cell);
            grid.Fixtures.Add(new HoldFixture { Kind = FixtureKind.DropLip, Cell = c, Data = 2 });
        }

        /// <summary>
        /// The lower half: the top storey's ShaftUp loses its ladder and the storey under it opens into the same
        /// shaft (a ShaftUp without ladder over a ShaftUp: no floor between, no move in HoldNav), landing on that
        /// storey's floor, joined to its tunnels by the shortest dig if it was rock.
        /// </summary>
        static void DropLanding(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (!DropBelow(seed, region + new int3(0, 1, 0), p, out var col)) return;
            var top = new int3(col.x, grid.Size.y - 1, col.y);
            var land = top - new int3(0, 1, 0);
            var topCell = grid.Get(top);
            var landCell = grid.Get(land);
            if (topCell.Kind != CellKind.ShaftUp) return;
            var plain = landCell.Kind == CellKind.Solid || landCell.Kind == CellKind.Mine || landCell.Kind == CellKind.Corridor || landCell.Kind == CellKind.Floor;
            var under = grid.Get(land - new int3(0, 1, 0));
            if (!plain || under.Kind == CellKind.ShaftUp || (under.Flags & CellFlags.OpenAbove) != 0) return;
            if (landCell.Kind == CellKind.Solid && !DigToOpen(ref grid, land, topCell.Palette)) return;
            topCell.Reserved |= DepthCells.DropBit;
            grid.Set(top, topCell);
            landCell = grid.Get(land);
            landCell.Kind = CellKind.ShaftUp;
            landCell.Flags &= ~(CellFlags.Pillar | CellFlags.OpenAbove);
            landCell.Palette = topCell.Palette;
            landCell.Reserved = DepthCells.DropBit;
            grid.Set(land, landCell);
        }

        /// <summary>Shortest dig on c's level from rock cell c to the nearest standable cell (interior only).</summary>
        static bool DigToOpen(ref HoldGrid grid, int3 c, byte palette)
        {
            int n = grid.Size.x, m = grid.Size.z;
            var prev = new NativeArray<int>(n * m, Allocator.Temp);
            for (var i = 0; i < prev.Length; i++) prev[i] = -1;
            var queue = new NativeList<int2>(64, Allocator.Temp);
            queue.Add(c.xz);
            prev[c.x + n * c.z] = c.x + n * c.z;
            var found = -1;
            for (var head = 0; head < queue.Length && found < 0; head++)
            {
                var at = queue[head];
                for (var d = 0; d < 4; d++)
                {
                    var o = HoldRules.Around(d).xz;
                    var nb = at + o;
                    if (nb.x < 1 || nb.y < 1 || nb.x > n - 2 || nb.y > m - 2) continue;
                    var ni = nb.x + n * nb.y;
                    if (prev[ni] >= 0) continue;
                    var cell = grid.Get(new int3(nb.x, c.y, nb.y));
                    prev[ni] = at.x + n * at.y;
                    if (HoldNav.Standable(cell) && cell.Kind != CellKind.ShaftDown && cell.Kind != CellKind.ShaftUp) { found = ni; break; }
                    if (cell.Kind == CellKind.Solid) queue.Add(nb);
                }
            }
            if (found >= 0)
                for (var i = prev[found]; i != c.x + n * c.z; i = prev[i])
                    grid.Set(new int3(i % n, c.y, i / n), new HoldCell { Kind = CellKind.Mine, Palette = palette });
            prev.Dispose();
            queue.Dispose();
            return found >= 0;
        }

        // ------------------------------------------------------------------------------------------- seals

        /// <summary>
        /// At a band's bottom depth every way down is sealed: each ShaftDown on level 0 (into the region below) and any
        /// GreatShaft cell there gets a Seal fixture (Data, Link: the band). A seal must not cut the region in two
        /// (a tunnel may run through the shaft's cell): if it would, the ring round it is dug out (props cleared).
        /// </summary>
        static void Seals(int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (!SealedBelow(region, p)) return;
            var band = BandOf(DepthOf(region), p);
            var start = Start(grid);
            var seen = new NativeArray<bool>(grid.Cells.Length, Allocator.Temp);
            var before = FloodOpen(grid, start, seen);
            var first = grid.Fixtures.Length;
            var blockedReached = 0;
            for (var z = 0; z < grid.Size.z; z++)
            for (var x = 0; x < grid.Size.x; x++)
            {
                var c = new int3(x, 0, z);
                var cell = grid.Get(c);
                if (cell.Kind != CellKind.ShaftDown && cell.Kind != CellKind.GreatShaft) continue;
                if (cell.Kind == CellKind.ShaftDown)
                {
                    cell.Reserved |= DepthCells.SealBit;
                    grid.Set(c, cell);
                }
                if (seen[grid.Index(c)]) blockedReached++;
                grid.Fixtures.Add(new HoldFixture { Kind = FixtureKind.Seal, Cell = c, Link = (ushort)band, Data = band });
            }
            if (FloodOpen(grid, start, seen) != before - blockedReached)
            {
                for (var f = first; f < grid.Fixtures.Length; f++) Ring(ref grid, grid.Fixtures[f].Cell);
                before = FloodOpen(grid, start, seen);   // (the ring added cells; nothing should be cut off now)
                var all = 0;
                for (var i = 0; i < grid.Cells.Length; i++)
                    if (HoldNav.Standable(grid.Cells[i])) all++;
                if (before != all - blockedReached)
                    Debug.LogWarning($"[MinePasses] region {region}: a seal still cuts the region ({before}/{all - blockedReached})");
            }
            seen.Dispose();
        }

        /// <summary>Dig (or clear the props of) the eight cells round c on its level, so its neighbours join round it.</summary>
        static void Ring(ref HoldGrid grid, int3 c)
        {
            var palette = grid.Get(c).Palette;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var r = c + new int3(dx, 0, dz);
                if (r.x < 1 || r.z < 1 || r.x > grid.Size.x - 2 || r.z > grid.Size.z - 2) continue;
                var cell = grid.Get(r);
                if (cell.Kind == CellKind.Solid) grid.Set(r, new HoldCell { Kind = CellKind.Mine, Palette = palette });
                else if ((cell.Flags & CellFlags.Pillar) != 0) { cell.Flags &= ~CellFlags.Pillar; grid.Set(r, cell); }
            }
        }

        /// <summary>
        /// A band's key regions each get one SealKey (Data, Link: the band) on a tunnel cell (two ways out: never where
        /// caches or creatures go) reachable from the west seam with every door, gate and seal shut.
        /// </summary>
        static void SealKey(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (!HoldsKey(seed, region, p)) return;
            var band = BandOf(DepthOf(region), p);
            var seen = new NativeArray<bool>(grid.Cells.Length, Allocator.Temp);
            HoldNav.Flood(grid, Start(grid), true, seen);
            var candidates = new NativeList<int3>(64, Allocator.Temp);
            for (var pass = 0; pass < 2 && candidates.Length == 0; pass++)
                for (var i = 0; i < grid.Cells.Length; i++)
                {
                    if (!seen[i]) continue;
                    var c = CellAt(grid, i);
                    if (KeySpot(grid, c, pass == 0)) candidates.Add(c);
                }
            if (candidates.Length > 0)
            {
                var rng = new Random(MineGenerator.Hash(seed, TagKeyCell, region.x, region.y, region.z, 0) | 1u);
                var at = candidates[rng.NextInt(candidates.Length)];
                grid.Fixtures.Add(new HoldFixture { Kind = FixtureKind.SealKey, Cell = at, Link = (ushort)band, Data = band });
            }
            else Debug.LogWarning($"[MinePasses] region {region}: nowhere to put band {band}'s seal key");
            candidates.Dispose();
            seen.Dispose();
        }

        static bool KeySpot(in HoldGrid g, int3 c, bool strict)
        {
            var cell = g.Get(c);
            if (cell.Kind != CellKind.Mine || cell.Flags != CellFlags.None) return false;
            if (c.x < 2 || c.z < 2 || c.x > g.Size.x - 3 || c.z > g.Size.z - 3) return false;
            if (!ClearOfSpecial(g, c, 1)) return false;
            if (!strict) return true;
            var ways = 0;
            for (var d = 0; d < 4; d++) if (HoldNav.Standable(g.Get(c + HoldRules.Around(d)))) ways++;
            return ways == 2;
        }

        // ------------------------------------------------------------------------------------------- collapses

        /// <summary>
        /// collapses_min..max straight tunnel cells become Rubble, each kept only if the region stays as connected as
        /// it was (every cell still reached from the west seam, with doors open and with them shut).
        /// </summary>
        static void Collapses(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (DepthOf(region) < p.CollapsesFromDepth || p.CollapsesMax <= 0) return;
            var rng = new Random(MineGenerator.Hash(seed, TagRubble, region.x, region.y, region.z, 0) | 1u);
            var want = rng.NextInt(p.CollapsesMin, p.CollapsesMax + 1);
            if (want == 0) return;
            var candidates = new NativeList<int3>(128, Allocator.Temp);
            for (var i = 0; i < grid.Cells.Length; i++)
            {
                var c = CellAt(grid, i);
                if (RubbleSpot(grid, c)) candidates.Add(c);
            }
            for (var i = candidates.Length - 1; i > 0; i--)
            {
                var j = rng.NextInt(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }
            var start = Start(grid);
            var seen = new NativeArray<bool>(grid.Cells.Length, Allocator.Temp);
            var open = FloodOpen(grid, start, seen);
            var shut = HoldNav.Flood(grid, start, true, seen);
            var placed = 0;
            for (var k = 0; k < candidates.Length && k < 32 && placed < want; k++)
            {
                var c = candidates[k];
                if (!RubbleSpot(grid, c)) continue;   // a neighbour fell in already
                var was = grid.Get(c);
                grid.Set(c, new HoldCell { Kind = CellKind.Rubble, Palette = was.Palette });
                // The cell was reached both ways (a plain tunnel cell next to two standable ones on a connected region).
                var openNow = FloodOpen(grid, start, seen);
                var shutNow = openNow == open - 1 ? HoldNav.Flood(grid, start, true, seen) : -1;
                if (openNow == open - 1 && shutNow == shut - 1) { open = openNow; shut = shutNow; placed++; }
                else grid.Set(c, was);
            }
            seen.Dispose();
            candidates.Dispose();
        }

        /// <summary>A plain straight tunnel cell (rock both sides, open both ends), clear of anything special.</summary>
        static bool RubbleSpot(in HoldGrid g, int3 c)
        {
            var cell = g.Get(c);
            if (cell.Kind != CellKind.Mine || cell.Flags != CellFlags.None || cell.Reserved != 0) return false;
            if (c.x < 2 || c.z < 2 || c.x > g.Size.x - 3 || c.z > g.Size.z - 3) return false;
            if ((g.Get(c - new int3(0, 1, 0)).Flags & CellFlags.OpenAbove) != 0) return false;
            var ex = Plain(g, c + new int3(1, 0, 0)) && Plain(g, c - new int3(1, 0, 0));
            var ez = Plain(g, c + new int3(0, 0, 1)) && Plain(g, c - new int3(0, 0, 1));
            var wx = g.Get(c + new int3(1, 0, 0)).Kind == CellKind.Solid && g.Get(c - new int3(1, 0, 0)).Kind == CellKind.Solid;
            var wz = g.Get(c + new int3(0, 0, 1)).Kind == CellKind.Solid && g.Get(c - new int3(0, 0, 1)).Kind == CellKind.Solid;
            if (!(ex && wz) && !(ez && wx)) return false;
            return ClearOfSpecial(g, c, 2);
        }

        static bool Plain(in HoldGrid g, int3 c)
        {
            var cell = g.Get(c);
            return cell.Kind == CellKind.Mine && (cell.Flags & CellFlags.Pillar) == 0;
        }

        /// <summary>No shaft, stair, door, rubble or fixture within Chebyshev r on c's level (and nothing linked above/below).</summary>
        static bool ClearOfSpecial(in HoldGrid g, int3 c, int r)
        {
            for (var dz = -r; dz <= r; dz++)
            for (var dx = -r; dx <= r; dx++)
            {
                var k = g.Get(c + new int3(dx, 0, dz)).Kind;
                if (k != CellKind.Solid && k != CellKind.Mine) return false;
            }
            if (g.Fixtures.IsCreated)
                foreach (var f in g.Fixtures)
                    if (f.Cell.y == c.y && math.cmax(math.abs(f.Cell.xz - c.xz)) <= r) return false;
            return true;
        }

        // ------------------------------------------------------------------------------------------- shared

        /// <summary>A walkable cell on the west seam of level 0 (east-west seams there always open), else any standable one.</summary>
        static int3 Start(in HoldGrid g)
        {
            for (var z = 0; z < g.Size.z; z++)
                if (HoldNav.Standable(g.Get(new int3(0, 0, z)))) return new int3(0, 0, z);
            for (var i = 0; i < g.Cells.Length; i++)
                if (HoldNav.Standable(g.Cells[i])) return CellAt(g, i);
            return int3.zero;
        }

        static int3 CellAt(in HoldGrid g, int i) => new(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);

        /// <summary>HoldNav.Flood with doors and gates open but seals shut (the region as you walk it before its key).</summary>
        public static int FloodOpen(in HoldGrid g, int3 from, NativeArray<bool> seen)
        {
            for (var i = 0; i < seen.Length; i++) seen[i] = false;
            if (!g.InBounds(from) || !HoldNav.Standable(g.Get(from))) return 0;
            var blocked = new NativeArray<bool>(seen.Length, Allocator.Temp);
            if (g.Fixtures.IsCreated)
                foreach (var f in g.Fixtures)
                    if (f.Kind == FixtureKind.Seal && g.InBounds(f.Cell)) blocked[g.Index(f.Cell)] = true;
            var queue = new NativeList<int3>(256, Allocator.Temp);
            queue.Add(from);
            seen[g.Index(from)] = true;
            var moves = new FixedList128Bytes<int3>();
            for (var head = 0; head < queue.Length; head++)
            {
                moves.Clear();
                HoldNav.Moves(g, queue[head], ref moves);
                foreach (var to in moves)
                {
                    if (!g.InBounds(to)) continue;
                    var i = g.Index(to);
                    if (seen[i] || blocked[i] || !HoldNav.Standable(g.Get(to))) continue;
                    seen[i] = true;
                    queue.Add(to);
                }
            }
            var count = queue.Length;
            queue.Dispose();
            blocked.Dispose();
            return count;
        }
    }
}
