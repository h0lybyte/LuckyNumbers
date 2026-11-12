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
    /// Headless check of the Vertical pass (MinePasses.Vertical.cs) over several seeds: blocks of regions for stairs
    /// (flights well-formed, the top step and its landing joined in HoldNav, treads under the step offset) and every
    /// great shaft found near the top for its whole length (the same cells in every region it passes, nothing past its
    /// ends, floor/ceiling bits only at bottom/top, one Hoist fixture per region with the right data, each landing
    /// joined to that region's mines, region-to-region shafts intact); every region connected (HoldNav.Flood),
    /// determinism (cells and fixtures), and a mesh build of a region with each.
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.VerticalCheck.Run
    /// </summary>
    public static class VerticalCheck
    {
        static readonly uint[] Seeds = { 1234, 7, 42, 9001, 31337 };
        const float StepOffset = 0.4f;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Vertical Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            int n = p.Cells, levels = p.Levels;
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 60) Debug.LogError($"[VerticalCheck] FAIL {why}"); }
            int regions = 0, flights = 0, timber = 0, shafts = 0, shaftRegions = 0, landings = 0, noShaftPairs = 0;
            var watch = Stopwatch.StartNew();
            var palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            bool meshedStairs = false, meshedShaft = false;
            // The same dig without the vertical pass (its rules aren't read by the digger), to tell old mines from new.
            var plain = p;
            plain.GreatChance = 0f;
            plain.StairChance = 0f;

            foreach (var seed in Seeds)
            {
                // ---- a block of regions: stairs, connectivity, determinism ----
                for (var ry = 0; ry >= -2; ry--)
                for (var rz = -1; rz <= 1; rz++)
                for (var rx = -1; rx <= 1; rx++)
                {
                    var region = new int3(rx, ry, rz);
                    var g = MineGenerator.Region(seed, region, p, Allocator.Temp);
                    regions++;
                    var name = $"seed {seed} region {region}";
                    if (!MinePasses.Connected(g)) Fail($"{name}: not connected");
                    var found = CheckStairs(g, name, Fail, ref timber);
                    flights += found;
                    if (found > 0)
                    {
                        var pairs = 0;
                        for (var i = 0; i < g.Cells.Length; i++) if (g.Cells[i].Kind == CellKind.ShaftUp && i / (n * n) < levels - 1) pairs++;
                        if (pairs == 0) noShaftPairs++;
                        if (!meshedStairs) { meshedStairs = true; Mesh(g, palettes, config, name, Fail, true); }
                    }
                    var again = MineGenerator.Region(seed, region, p, Allocator.Temp);
                    if (!Same(g, again)) Fail($"{name}: not deterministic");
                    again.Dispose();
                    g.Dispose();
                }

                // ---- great shafts: every one in groups round the top, bands 0 and 1, along its whole length ----
                for (var band = 0; band <= 1; band++)
                for (var gz = -3; gz <= 3; gz++)
                for (var gx = -3; gx <= 3; gx++)
                {
                    if (!MinePasses.GreatShaftOf(seed, new int2(gx, gz), band, p, out var s)) continue;
                    shafts++;
                    var name = $"seed {seed} shaft g({gx},{gz}) band {band} col {s.Region} min {s.Min}";
                    if (s.TopRegion > -band * p.GreatBand || s.BottomRegion <= -(band + 1) * p.GreatBand) Fail($"{name}: outside its band");
                    if (math.any(s.Region < new int2(gx, gz) * p.GreatSpacing) || math.any(s.Region >= new int2(gx + 1, gz + 1) * p.GreatSpacing))
                        Fail($"{name}: column outside its group");
                    for (var ry = s.TopRegion + 1; ry >= s.BottomRegion - 1; ry--)
                    {
                        var region = new int3(s.Region.x, ry, s.Region.y);
                        if (ry > 0) continue;
                        var inside = ry <= s.TopRegion && ry >= s.BottomRegion;
                        var at = MinePasses.GreatShaftAt(seed, region, p, out var here);
                        if (!inside && at) continue;   // another band's shaft in the same column
                        if (at != inside || (inside && !here.Min.Equals(s.Min))) Fail($"{name}: region {ry} GreatShaftAt {at}, expected {inside}");
                        var g = MineGenerator.Region(seed, region, p, Allocator.Temp);
                        var dig = MineGenerator.Region(seed, region, plain, Allocator.Temp);
                        CheckShaftRegion(g, dig, s, region, p, inside, $"{name} region y {ry}", Fail, ref landings);
                        dig.Dispose();
                        if (inside)
                        {
                            shaftRegions++;
                            if (!MinePasses.Connected(g)) Fail($"{name} region y {ry}: not connected");
                            if (!meshedShaft) { meshedShaft = true; Mesh(g, palettes, config, name, Fail, false); }
                        }
                        // Region-to-region shafts still meet across the seam under this region.
                        var below = MineGenerator.Region(seed, region - new int3(0, 1, 0), p, Allocator.Temp);
                        for (var z = 0; z < n; z++)
                        for (var x = 0; x < n; x++)
                        {
                            var down = g.Get(new int3(x, 0, z)).Kind == CellKind.ShaftDown;
                            var up = below.Get(new int3(x, levels - 1, z)).Kind == CellKind.ShaftUp;
                            if (down != up) Fail($"{name} region y {ry}: seam shaft at {x},{z} down {down} up {up}");
                        }
                        below.Dispose();
                        g.Dispose();
                    }
                }
            }
            palettes.Dispose();
            var ms = watch.Elapsed.TotalMilliseconds;

            if (flights == 0) Fail("no stairs anywhere");
            if (shafts == 0) Fail("no great shafts anywhere");
            if (!meshedStairs || !meshedShaft) Fail("nothing meshed");
            Debug.Log($"[VerticalCheck] {Seeds.Length} seeds: {regions} block regions, {flights} flights ({timber} timber, " +
                      $"{noShaftPairs} regions with stairs and no internal shaft left), {shafts} great shafts through " +
                      $"{shaftRegions} regions, {landings} landings; {ms:F0} ms");
            Debug.Log(failures == 0 ? "[VerticalCheck] PASS stairs, great shafts, landings, hoists, connectivity, determinism" : $"[VerticalCheck] {failures} failure(s)");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>Every flight: indices run 0..len-1 up its direction, wells over every step, rock round it, the
        /// approach and landing standable, HoldNav joins the top step and the landing both ways, treads under the step offset.</summary>
        static int CheckStairs(HoldGrid g, string name, System.Action<string> fail, ref int timber)
        {
            var count = 0;
            var up = new int3(0, 1, 0);
            var storey = GameContent.Hold.StoreyHeight > 0 ? GameContent.Hold.StoreyHeight : 3f;
            for (var i = 0; i < g.Cells.Length; i++)
            {
                var cell = g.Cells[i];
                if (!StairCell.IsStep(cell) || StairCell.Index(cell) != 0) continue;
                count++;
                if (StairCell.Timber(cell)) timber++;
                var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                var d = StairCell.Vec(StairCell.Dir(cell));
                var side = new int3(d.z, 0, d.x);
                var len = StairCell.Length(g, c, cell);
                var tag = $"{name}: flight at {c} dir {d} len {len}";
                if (len < StairCell.MinLength || len > StairCell.MaxLength) fail($"{tag}: bad length");
                if (storey / (StairCell.TreadsPerCell * len) > StepOffset) fail($"{tag}: risers over the step offset");
                if (!HoldNav.Standable(g.Get(c - d)) || g.Get(c - d).Kind == CellKind.Stair) fail($"{tag}: no approach");
                for (var k = 0; k < len; k++)
                {
                    var s = c + d * k;
                    var step = g.Get(s);
                    var well = g.Get(s + up);
                    if (!StairCell.IsStep(step) || StairCell.Index(step) != k || StairCell.Dir(step) != StairCell.Dir(cell) ||
                        (step.Flags & CellFlags.OpenAbove) == 0) fail($"{tag}: step {k} wrong");
                    if (!StairCell.IsWell(well) || StairCell.Index(well) != k) fail($"{tag}: well {k} wrong");
                    for (var y = 0; y < 2; y++)
                        if (g.Get(s + up * y + side).Walkable || g.Get(s + up * y - side).Walkable) fail($"{tag}: open beside {k}");
                    if (!HoldRules.HasFloor(g, s) || HoldRules.HasCeiling(g, s) || HoldRules.HasFloor(g, s + up)) fail($"{tag}: floor/ceiling rule at {k}");
                }
                var topStep = c + d * (len - 1);
                var landing = topStep + d + up;
                if (!HoldNav.Standable(g.Get(landing)) || g.Get(landing).Kind == CellKind.Stair) fail($"{tag}: no landing");
                if (g.Get(topStep + d).Walkable) fail($"{tag}: open under the landing");
                if (!Moves(g, topStep, landing) || !Moves(g, landing, topStep)) fail($"{tag}: HoldNav doesn't join the top step and its landing");
                if (!Moves(g, c - d, c)) fail($"{tag}: HoldNav doesn't join the approach");
            }
            return count;
        }

        static bool Moves(HoldGrid g, int3 from, int3 to)
        {
            var moves = new FixedList128Bytes<int3>();
            HoldNav.Moves(g, from, ref moves);
            foreach (var m in moves) if (m.Equals(to)) return true;
            return false;
        }

        static void CheckShaftRegion(HoldGrid g, HoldGrid dig, in GreatShaft s, int3 region, in MineParams p, bool inside, string name,
                                     System.Action<string> fail, ref int landings)
        {
            var levels = p.Levels;
            var hoists = 0;
            foreach (var f in g.Fixtures)
            {
                if (f.Kind != FixtureKind.Hoist) continue;
                hoists++;
                if (!f.Cell.Equals(new int3(s.Min.x, levels - 1, s.Min.y)) || f.Dir != s.Dock) fail($"{name}: hoist fixture at {f.Cell} dir {f.Dir}");
                if ((f.Data & 0xFFFF) != region.y * levels + levels - 1 - s.BottomLevel(p)) fail($"{name}: hoist storeys {f.Data & 0xFFFF}");
            }
            if (hoists != (inside ? 1 : 0)) fail($"{name}: {hoists} hoist fixtures");

            var mask = 0;
            foreach (var f in g.Fixtures) if (f.Kind == FixtureKind.Hoist) mask = f.Data >> 16;
            for (var l = 0; l < levels; l++)
            {
                var world = region.y * levels + l;
                for (var iz = 0; iz < s.Size; iz++)
                for (var ix = 0; ix < s.Size; ix++)
                {
                    var c = new int3(s.Min.x + ix, l, s.Min.y + iz);
                    var cell = g.Get(c);
                    if (!inside)
                    {
                        if (cell.Kind == CellKind.GreatShaft) fail($"{name}: great shaft cell past the shaft's end at {c}");
                        continue;
                    }
                    var floor = world == s.BottomLevel(p);
                    var ceiling = world == s.TopLevel(p);
                    if (cell.Kind != CellKind.GreatShaft || cell.Reserved != GreatShaftCell.Encode(floor, ceiling, s.Dock, ix, iz))
                        fail($"{name}: cell {c} is {cell.Kind} reserved {cell.Reserved}");
                    if (HoldRules.HasFloor(g, c) != floor || HoldRules.HasCeiling(g, c) != ceiling) fail($"{name}: floor/ceiling at {c}");
                }
                if (!inside) continue;
                var landing = (mask & (1 << l)) != 0;
                if (s.PlannedLanding(world, p) && !landing) fail($"{name}: planned landing {world} missing");
                // Ring: walkable all round on a landing, rock all round otherwise.
                for (var z = s.Min.y - 1; z <= s.Min.y + s.Size; z++)
                for (var x = s.Min.x - 1; x <= s.Min.x + s.Size; x++)
                {
                    var inFoot = x >= s.Min.x && z >= s.Min.y && x < s.Min.x + s.Size && z < s.Min.y + s.Size;
                    if (inFoot) continue;
                    var w = g.Get(new int3(x, l, z));
                    if (landing ? !HoldNav.Standable(w) : w.Walkable) fail($"{name}: ring cell {x},{l},{z} {(landing ? "blocked" : "open")} ");
                }
                if (!landing) continue;
                landings++;
                // The landing joins this storey's mines without the shaft: with the shaft filled in, a flood from the dock
                // reaches a cell the dig itself made (outside the ring).
                var dock = s.DockRing();
                if (!g.Get(new int3(dock.x, l, dock.y) + StairCell.Vec(s.Dock)).Walkable) fail($"{name}: no tunnel out of the dock at level {l}");
                var solid = new HoldGrid(g.Size, g.Origin, Allocator.Temp);
                for (var i = 0; i < g.Cells.Length; i++) solid.Cells[i] = g.Cells[i].Kind == CellKind.GreatShaft ? HoldCell.Solid : g.Cells[i];
                var seen = new NativeArray<bool>(g.Cells.Length, Allocator.Temp);
                HoldNav.Flood(solid, new int3(dock.x, l, dock.y), false, seen);
                var joined = false;
                for (var i = 0; i < g.Cells.Length && !joined; i++)
                {
                    if (!seen[i] || !dig.Cells[i].Walkable) continue;
                    int x = i % g.Size.x, z = i / g.Size.x % g.Size.z;
                    joined = x < s.Min.x - 1 || z < s.Min.y - 1 || x > s.Min.x + s.Size || z > s.Min.y + s.Size;
                }
                seen.Dispose();
                solid.Dispose();
                if (!joined) fail($"{name}: landing at level {l} not joined to the mines");
            }
        }

        static bool Same(HoldGrid a, HoldGrid b)
        {
            if (!a.Size.Equals(b.Size) || !a.Origin.Equals(b.Origin) || a.Fixtures.Length != b.Fixtures.Length) return false;
            for (var i = 0; i < a.Cells.Length; i++)
            {
                HoldCell u = a.Cells[i], v = b.Cells[i];
                if (u.Kind != v.Kind || u.Palette != v.Palette || u.Flags != v.Flags || u.Reserved != v.Reserved) return false;
            }
            for (var i = 0; i < a.Fixtures.Length; i++)
            {
                HoldFixture u = a.Fixtures[i], v = b.Fixtures[i];
                if (u.Kind != v.Kind || !u.Cell.Equals(v.Cell) || u.Dir != v.Dir || u.Link != v.Link || u.Data != v.Data) return false;
            }
            return true;
        }

        /// <summary>Mesh a region: it builds, and (stairs) a collider box per tread exists.</summary>
        static void Mesh(HoldGrid g, NativeArray<PaletteLayers> palettes, HoldConfig config, string name, System.Action<string> fail, bool stairs)
        {
            var copy = new HoldGrid(g.Size, g.Origin, Allocator.Persistent);   // jobs can't take Temp memory
            copy.Cells.CopyFrom(g.Cells);
            var chunks = HoldMesher.Build(copy, palettes, config);
            copy.Dispose();
            var tris = 0;
            var boxes = new List<Bounds>();
            foreach (var c in chunks)
            {
                tris += (int)c.Mesh.GetIndexCount(0) / 3;
                boxes.AddRange(c.Colliders);
                Object.DestroyImmediate(c.Mesh);
            }
            if (tris == 0) fail($"{name}: empty mesh");
            if (stairs)
            {
                // The first flight's first tread: a box from its floor up one riser.
                for (var i = 0; i < g.Cells.Length; i++)
                {
                    var cell = g.Cells[i];
                    if (!StairCell.IsStep(cell) || StairCell.Index(cell) != 0) continue;
                    var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                    var len = StairCell.Length(g, c, cell);
                    var floor = (float)(g.Origin.y + c.y) * config.StoreyHeight;
                    var rise = config.StoreyHeight / (StairCell.TreadsPerCell * len);
                    var ok = false;
                    foreach (var b in boxes) ok |= math.abs(b.min.y - floor) < 0.01f && math.abs(b.max.y - floor - rise) < 0.01f;
                    if (!ok) fail($"{name}: no collider for the first tread at {c}");
                    break;
                }
            }
            Debug.Log($"[VerticalCheck] meshed {name}: {chunks.Length} chunks, {tris} triangles, {boxes.Count} collider boxes");
        }
    }
}
