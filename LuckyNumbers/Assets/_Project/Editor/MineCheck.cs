using System.Diagnostics;
using System.IO;
using System.Text;
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
    /// Headless check of MineGenerator: a 3x3 block of regions at depth 0 and the 3x3 under it. Asserts seams (edge
    /// openings face each other, shafts meet between depths), per-region connectivity, determinism, and times it.
    /// ASCII maps go to Logs/mines-ascii.txt. Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.MineCheck.Run
    /// </summary>
    public static class MineCheck
    {
        const uint Seed = 1234;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Mine Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            int n = p.Cells, levels = p.Levels;
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 40) Debug.LogError($"[MineCheck] FAIL {why}"); }

            // Warm up (JIT), then time the 18 regions.
            MineGenerator.Region(Seed, new int3(99, 99, 99), p, Allocator.Temp).Dispose();
            var grids = new HoldGrid[2, 3, 3];
            var watch = Stopwatch.StartNew();
            for (var d = 0; d < 2; d++)
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
                grids[d, z, x] = MineGenerator.Region(Seed, new int3(x - 1, -d, z - 1), p, Allocator.Persistent);
            var ms = watch.Elapsed.TotalMilliseconds / 18.0;

            // Determinism: a second generation is byte-identical.
            for (var d = 0; d < 2; d++)
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
            {
                var again = MineGenerator.Region(Seed, new int3(x - 1, -d, z - 1), config, Allocator.Temp);
                var a = grids[d, z, x];
                if (!again.Origin.Equals(a.Origin) || !again.Size.Equals(a.Size)) Fail($"region {x - 1},{-d},{z - 1}: origin/size differ");
                for (var i = 0; i < a.Cells.Length; i++)
                {
                    HoldCell u = a.Cells[i], v = again.Cells[i];
                    if (u.Kind != v.Kind || u.Palette != v.Palette || u.Flags != v.Flags) { Fail($"region {x - 1},{-d},{z - 1}: cell {i} differs"); break; }
                }
                again.Dispose();
            }

            // Seams: east/west and north/south neighbours agree on every edge cell, every level.
            var seamOpenings = 0;
            for (var d = 0; d < 2; d++)
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
            for (var l = 0; l < levels; l++)
            for (var t = 0; t < n; t++)
            {
                var g = grids[d, z, x];
                if (x < 2)
                {
                    bool a = g.Get(new int3(n - 1, l, t)).Walkable, b = grids[d, z, x + 1].Get(new int3(0, l, t)).Walkable;
                    if (a != b) Fail($"depth {-d} ({x},{z})->east level {l} t {t}: {a} vs {b}");
                    if (a && b) seamOpenings++;
                }
                if (z < 2)
                {
                    bool a = g.Get(new int3(t, l, n - 1)).Walkable, b = grids[d, z + 1, x].Get(new int3(t, l, 0)).Walkable;
                    if (a != b) Fail($"depth {-d} ({x},{z})->north level {l} t {t}: {a} vs {b}");
                    if (a && b) seamOpenings++;
                }
            }

            // Shafts between depths: upper level 0 ShaftDown <=> lower top level ShaftUp, same x, z.
            var shaftPairs = 0;
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
            for (var cz = 0; cz < n; cz++)
            for (var cx = 0; cx < n; cx++)
            {
                var down = grids[0, z, x].Get(new int3(cx, 0, cz)).Kind == CellKind.ShaftDown;
                var up = grids[1, z, x].Get(new int3(cx, levels - 1, cz)).Kind == CellKind.ShaftUp;
                if (down != up) Fail($"shaft ({x},{z}) cell {cx},{cz}: down {down} up {up}");
                if (down && up) shaftPairs++;
            }

            // The hold's entrance: region (0,0,0) always has its shaft up at MineGenerator.Entrance (the hall's stair drops
            // onto it); no other region at depth 0 has one (only rock above them).
            var entrance = MineGenerator.Entrance(Seed, p);
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
            for (var cz = 0; cz < n; cz++)
            for (var cx = 0; cx < n; cx++)
            {
                var up = grids[0, z, x].Get(new int3(cx, levels - 1, cz)).Kind == CellKind.ShaftUp;
                var expect = x == 1 && z == 1 && cx == entrance.x && cz == entrance.y;
                if (up != expect) Fail($"entrance: region ({x - 1},0,{z - 1}) cell {cx},{cz} top-level shaft up {up}, expected {expect}");
            }
            for (var s = 1u; s <= 64u; s++)
            {
                var e = MineGenerator.Entrance(s, p);
                if (!MineGenerator.ShaftBelow(s, new int3(0, 1, 0), p, out _)) Fail($"entrance: seed {s} has no shaft up");
                if (math.any(e < 2) || math.any(e > n - 3)) Fail($"entrance: seed {s} at {e} too near the edge");
            }

            // Connectivity, margin, and stats per region.
            int walkable = 0, lanterns = 0, pillars = 0, openings = 0;
            for (var d = 0; d < 2; d++)
            for (var z = 0; z < 3; z++)
            for (var x = 0; x < 3; x++)
            {
                var g = grids[d, z, x];
                var name = $"region {x - 1},{-d},{z - 1}";
                int open = 0, mouths = 0, first = -1;
                for (var i = 0; i < g.Cells.Length; i++)
                {
                    var c = g.Cells[i];
                    if (!c.Walkable) continue;
                    walkable++;
                    if ((c.Flags & CellFlags.Lantern) != 0) lanterns++;
                    if ((c.Flags & CellFlags.Pillar) != 0) { pillars++; continue; }
                    open++;
                    if (first < 0) first = i;
                    var cell = Cell(g, i);
                    if (cell.x == 0 || cell.z == 0 || cell.x == n - 1 || cell.z == n - 1)
                    {
                        mouths++;
                        var inward = cell.x == 0 ? new int3(1, 0, 0) : cell.x == n - 1 ? new int3(-1, 0, 0)
                            : cell.z == 0 ? new int3(0, 0, 1) : new int3(0, 0, -1);
                        if (!g.Get(cell + inward).Walkable) Fail($"{name}: opening {cell} has no tunnel behind it");
                        if ((cell.x == 0 || cell.x == n - 1) && (cell.z < 3 || cell.z > n - 4) || (cell.z == 0 || cell.z == n - 1) && (cell.x < 3 || cell.x > n - 4))
                            Fail($"{name}: opening {cell} too near a corner");
                    }
                }
                openings += mouths;
                if (mouths == 0) Fail($"{name}: sealed (no openings)");
                var reached = first < 0 ? 0 : Flood(g, Cell(g, first));
                if (reached != open) Fail($"{name}: {reached}/{open} cells reachable");
            }

            File.WriteAllText(Path.Combine(Application.dataPath, "..", "Logs", "mines-ascii.txt"), Ascii(grids, n, levels));
            foreach (var g in grids) g.Dispose();

            // Time a larger batch too (fresh regions, no cache), for a steadier number.
            watch.Restart();
            for (var i = 0; i < 64; i++) MineGenerator.Region(Seed, new int3(i % 8 + 10, -(i / 8), 3), p, Allocator.Temp).Dispose();
            var batchMs = watch.Elapsed.TotalMilliseconds / 64.0;

            Debug.Log($"[MineCheck] seed {Seed}, regions {n}x{levels}x{n}: 18 regions, {walkable} walkable " +
                      $"({100.0 * walkable / (18 * n * n * levels):F1}%), {openings} edge mouths, {seamOpenings} matched seam cells, " +
                      $"{shaftPairs} depth shafts, {lanterns} lanterns, {pillars} props");
            Debug.Log($"[MineCheck] timing: {ms:F2} ms/region (3x3x2 block), {batchMs:F2} ms/region (64 more)");
            Debug.Log(failures == 0 ? "[MineCheck] PASS seams, shafts, entrance, connectivity, determinism" : $"[MineCheck] {failures} failure(s)");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static int3 Cell(HoldGrid g, int i) => new(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);

        static bool Open(HoldGrid g, int3 c)
        {
            var cell = g.Get(c);
            return cell.Walkable && (cell.Flags & CellFlags.Pillar) == 0;
        }

        /// <summary>Independent flood fill over the finished grid (props block, shafts join levels).</summary>
        static int Flood(HoldGrid g, int3 start)
        {
            var seen = new NativeArray<bool>(g.Cells.Length, Allocator.Temp);
            var queue = new NativeArray<int3>(g.Cells.Length, Allocator.Temp);
            int head = 0, tail = 0;
            queue[tail++] = start;
            seen[g.Index(start)] = true;
            while (head < tail)
            {
                var c = queue[head++];
                var k = g.Get(c).Kind;
                for (var dir = 0; dir < 6; dir++)
                {
                    var o = dir switch
                    {
                        0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 0, 1), 3 => new int3(0, 0, -1),
                        4 => new int3(0, -1, 0), _ => new int3(0, 1, 0),
                    };
                    if (dir == 4 && !(k == CellKind.ShaftDown && g.Get(c + o).Kind == CellKind.ShaftUp)) continue;
                    if (dir == 5 && !(k == CellKind.ShaftUp && g.Get(c + o).Kind == CellKind.ShaftDown)) continue;
                    var nb = c + o;
                    if (!g.InBounds(nb) || !Open(g, nb) || seen[g.Index(nb)]) continue;
                    seen[g.Index(nb)] = true;
                    queue[tail++] = nb;
                }
            }
            seen.Dispose();
            queue.Dispose();
            return tail;
        }

        static string Ascii(HoldGrid[,,] grids, int n, int levels)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MineGenerator seed {Seed}: 3x3 regions x -1..1, z -1..1; north up, east right. " +
                          "# solid  . mine  v shaft down  ^ shaft up  * lantern  o timber prop");
            for (var d = 0; d < 2; d++)
            for (var l = levels - 1; l >= 0; l--)
            {
                sb.AppendLine();
                sb.AppendLine($"== region y {-d}, level {l} (world level {-d * levels + l}) ==");
                for (var rz = 2; rz >= 0; rz--)
                for (var cz = n - 1; cz >= 0; cz--)
                {
                    for (var rx = 0; rx < 3; rx++)
                    for (var cx = 0; cx < n; cx++)
                        sb.Append(Char(grids[d, rz, rx].Get(new int3(cx, l, cz))));
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        static char Char(HoldCell c)
        {
            if (!c.Walkable) return '#';
            if (c.Kind == CellKind.ShaftDown) return 'v';
            if (c.Kind == CellKind.ShaftUp) return '^';
            if ((c.Flags & CellFlags.Pillar) != 0) return 'o';
            if ((c.Flags & CellFlags.Lantern) != 0) return '*';
            return '.';
        }
    }
}
