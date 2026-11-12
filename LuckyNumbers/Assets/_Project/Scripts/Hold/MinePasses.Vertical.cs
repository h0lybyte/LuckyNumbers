using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Hold
{
    /// <summary>VerticalRules flattened (MineParams.FromVertical).</summary>
    public partial struct MineParams
    {
        public float StairChance, ReplaceShaftChance, TimberChance;
        public int StairsMax, FlightMin, FlightMax;
        public float GreatChance;
        public int GreatSpacing, GreatBand, GreatRegionsMin, GreatRegionsMax, GreatSize, LandingEvery;

        partial void FromVertical(HoldConfig config)
        {
            var r = config.Vertical ?? new VerticalRules();
            StairChance = math.saturate(r.StairChance);
            StairsMax = math.max(0, r.StairsMax);
            FlightMin = math.clamp(r.FlightMin, StairCell.MinLength, StairCell.MaxLength);
            FlightMax = math.clamp(r.FlightMax, FlightMin, StairCell.MaxLength);
            ReplaceShaftChance = math.saturate(r.ReplaceShaftChance);
            TimberChance = math.saturate(r.TimberChance);
            GreatChance = math.saturate(r.GreatChance);
            GreatSpacing = math.max(1, r.GreatSpacing);
            GreatBand = math.max(1, r.GreatBand);
            GreatRegionsMin = math.clamp(r.GreatRegionsMin, 1, GreatBand);
            GreatRegionsMax = math.clamp(r.GreatRegionsMax, GreatRegionsMin, GreatBand);
            GreatSize = math.clamp(r.GreatSize, 2, 3);
            LandingEvery = math.max(1, r.LandingEvery);
        }
    }

    /// <summary>
    /// HoldCell.Reserved for Stair cells. A flight climbs one storey in a straight line over 3..6 cells on the lower
    /// storey ("steps", which also carry OpenAbove), under an open well of the same cells on the upper storey; you
    /// walk on from the cell before the first step (lower storey) and off onto the cell past the last (upper).
    /// Bits 0-1: direction up the flight (0 +x, 1 -x, 2 +z, 3 -z); bits 2-4: cell index from the bottom; bit 5: timber
    /// treads (else carved stone); bit 7: well (the upper-storey cell over step `index`). A flight's length is found
    /// by walking it (index + the steps after this one).
    /// </summary>
    public static class StairCell
    {
        public const int MinLength = 3, MaxLength = 6, TreadsPerCell = 4;
        public const byte TimberBit = 1 << 5, WellBit = 1 << 7;

        public static byte Encode(int dir, int index, bool timber, bool well) =>
            (byte)((dir & 3) | ((index & 7) << 2) | (timber ? TimberBit : 0) | (well ? WellBit : 0));

        public static int Dir(HoldCell c) => c.Reserved & 3;
        public static int Index(HoldCell c) => (c.Reserved >> 2) & 7;
        public static bool Timber(HoldCell c) => (c.Reserved & TimberBit) != 0;
        public static bool IsStep(HoldCell c) => c.Kind == CellKind.Stair && (c.Reserved & WellBit) == 0;
        public static bool IsWell(HoldCell c) => c.Kind == CellKind.Stair && (c.Reserved & WellBit) != 0;

        /// <summary>Direction index -> unit vector (0 +x, 1 -x, 2 +z, 3 -z), as HoldFixture.Dir.</summary>
        public static int3 Vec(int dir) => (dir & 3) switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 0, 1), _ => new int3(0, 0, -1),
        };

        /// <summary>The next step up the same flight is at c + Vec(dir).</summary>
        public static bool NextIsStep(in HoldGrid g, int3 c, HoldCell here)
        {
            var next = g.Get(c + Vec(Dir(here)));
            return IsStep(next) && Dir(next) == Dir(here) && Index(next) == Index(here) + 1;
        }

        /// <summary>Cells in the flight of step c (index + this one + those after it).</summary>
        public static int Length(in HoldGrid g, int3 c, HoldCell here)
        {
            var length = Index(here) + 1;
            var at = c;
            var cell = here;
            while (length < MaxLength + 1 && NextIsStep(g, at, cell))
            {
                at += Vec(Dir(cell));
                cell = g.Get(at);
                length++;
            }
            return length;
        }
    }

    /// <summary>
    /// HoldCell.Reserved for GreatShaft cells. Bit 0: floor here (the shaft's bottom storey); bit 1: ceiling here (its
    /// top storey); bits 2-3: dock side (0 +x, 1 -x, 2 +z, 3 -z: the landings' gate and the hoist's open side); bits
    /// 4-5 / 6-7: this cell's x / z index in the shaft's footprint.
    /// </summary>
    public static class GreatShaftCell
    {
        public const byte FloorBit = 1, CeilingBit = 2;

        public static byte Encode(bool floor, bool ceiling, int dock, int ix, int iz) =>
            (byte)((floor ? FloorBit : 0) | (ceiling ? CeilingBit : 0) | ((dock & 3) << 2) | ((ix & 3) << 4) | ((iz & 3) << 6));

        public static bool Floor(HoldCell c) => (c.Reserved & FloorBit) != 0;
        public static bool Ceiling(HoldCell c) => (c.Reserved & CeilingBit) != 0;
        public static int Dock(HoldCell c) => (c.Reserved >> 2) & 3;
        public static int Ix(HoldCell c) => (c.Reserved >> 4) & 3;
        public static int Iz(HoldCell c) => (c.Reserved >> 6) & 3;

        /// <summary>Footprint side length, by walking the row from cell c.</summary>
        public static int Size(in HoldGrid g, int3 c, HoldCell here)
        {
            var size = Ix(here) + 1;
            for (var x = c.x + 1; size < 4 && g.Get(new int3(x, c.y, c.z)).Kind == CellKind.GreatShaft; x++) size++;
            return size;
        }
    }

    /// <summary>
    /// One great shaft: a Size x Size column of GreatShaft cells at the same local (x, z) in every region of one region
    /// column from region y TopRegion down to BottomRegion. Comes from a hash of its column group and depth band only
    /// (<see cref="MinePasses.GreatShaftOf"/>), so every region it passes through (and the hoist) agrees.
    /// </summary>
    public struct GreatShaft
    {
        public int2 Group;
        public int Band;
        /// <summary>Region (x, z) of the column it's in.</summary>
        public int2 Region;
        /// <summary>Local (x, z) of the footprint's low corner (the same in every region it passes).</summary>
        public int2 Min;
        public int Size;
        public int TopRegion, BottomRegion;
        /// <summary>Dock side (0 +x, 1 -x, 2 +z, 3 -z).</summary>
        public int Dock;

        /// <summary>World storey of the top (the top region's highest level) and the bottom.</summary>
        public readonly int TopLevel(in MineParams p) => TopRegion * p.Levels + p.Levels - 1;
        public readonly int BottomLevel(in MineParams p) => BottomRegion * p.Levels;
        /// <summary>World cell (x, z) of the footprint's low corner.</summary>
        public readonly int2 WorldMin(in MineParams p) => Region * p.Cells + Min;
        public readonly bool Contains(int3 region) =>
            region.x == Region.x && region.z == Region.y && region.y <= TopRegion && region.y >= BottomRegion;
        /// <summary>A planned landing (every landing_every storeys from the top, and the bottom). Regions add more where
        /// their mines already touched the shaft (the Hoist fixture's mask).</summary>
        public readonly bool PlannedLanding(int worldLevel, in MineParams p) =>
            worldLevel == BottomLevel(p) || (TopLevel(p) - worldLevel) % p.LandingEvery == 0;
        /// <summary>The footprint cell on the dock side's middle (local x, z), and the ring cell outside it.</summary>
        public readonly int2 DockEdge()
        {
            var m = Size / 2;
            return Dock switch
            {
                0 => Min + new int2(Size - 1, m), 1 => Min + new int2(0, m), 2 => Min + new int2(m, Size - 1), _ => Min + new int2(m, 0),
            };
        }
        public readonly int2 DockRing() => DockEdge() + StairCell.Vec(Dock).xz;
    }

    public static partial class MinePasses
    {
        const uint TagGreat = 0x47524554u, TagStairs = 0x53544152u;

        /// <summary>
        /// The great shaft (if any) of a column group (regions x, z floor-divided by great_spacing) and depth band
        /// (regions y 0 .. -(band-1) are band 0, and so on down). From hash(seed, group, band) only: present by
        /// great_chance; which region column in the group; how many regions it spans and where in the band it starts;
        /// its dock side; and its footprint, re-rolled until it's 2+ cells clear (Chebyshev) of every region-to-region
        /// shaft (MineGenerator.ShaftBelow) along its length, so no region has to give one up.
        /// </summary>
        public static bool GreatShaftOf(uint seed, int2 group, int band, in MineParams p, out GreatShaft s)
        {
            s = default;
            if (band < 0 || p.GreatChance <= 0f || p.Cells < 12) return false;
            var h = MineGenerator.Hash(seed, TagGreat, group.x, group.y, band, 0);
            var rng = new Random(h == 0 ? 0x2545F491u : h);
            if (rng.NextFloat() >= p.GreatChance) return false;
            var spacing = p.GreatSpacing;
            var regions = rng.NextInt(p.GreatRegionsMin, p.GreatRegionsMax + 1);
            s.Group = group;
            s.Band = band;
            s.Region = group * spacing + rng.NextInt2(int2.zero, new int2(spacing));
            s.TopRegion = -band * p.GreatBand - rng.NextInt(0, p.GreatBand - regions + 1);
            s.BottomRegion = s.TopRegion - regions + 1;
            s.Size = p.GreatSize;
            s.Dock = rng.NextInt(4);
            int lo = 3, hi = p.Cells - 3 - s.Size;   // ring and its tunnels clear of the margin
            for (var tries = 0; tries < 24; tries++)
            {
                s.Min = rng.NextInt2(new int2(lo), new int2(hi + 1));
                var clear = true;
                for (var ry = s.BottomRegion; ry <= s.TopRegion + 1 && clear; ry++)
                {
                    if (!MineGenerator.ShaftBelow(seed, new int3(s.Region.x, ry, s.Region.y), p, out var c)) continue;
                    clear = math.any(c < s.Min - 2) || math.any(c > s.Min + s.Size + 1);
                }
                if (clear) return true;
            }
            return false;
        }

        /// <summary>The great shaft passing through this region, if one does.</summary>
        public static bool GreatShaftAt(uint seed, int3 region, in MineParams p, out GreatShaft s)
        {
            s = default;
            if (region.y > 0) return false;
            var group = new int2(FloorDiv(region.x, p.GreatSpacing), FloorDiv(region.z, p.GreatSpacing));
            return GreatShaftOf(seed, group, -region.y / p.GreatBand, p, out s) && s.Contains(region);
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        static partial void Vertical(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (GreatShaftAt(seed, region, p, out var shaft)) CarveGreatShaft(region, p, shaft, ref grid);
            Stairs(seed, region, p, ref grid);
            if (!Connected(grid)) Debug.LogWarning($"[MinePasses] seed {seed} region {region}: vertical pass split the region");
        }

        // ------------------------------------------------------------------------------------------- great shafts

        /// <summary>
        /// The shaft's cells in this region, and a landing on each storey that is a planned landing or that the mines
        /// already touched (footprint or the ring round it): a ring of mine floor all round the footprint (so any
        /// tunnel the shaft cut still joins up round it), lanterns on two of its corners, and a tunnel from the dock
        /// side to the nearest mine cell. One Hoist fixture on the region's top level of the footprint (Data: storeys
        /// below it to the shaft's bottom, bits 16+: which local levels have landings).
        /// </summary>
        static void CarveGreatShaft(int3 region, in MineParams p, in GreatShaft s, ref HoldGrid grid)
        {
            var levels = p.Levels;
            var mask = 0;
            for (var l = 0; l < levels; l++)
            {
                var touched = false;
                for (var z = s.Min.y - 1; z <= s.Min.y + s.Size && !touched; z++)
                for (var x = s.Min.x - 1; x <= s.Min.x + s.Size && !touched; x++)
                    touched = grid.Get(new int3(x, l, z)).Walkable;
                if (touched || s.PlannedLanding(region.y * levels + l, p)) mask |= 1 << l;
            }

            for (var l = 0; l < levels; l++)
            {
                var landing = (mask & (1 << l)) != 0;
                for (var z = s.Min.y - 1; z <= s.Min.y + s.Size; z++)
                for (var x = s.Min.x - 1; x <= s.Min.x + s.Size; x++)
                {
                    var c = new int3(x, l, z);
                    int ix = x - s.Min.x, iz = z - s.Min.y;
                    var inside = ix >= 0 && iz >= 0 && ix < s.Size && iz < s.Size;
                    if (inside)
                    {
                        var floor = l == 0 && region.y == s.BottomRegion;
                        var ceiling = l == levels - 1 && region.y == s.TopRegion;
                        grid.Set(c, new HoldCell
                        {
                            Kind = CellKind.GreatShaft, Palette = p.Palette,
                            Reserved = GreatShaftCell.Encode(floor, ceiling, s.Dock, ix, iz),
                        });
                    }
                    else if (landing)
                    {
                        var old = grid.Get(c);
                        grid.Set(c, new HoldCell
                        {
                            Kind = CellKind.Mine, Palette = p.Palette,
                            Flags = old.Flags & CellFlags.Lantern,
                        });
                    }
                }
                if (!landing) continue;
                Lantern(ref grid, new int3(s.Min.x - 1, l, s.Min.y - 1));
                Lantern(ref grid, new int3(s.Min.x + s.Size, l, s.Min.y + s.Size));
                LandingTunnel(ref grid, s, l, p);
            }
            FixShaftPairs(ref grid);

            var top = new int3(s.Min.x, levels - 1, s.Min.y);
            grid.Fixtures.Add(new HoldFixture
            {
                Kind = FixtureKind.Hoist, Cell = top, Dir = (byte)s.Dock,
                Data = (region.y * levels + levels - 1 - s.BottomLevel(p)) | (mask << 16),
            });
        }

        static void Lantern(ref HoldGrid grid, int3 c)
        {
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dz != 0) && (grid.Get(c + new int3(dx, 0, dz)).Flags & CellFlags.Lantern) != 0) return;
            var cell = grid.Get(c);
            if (cell.Kind != CellKind.Mine) return;
            cell.Flags |= CellFlags.Lantern;
            grid.Set(c, cell);
        }

        /// <summary>Breadth-first from the dock ring cell through rock (interior only, not the shaft or its ring) to the
        /// nearest walkable cell; the rock on the way becomes mine tunnel.</summary>
        static void LandingTunnel(ref HoldGrid grid, in GreatShaft s, int l, in MineParams p)
        {
            var n = p.Cells;
            var start = s.DockRing();
            var prev = new NativeArray<int>(n * n, Allocator.Temp);
            for (var i = 0; i < prev.Length; i++) prev[i] = -2;
            var queue = new NativeList<int2>(64, Allocator.Temp);
            queue.Add(start);
            prev[start.x + n * start.y] = -1;
            var found = -1;
            for (var head = 0; head < queue.Length && found < 0; head++)
            {
                var at = queue[head];
                for (var k = 0; k < 4; k++)
                {
                    // Out of the dock side first (so the tunnel leaves straight), then the other three.
                    var dir = k == 0 ? s.Dock : k - 1 < s.Dock ? k - 1 : k;
                    var to = at + StairCell.Vec(dir).xz;
                    if (to.x < 1 || to.y < 1 || to.x > n - 2 || to.y > n - 2) continue;
                    var i = to.x + n * to.y;
                    if (prev[i] != -2) continue;
                    var nearShaft = math.all(to >= s.Min - 1) && math.all(to <= s.Min + s.Size);
                    if (nearShaft) continue;
                    prev[i] = at.x + n * at.y;
                    if (grid.Get(new int3(to.x, l, to.y)).Walkable) { found = i; break; }
                    queue.Add(to);
                }
            }
            for (var i = found; i >= 0 && prev[i] >= 0; i = prev[i])
            {
                var c = new int3(i % n, l, i / n);
                if (grid.Get(c).Walkable) continue;
                grid.Set(c, new HoldCell { Kind = CellKind.Mine, Palette = p.Palette });
            }
            queue.Dispose();
            prev.Dispose();
        }

        /// <summary>An internal shaft half left without its other half (the great shaft took it) becomes plain floor.</summary>
        static void FixShaftPairs(ref HoldGrid grid)
        {
            var up = new int3(0, 1, 0);
            for (var l = 0; l < grid.Size.y; l++)
            for (var z = 0; z < grid.Size.z; z++)
            for (var x = 0; x < grid.Size.x; x++)
            {
                var c = new int3(x, l, z);
                var cell = grid.Get(c);
                var orphan = cell.Kind == CellKind.ShaftDown && l > 0 && grid.Get(c - up).Kind != CellKind.ShaftUp
                             || cell.Kind == CellKind.ShaftUp && l < grid.Size.y - 1 && grid.Get(c + up).Kind != CellKind.ShaftDown;
                if (!orphan) continue;
                cell.Kind = CellKind.Mine;
                grid.Set(c, cell);
            }
        }

        // ------------------------------------------------------------------------------------------- stairs

        /// <summary>
        /// Per storey pair (stair_chance), up to stairs_max flights, each picked from every valid spot: the bottom
        /// approach a plain mine cell, the flight's cells, the cell past its end and the cell behind its start on both
        /// storeys solid rock with rock either side, and the top landing a plain mine cell on the upper storey. The
        /// flight only adds a way through untouched rock, so the region stays connected; then (replace_shaft_chance)
        /// one of that pair's internal shafts is filled in, unless that would split the region.
        /// </summary>
        static void Stairs(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (p.StairsMax <= 0 || p.StairChance <= 0f) return;
            var h = MineGenerator.Hash(seed, TagStairs, region.x, region.y, region.z, 0);
            var rng = new Random(h == 0 ? 0x68E31DA4u : h);
            var spots = new NativeList<int4>(256, Allocator.Temp);
            for (var l = 0; l + 1 < grid.Size.y; l++)
            {
                if (rng.NextFloat() >= p.StairChance) continue;
                var count = rng.NextInt(1, p.StairsMax + 1);
                for (var k = 0; k < count; k++)
                {
                    spots.Clear();
                    for (var z = 1; z < grid.Size.z - 1; z++)
                    for (var x = 1; x < grid.Size.x - 1; x++)
                    for (var dir = 0; dir < 4; dir++)
                    for (var len = p.FlightMin; len <= p.FlightMax; len++)
                        if (FlightFits(grid, new int3(x, l, z), dir, len)) spots.Add(new int4(x, z, dir, len));
                    if (spots.Length == 0) break;
                    var spot = spots[rng.NextInt(spots.Length)];
                    CarveFlight(ref grid, new int3(spot.x, l, spot.y), spot.z, spot.w, rng.NextFloat() < p.TimberChance, p.Palette);
                    if (rng.NextFloat() < p.ReplaceShaftChance) ReplaceShaft(ref grid, l, rng.NextInt(16));
                }
            }
            spots.Dispose();
        }

        static bool Plain(HoldCell c) => c.Kind == CellKind.Mine && (c.Flags & (CellFlags.Pillar | CellFlags.OpenAbove)) == 0;

        /// <summary>A flight of `len` cells up direction `dir` from bottom approach cell b (lower storey) fits.</summary>
        static bool FlightFits(in HoldGrid g, int3 b, int dir, int len)
        {
            if (!Plain(g.Get(b))) return false;
            var d = StairCell.Vec(dir);
            var side = new int3(d.z, 0, d.x);
            var up = new int3(0, 1, 0);
            var top = b + d * (len + 1) + up;
            if (!g.InBounds(top) || top.x < 1 || top.z < 1 || top.x > g.Size.x - 2 || top.z > g.Size.z - 2) return false;
            if (!Plain(g.Get(top))) return false;
            if (g.Get(b + up).Walkable || g.Get(top - up).Walkable) return false;
            for (var i = 1; i <= len; i++)
            {
                var c = b + d * i;
                if (c.x < 1 || c.z < 1 || c.x > g.Size.x - 2 || c.z > g.Size.z - 2) return false;
                for (var y = 0; y < 2; y++)
                {
                    var at = c + up * y;
                    if (g.Get(at).Walkable || g.Get(at + side).Walkable || g.Get(at - side).Walkable) return false;
                    // Not in a great shaft's ring (rock between its landings).
                    for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (g.Get(at + new int3(dx, 0, dz)).Kind == CellKind.GreatShaft) return false;
                }
            }
            return true;
        }

        static void CarveFlight(ref HoldGrid grid, int3 b, int dir, int len, bool timber, byte palette)
        {
            var d = StairCell.Vec(dir);
            var up = new int3(0, 1, 0);
            for (var i = 0; i < len; i++)
            {
                var c = b + d * (i + 1);
                grid.Set(c, new HoldCell
                {
                    Kind = CellKind.Stair, Palette = palette, Flags = CellFlags.OpenAbove,
                    Reserved = StairCell.Encode(dir, i, timber, false),
                });
                grid.Set(c + up, new HoldCell
                {
                    Kind = CellKind.Stair, Palette = palette, Reserved = StairCell.Encode(dir, i, timber, true),
                });
            }
            // A lantern hangs over the top of the flight (unless one is already next to it).
            var hang = b + d * len + up;
            var near = false;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
                near |= (grid.Get(hang + new int3(dx, 0, dz)).Flags & CellFlags.Lantern) != 0;
            if (near) return;
            var cell = grid.Get(hang);
            cell.Flags |= CellFlags.Lantern;
            grid.Set(hang, cell);
        }

        /// <summary>Fill in one internal shaft between storeys l and l + 1 (the pick-th, wrapping), if the region stays connected.</summary>
        static void ReplaceShaft(ref HoldGrid grid, int l, int pick)
        {
            var found = new FixedList128Bytes<int3>();
            var up = new int3(0, 1, 0);
            for (var z = 0; z < grid.Size.z; z++)
            for (var x = 0; x < grid.Size.x; x++)
            {
                var c = new int3(x, l, z);
                if (grid.Get(c).Kind == CellKind.ShaftUp && grid.Get(c + up).Kind == CellKind.ShaftDown && found.Length < found.Capacity)
                    found.Add(c);
            }
            if (found.Length == 0) return;
            var at = found[pick % found.Length];
            HoldCell lower = grid.Get(at), upper = grid.Get(at + up);
            var a = lower; a.Kind = CellKind.Mine;
            var b = upper; b.Kind = CellKind.Mine;
            grid.Set(at, a);
            grid.Set(at + up, b);
            if (Connected(grid)) return;
            grid.Set(at, lower);
            grid.Set(at + up, upper);
        }

        /// <summary>Every standable cell reachable from the first one (HoldNav, fixtures open).</summary>
        public static bool Connected(in HoldGrid grid)
        {
            var first = -1;
            var total = 0;
            for (var i = 0; i < grid.Cells.Length; i++)
                if (HoldNav.Standable(grid.Cells[i])) { total++; if (first < 0) first = i; }
            if (first < 0) return true;
            var sx = grid.Size.x;
            var start = new int3(first % sx, first / (sx * grid.Size.z), first / sx % grid.Size.z);
            var seen = new NativeArray<bool>(grid.Cells.Length, Allocator.Temp);
            var reached = HoldNav.Flood(grid, start, false, seen);
            seen.Dispose();
            return reached == total;
        }
    }
}
