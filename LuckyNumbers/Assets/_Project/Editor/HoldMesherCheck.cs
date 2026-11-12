using System;
using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Headless self-test for HoldMesher: a small hand-made grid (room with pillar, lantern and a two-storey corner,
    /// door, corridor out to a gate, a shaft up to a room on the level above, shafts out of the grid's top and bottom),
    /// meshed, then every face and ladder checked.
    /// Batchmode: -executeMethod LuckyNumbers.Editor.HoldMesherCheck.Run (throws on failure, so Unity exits non-zero).
    /// </summary>
    public static class HoldMesherCheck
    {
        const string Tag = "[HoldMesherCheck]";
        // Test palettes: distinct slices per surface; palette 1 has no ceiling layer (-1, meshed as slice 0).
        static readonly PaletteLayers[] Palettes =
        {
            new() { Floor = 0, Wall = 1, Ceiling = 2, Trim = 3, Pillar = 4 },
            new() { Floor = 0, Wall = 1, Ceiling = -1, Trim = 3, Pillar = 4 },
        };

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Mesher Self-Test")]
        public static void Run()
        {
            var config = new HoldConfig
            {
                CellSize = 2, StoreyHeight = 3, ChunkCells = 8, LanternRadius = 9,
                LanternR = 1, LanternG = 0.72f, LanternB = 0.42f, Ambient = 0.12f,
            };
            var grid = TestGrid();
            var palettes = new NativeArray<PaletteLayers>(Palettes, Allocator.Persistent);
            var failures = new List<string>();
            HoldChunk[] chunks = null;
            try
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                chunks = HoldMesher.Build(grid, palettes, config);
                var ms = (Time.realtimeSinceStartupAsDouble - t0) * 1000.0;
                Debug.Log($"{Tag} grid {grid.Size} origin {grid.Origin}: {chunks.Length} non-empty chunk(s) of " +
                          $"{HoldMesher.ChunkCount(grid.Size, config.ChunkCells)} in {ms:0.0} ms");
                Check(grid, config, chunks, failures);
            }
            finally
            {
                if (chunks != null) foreach (var c in chunks) Object.DestroyImmediate(c.Mesh);
                palettes.Dispose();
                grid.Dispose();
            }
            foreach (var f in failures) Debug.LogError($"{Tag} FAIL {f}");
            if (failures.Count > 0) throw new Exception($"{Tag} {failures.Count} failure(s)");
            Debug.Log($"{Tag} PASS");
        }

        static HoldGrid TestGrid()
        {
            var g = new HoldGrid(new int3(20, 2, 12), new int3(-4, -1, 3), Allocator.Persistent);
            void Fill(int3 a, int3 b, CellKind kind, byte palette = 0)
            {
                for (var y = a.y; y <= b.y; y++)
                for (var z = a.z; z <= b.z; z++)
                for (var x = a.x; x <= b.x; x++)
                    g.Set(new int3(x, y, z), new HoldCell { Kind = kind, Palette = palette });
            }
            void Flag(int3 c, CellFlags f) { var cell = g.Get(c); cell.Flags |= f; g.Set(c, cell); }

            Fill(new int3(1, 0, 1), new int3(6, 0, 6), CellKind.Floor);          // room
            Flag(new int3(3, 0, 3), CellFlags.Pillar);
            Flag(new int3(5, 0, 2), CellFlags.Lantern);
            Flag(new int3(2, 0, 5), CellFlags.OpenAbove);                         // two-storey corner
            Fill(new int3(2, 1, 5), new int3(2, 1, 5), CellKind.Floor);
            Fill(new int3(7, 0, 3), new int3(7, 0, 3), CellKind.Door);
            Fill(new int3(8, 0, 3), new int3(18, 0, 3), CellKind.Corridor);
            Fill(new int3(19, 0, 3), new int3(19, 0, 3), CellKind.Gate);         // the way out (east edge)
            Fill(new int3(12, 0, 3), new int3(12, 0, 3), CellKind.ShaftUp);
            Fill(new int3(10, 1, 3), new int3(13, 1, 6), CellKind.Floor, 1);     // upper room, no ceiling layer
            Fill(new int3(12, 1, 3), new int3(12, 1, 3), CellKind.ShaftDown, 1);  // on its south wall: ladder there
            Flag(new int3(11, 1, 4), CellFlags.Lantern);
            // Shafts out of the grid: a ShaftUp on the top level (the region above has its ShaftDown) and a
            // ShaftDown on the bottom level (the region below has its ShaftUp).
            Fill(new int3(15, 1, 8), new int3(17, 1, 8), CellKind.Mine);
            Fill(new int3(16, 1, 8), new int3(16, 1, 8), CellKind.ShaftUp);
            Fill(new int3(8, 0, 8), new int3(10, 0, 8), CellKind.Mine);
            Fill(new int3(9, 0, 8), new int3(9, 0, 8), CellKind.ShaftDown);
            return g;
        }

        static void Check(in HoldGrid g, HoldConfig config, HoldChunk[] chunks, List<string> failures)
        {
            var scale = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            var expectedWalls = 0;
            for (var y = 0; y < g.Size.y; y++)
            for (var z = 0; z < g.Size.z; z++)
            for (var x = 0; x < g.Size.x; x++)
            {
                var c = new int3(x, y, z);
                if (!g.Get(c).Walkable) continue;
                for (var i = 0; i < 4; i++)
                    if (HoldRules.HasWall(g, c, Dir(i))) expectedWalls++;
            }

            int walls = 0, floors = 0, ceilings = 0, kit = 0, totalV = 0, totalT = 0;
            float darkest = float.MaxValue, brightest = 0f, lanternFloor = 0f, farFloor = -1f;
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector3>();
            var colors = new List<Color32>();
            var seen = new HashSet<(int3, int3)>();
            var all = new List<float3>();
            foreach (var chunk in chunks)
            {
                var m = chunk.Mesh;
                m.GetVertices(verts);
                foreach (var v in verts) all.Add(v);
                m.GetNormals(normals);
                m.GetUVs(0, uvs);
                m.GetColors(colors);
                var tris = m.triangles;
                totalV += verts.Count;
                totalT += tris.Length / 3;
                Debug.Log($"{Tag} chunk {chunk.Index.x},{chunk.Index.y}: {verts.Count} vertices, {tris.Length / 3} triangles, " +
                          $"{chunk.Colliders.Length} collider boxes, bounds {m.bounds.min}..{m.bounds.max}");
                if (verts.Count % 4 != 0 || tris.Length != verts.Count / 4 * 6) failures.Add($"chunk {chunk.Index}: not whole quads");

                for (var q = 0; q < verts.Count / 4; q++)
                {
                    var b = q * 4;
                    var n = (float3)normals[b];
                    var centre = (float3)(verts[b] + verts[b + 1] + verts[b + 2] + verts[b + 3]) * 0.25f;
                    // Winding agrees with the normal (Unity front faces: clockwise, cross(b-a, c-a) along n).
                    var t = q * 6;
                    var p0 = (float3)verts[tris[t]];
                    var geo = math.cross((float3)verts[tris[t + 1]] - p0, (float3)verts[tris[t + 2]] - p0);
                    if (math.dot(geo, n) <= 0f) failures.Add($"quad at {centre} winds against its normal {n}");
                    for (var k = 0; k < 4; k++)
                    {
                        var lum = colors[b + k].r / 255f;
                        darkest = math.min(darkest, lum);
                        brightest = math.max(brightest, lum);
                    }

                    var local = centre / scale - g.Origin;
                    var axis = math.abs(n.x) > 0.5f ? 0 : math.abs(n.y) > 0.5f ? 1 : 2;
                    var onBoundary = math.abs(local[axis] - math.round(local[axis])) < 1e-3f;
                    if (!onBoundary) { kit++; continue; }

                    var front = (int3)math.floor(local + n * 0.01f);
                    var back = (int3)math.floor(local - n * 0.01f);
                    var fc = g.Get(front);
                    var bc = g.Get(back);
                    if (!fc.Walkable) { failures.Add($"face at {centre} n {n} faces Solid cell {front}"); continue; }
                    if (!seen.Add((front, (int3)math.round(-n)))) failures.Add($"duplicate face on cell {front} side {-n}");
                    var pal = Palettes[fc.Palette];
                    var slice = (int)math.round(uvs[b].z);
                    float2 uMin = float.MaxValue, uMax = float.MinValue;
                    for (var k = 0; k < 4; k++)
                    {
                        uMin = math.min(uMin, ((float3)uvs[b + k]).xy);
                        uMax = math.max(uMax, ((float3)uvs[b + k]).xy);
                    }
                    var span = uMax - uMin;
                    if (axis != 1)
                    {
                        walls++;
                        if (bc.Walkable) failures.Add($"wall at {centre} between walkable {front} and {back}");
                        if (slice != pal.Wall) failures.Add($"wall at {centre} slice {slice}, expected {pal.Wall}");
                        if (math.any(math.abs(span - new float2(1f, config.StoreyHeight / config.CellSize)) > 1e-3f))
                            failures.Add($"wall at {centre} uv span {span}, expected (1, {config.StoreyHeight / config.CellSize})");
                        var vBottom = (g.Origin.y + front.y) * config.StoreyHeight / config.CellSize;
                        if (math.abs(uMin.y - vBottom) > 1e-3f) failures.Add($"wall at {centre} v starts {uMin.y}, expected {vBottom} (world-aligned)");
                    }
                    else
                    {
                        if (HoldRules.Linked(n.y > 0 ? bc : fc, n.y > 0 ? fc : bc)) failures.Add($"slab at {centre} inside one linked space");
                        if (math.any(math.abs(span - 1f) > 1e-3f)) failures.Add($"slab at {centre} uv span {span}");
                        var expected = math.max(0, n.y > 0 ? pal.Floor : pal.Ceiling);
                        if (slice != expected) failures.Add($"{(n.y > 0 ? "floor" : "ceiling")} at {centre} slice {slice}, expected {expected}");
                        if (n.y > 0) floors++; else ceilings++;
                        if (n.y > 0 && math.all(front == new int3(5, 0, 2))) lanternFloor = math.max(lanternFloor, colors[b].r / 255f);
                        if (n.y > 0 && math.all(front == new int3(18, 0, 3))) farFloor = colors[b].r / 255f;
                    }
                }
            }

            Debug.Log($"{Tag} total {totalV} vertices, {totalT} triangles: {walls} walls (expected {expectedWalls}), " +
                      $"{floors} floors, {ceilings} ceilings, {kit} kit quads; light r {darkest:0.00}..{brightest:0.00}, " +
                      $"under lantern {lanternFloor:0.00}, far corridor {farFloor:0.00}");
            if (walls != expectedWalls) failures.Add($"{walls} walls, expected {expectedWalls}");

            // Expected horizontal faces: one floor per walkable cell with a floor, one ceiling per cell with a ceiling.
            int expectFloors = 0, expectCeilings = 0;
            for (var i = 0; i < g.Cells.Length; i++)
            {
                var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                if (!g.Get(c).Walkable) continue;
                if (HoldRules.HasFloor(g, c)) expectFloors++;
                if (HoldRules.HasCeiling(g, c)) expectCeilings++;
            }
            if (floors != expectFloors) failures.Add($"{floors} floors, expected {expectFloors}");
            if (ceilings != expectCeilings) failures.Add($"{ceilings} ceilings, expected {expectCeilings}");
            if (HoldRules.HasFloor(g, new int3(12, 1, 3)) || HoldRules.HasCeiling(g, new int3(12, 0, 3))) failures.Add("shaft is not open");
            if (HoldRules.HasWall(g, new int3(19, 0, 3), new int3(1, 0, 0))) failures.Add("gate has a wall on its way out");
            if (lanternFloor < config.Ambient * 3f) failures.Add($"floor under the lantern only {lanternFloor}");
            if (farFloor < 0f || farFloor > config.Ambient + 0.02f) failures.Add($"far corridor floor {farFloor}, expected ~ambient");
            if (kit == 0) failures.Add("no kit pieces (door frame, pillar, lantern)");
            if (HoldRules.HasCeiling(g, new int3(16, 1, 8))) failures.Add("ShaftUp on the top level is capped (shaft into the region above sealed)");
            if (HoldRules.HasFloor(g, new int3(9, 0, 8))) failures.Add("ShaftDown on the bottom level has a floor (shaft into the region below sealed)");
            CheckLadders(g, config, all, failures);
        }

        /// <summary>Every ShaftUp has a ladder: on its LadderSide wall, touching it, floor to LadderAbove over the upper floor, rungs.</summary>
        static void CheckLadders(in HoldGrid g, HoldConfig config, List<float3> verts, List<string> failures)
        {
            var inner = HoldRules.LadderSide(g, new int3(12, 0, 3));
            if (math.any(inner != new int3(0, 0, -1))) failures.Add($"inner shaft ladder on {inner}, expected (0,0,-1) (Solid behind on both storeys, floor opposite)");
            if (math.any(HoldRules.LadderSide(g, new int3(3, 0, 3)) != 0)) failures.Add("ladder side for a non-shaft cell");
            var scale = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            var ladders = 0;
            for (var i = 0; i < g.Cells.Length; i++)
            {
                var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                if (g.Get(c).Kind != CellKind.ShaftUp) continue;
                ladders++;
                var d = HoldRules.LadderSide(g, c);
                if (math.all(d == 0)) { failures.Add($"shaft {c}: no ladder side"); continue; }
                int across = d.x != 0 ? 0 : 2, along = d.x != 0 ? 2 : 0;
                var s = d[across];
                var lo = ((float3)(g.Origin + c)) * scale;
                var hi = lo + scale;
                var wall = s > 0 ? hi[across] : lo[across];
                float yMin = float.MaxValue, yMax = float.MinValue, gapMin = float.MaxValue, gapMax = float.MinValue;
                var heights = new SortedSet<float>();
                var n = 0;
                foreach (var v in verts)
                {
                    var gap = s * (wall - v[across]);   // metres in front of the wall
                    if (gap < -1e-3f || gap > HoldRules.LadderDepth + 1e-3f) continue;
                    if (v[along] <= lo[along] + 0.05f || v[along] >= hi[along] - 0.05f) continue;   // the wall's own corners
                    if (v.y < lo.y - 1e-3f || v.y > lo.y + config.StoreyHeight + HoldRules.LadderAbove + 1e-3f) continue;
                    n++;
                    yMin = math.min(yMin, v.y); yMax = math.max(yMax, v.y);
                    gapMin = math.min(gapMin, gap); gapMax = math.max(gapMax, gap);
                    heights.Add(math.round(v.y * 100f) / 100f);
                }
                var top = lo.y + config.StoreyHeight + HoldRules.LadderAbove;
                Debug.Log($"{Tag} ladder {c} on {d}: {n} vertices, y {yMin:0.00}..{yMax:0.00} (floor {lo.y:0.00}, top {top:0.00}), " +
                          $"{gapMin:0.000}..{gapMax:0.000} m off the wall, {heights.Count} distinct heights");
                if (n < 40) { failures.Add($"shaft {c}: ladder has {n} vertices"); continue; }
                if (math.abs(yMin - lo.y) > 1e-3f) failures.Add($"shaft {c}: ladder starts at {yMin}, floor at {lo.y} (floating?)");
                if (math.abs(yMax - top) > 1e-3f) failures.Add($"shaft {c}: ladder tops out at {yMax}, expected {top}");
                if (gapMin > 1e-3f) failures.Add($"shaft {c}: ladder {gapMin} m off the wall (floating)");
                // Rungs: two heights each (bottom, top), at most RungPitch + a bit apart.
                var prev = float.NaN;
                var maxStep = 0f;
                foreach (var h in heights) { if (!float.IsNaN(prev)) maxStep = math.max(maxStep, h - prev); prev = h; }
                if (heights.Count < 20 || maxStep > 0.36f) failures.Add($"shaft {c}: rungs too sparse ({heights.Count} heights, biggest gap {maxStep:0.00} m)");
            }
            if (ladders != 2) failures.Add($"{ladders} ShaftUp cells checked, expected 2");
        }

        static int3 Dir(int i) => i switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 0, 1), _ => new int3(0, 0, -1),
        };
    }
}
