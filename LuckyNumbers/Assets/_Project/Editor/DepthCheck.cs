using System.Collections.Generic;
using System.Diagnostics;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Headless check of the Depth pass (MinePasses.Depth.cs) over columns of regions (x, z in -key_area..key_area)
    /// through three bands, for several seeds: drop shafts agree across each region pair (lip above, no-ladder shaft
    /// and landing below, landing joined up), collapses and seals never cut a region (every standable cell reached
    /// from the west seam with seals shut), every way down at a band's bottom is sealed and nothing else is, each band
    /// has a seal key reachable inside its region and from the band's top at x, z 0 through the search area, and a
    /// second generation is identical. Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.DepthCheck.Run
    /// </summary>
    public static class DepthCheck
    {
        static readonly uint[] Seeds = { 1234, 1, 7, 99, 31337 };
        const int Bands = 3;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Depth Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 60) Debug.LogError($"[DepthCheck] FAIL {why}"); }
            if (p.SealEvery <= 0) Fail("seal_every is 0: no bands to check");
            var great = config.Vertical?.GreatBand ?? 0;
            if (great > 0 && great != p.SealEvery) Fail($"seal_every {p.SealEvery} != vertical great_band {great}: a great shaft could bypass a seal");
            var a = p.KeyArea;
            var depths = Bands * math.max(1, p.SealEvery);
            int drops = 0, rubble = 0, seals = 0, keys = 0, regions = 0;
            var watch = Stopwatch.StartNew();

            foreach (var seed in Seeds)
            {
                var grids = new Dictionary<int3, HoldGrid>();
                for (var d = 0; d < depths; d++)
                for (var z = -a; z <= a; z++)
                for (var x = -a; x <= a; x++)
                    grids[new int3(x, -d, z)] = MineGenerator.Region(seed, new int3(x, -d, z), p, Allocator.Persistent);
                regions += grids.Count;
                var seen = new NativeArray<bool>(p.Cells * p.Cells * p.Levels, Allocator.Persistent);

                foreach (var pair in grids)
                {
                    var r = pair.Key;
                    var g = pair.Value;
                    var name = $"seed {seed} region {r}";

                    // Connectivity: everything standable reached from the west seam with the seals shut.
                    int standable = 0, sealCells = 0;
                    for (var i = 0; i < g.Cells.Length; i++)
                    {
                        if (HoldNav.Standable(g.Cells[i])) standable++;
                        if (g.Cells[i].Kind == CellKind.Rubble) rubble++;
                    }
                    foreach (var f in g.Fixtures) if (f.Kind == FixtureKind.Seal && HoldNav.Standable(g.Get(f.Cell))) sealCells++;
                    var reached = MinePasses.FloodOpen(g, West(g), seen);
                    if (reached != standable - sealCells) Fail($"{name}: {reached}/{standable - sealCells} cells reachable with seals shut");

                    // Seals: exactly the band bottom's ways down.
                    var isSealed = MinePasses.SealedBelow(r, p);
                    for (var z = 0; z < g.Size.z; z++)
                    for (var x = 0; x < g.Size.x; x++)
                    {
                        var c = new int3(x, 0, z);
                        var cell = g.Get(c);
                        var way = cell.Kind == CellKind.ShaftDown || cell.Kind == CellKind.GreatShaft && !GreatShaftCell.Floor(cell);
                        var has = HasFixture(g, c, FixtureKind.Seal);
                        if (isSealed && way && !has) Fail($"{name}: way down at {c} not sealed");
                        if (!isSealed && has) Fail($"{name}: seal at {c} off a band bottom");
                        if (isSealed && cell.Kind == CellKind.ShaftDown && !DepthCells.IsSealed(cell)) Fail($"{name}: sealed shaft {c} without its bit");
                        if (has) seals++;
                    }
                    foreach (var f in g.Fixtures)
                        if (f.Kind == FixtureKind.Seal && f.Cell.y != 0) Fail($"{name}: seal at {f.Cell} not on level 0");

                    // Drops: this region as the upper half, the one below (if generated) as the lower.
                    var below = r - new int3(0, 1, 0);
                    var isDrop = MinePasses.DropBelow(seed, r, p, out var col);
                    if (isDrop && isSealed) Fail($"{name}: a drop out of a sealed depth");
                    var lip = new int3(col.x, 0, col.y);
                    if (isDrop)
                    {
                        if (!DepthCells.IsDropLip(g.Get(lip)) || !HasFixture(g, lip, FixtureKind.DropLip)) Fail($"{name}: drop lip at {lip} missing");
                        if (grids.TryGetValue(below, out var lower))
                        {
                            drops++;
                            var top = new int3(col.x, lower.Size.y - 1, col.y);
                            var land = top - new int3(0, 1, 0);
                            var tc = lower.Get(top); var lc = lower.Get(land);
                            if (!DepthCells.NoLadder(tc) || !DepthCells.NoLadder(lc)) Fail($"{name}: drop into {below} at {col}: top {tc.Kind}/{tc.Reserved:x} land {lc.Kind}/{lc.Reserved:x}");
                            if (math.any(HoldRules.LadderSide(lower, top) != 0) || math.any(HoldRules.LadderSide(lower, land) != 0)) Fail($"{name}: drop at {col} still has a ladder");
                            if (HoldRules.HasFloor(lower, top) || !HoldRules.HasFloor(lower, land)) Fail($"{name}: drop at {col}: floors wrong (top {HoldRules.HasFloor(lower, top)}, land {HoldRules.HasFloor(lower, land)})");
                            MinePasses.FloodOpen(lower, West(lower), seen);
                            if (!seen[lower.Index(land)]) Fail($"{name}: drop landing {land} in {below} not joined up");
                        }
                    }
                    else
                    {
                        foreach (var f in g.Fixtures) if (f.Kind == FixtureKind.DropLip) Fail($"{name}: drop lip at {f.Cell} without a drop");
                        if (grids.TryGetValue(below, out var lower))
                            for (var i = 0; i < lower.Cells.Length; i++)
                                if (DepthCells.NoLadder(lower.Cells[i])) { Fail($"{below}: drop cell with no drop above"); break; }
                    }
                }

                // Keys: per band, in a key region, reachable in it with doors shut, and the region reachable from the
                // band's top at (0, 0) through the area.
                for (var band = 0; band < Bands; band++)
                {
                    var found = 0;
                    var reachable = RegionsReachable(seed, band, p, grids);
                    var any = false;
                    foreach (var pair in grids)
                    {
                        var r = pair.Key;
                        var g = pair.Value;
                        var holds = MinePasses.HoldsKey(seed, r, p) && MinePasses.BandOf(MinePasses.DepthOf(r), p) == band;
                        foreach (var f in g.Fixtures)
                        {
                            if (f.Kind != FixtureKind.SealKey) continue;
                            if (f.Data != MinePasses.BandOf(MinePasses.DepthOf(r), p)) Fail($"seed {seed} {r}: key for band {f.Data}");
                            if (MinePasses.BandOf(MinePasses.DepthOf(r), p) != band) continue;
                            if (!holds) Fail($"seed {seed} {r}: key in a region not picked for it");
                            keys++;
                            HoldNav.Flood(g, West(g), true, seen);
                            if (!seen[g.Index(f.Cell)]) { Fail($"seed {seed} {r}: key at {f.Cell} unreachable with doors shut"); continue; }
                            found++;
                            if (reachable.Contains(r)) any = true;
                        }
                    }
                    if (found == 0) Fail($"seed {seed} band {band}: no seal key in the area");
                    if (!any) Fail($"seed {seed} band {band}: no seal key reachable from the band's top at 0,0 ({found} in the area, {reachable.Count} regions reached)");
                }

                // Determinism: a few regions again, cells (with Reserved) and fixtures identical.
                var k = 0;
                foreach (var pair in grids)
                {
                    if (k++ % 7 != 0) continue;
                    var again = MineGenerator.Region(seed, pair.Key, p, Allocator.Temp);
                    var g = pair.Value;
                    for (var i = 0; i < g.Cells.Length; i++)
                    {
                        HoldCell u = g.Cells[i], v = again.Cells[i];
                        if (u.Kind != v.Kind || u.Flags != v.Flags || u.Reserved != v.Reserved || u.Palette != v.Palette) { Fail($"seed {seed} {pair.Key}: cell {i} differs"); break; }
                    }
                    if (again.Fixtures.Length != g.Fixtures.Length) Fail($"seed {seed} {pair.Key}: fixtures differ");
                    else
                        for (var i = 0; i < g.Fixtures.Length; i++)
                        {
                            HoldFixture u = g.Fixtures[i], v = again.Fixtures[i];
                            if (u.Kind != v.Kind || !u.Cell.Equals(v.Cell) || u.Data != v.Data || u.Link != v.Link) { Fail($"seed {seed} {pair.Key}: fixture {i} differs"); break; }
                        }
                    again.Dispose();
                }

                seen.Dispose();
                foreach (var g in grids.Values) g.Dispose();
            }

            Debug.Log($"[DepthCheck] {Seeds.Length} seeds x {regions / Seeds.Length} regions (x, z -{a}..{a}, depths 0..{depths - 1}): " +
                      $"{drops} drop shafts, {rubble} rubble cells, {seals} seals, {keys} keys ({watch.Elapsed.TotalSeconds:F1} s)");
            Debug.Log(failures == 0 ? "[DepthCheck] PASS drops, collapses, seals, keys, determinism" : $"[DepthCheck] {failures} failure(s)");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static int3 West(in HoldGrid g)
        {
            for (var z = 0; z < g.Size.z; z++)
                if (HoldNav.Standable(g.Get(new int3(0, 0, z)))) return new int3(0, 0, z);
            return int3.zero;
        }

        static bool HasFixture(in HoldGrid g, int3 c, FixtureKind kind)
        {
            foreach (var f in g.Fixtures) if (f.Kind == kind && f.Cell.Equals(c)) return true;
            return false;
        }

        /// <summary>
        /// Regions of a band reachable from its top depth at x, z 0, inside the generated area: across seams that are
        /// open on both sides, down shafts (not sealed: inside a band), up shafts that aren't drops. Each region is
        /// one connected space (checked above), so region-level reach is enough.
        /// </summary>
        static HashSet<int3> RegionsReachable(uint seed, int band, in MineParams p, Dictionary<int3, HoldGrid> grids)
        {
            var top = -MinePasses.BandTop(band, p);
            var bottom = -MinePasses.BandBottom(band, p);
            var seen = new HashSet<int3>();
            var queue = new Queue<int3>();
            var start = new int3(0, top, 0);
            if (!grids.ContainsKey(start)) return seen;
            seen.Add(start);
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var r = queue.Dequeue();
                var g = grids[r];
                void Try(int3 to)
                {
                    if (to.y > top || to.y < bottom || seen.Contains(to) || !grids.ContainsKey(to)) return;
                    seen.Add(to);
                    queue.Enqueue(to);
                }
                var n = g.Size.x;
                for (var axis = 0; axis < 2; axis++)
                for (var s = -1; s <= 1; s += 2)
                {
                    var o = axis == 0 ? new int3(s, 0, 0) : new int3(0, 0, s);
                    if (!grids.TryGetValue(r + o, out var other)) continue;
                    var open = false;
                    for (var l = 0; l < g.Size.y && !open; l++)
                    for (var t = 0; t < n && !open; t++)
                    {
                        int3 mine = axis == 0 ? new int3(s > 0 ? n - 1 : 0, l, t) : new int3(t, l, s > 0 ? n - 1 : 0);
                        int3 theirs = axis == 0 ? new int3(s > 0 ? 0 : n - 1, l, t) : new int3(t, l, s > 0 ? 0 : n - 1);
                        open = HoldNav.Standable(g.Get(mine)) && HoldNav.Standable(other.Get(theirs));
                    }
                    if (open) Try(r + o);
                }
                if (MineGenerator.ShaftBelow(seed, r, p, out _) && !MinePasses.SealedBelow(r, p)) Try(r - new int3(0, 1, 0));
                var up = r + new int3(0, 1, 0);
                if (MineGenerator.ShaftBelow(seed, up, p, out _) && !MinePasses.DropBelow(seed, up, p, out _)) Try(up);
            }
            return seen;
        }
    }
}
