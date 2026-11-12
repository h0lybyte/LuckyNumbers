using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using LuckyNumbers.Hold;
using LuckyNumbers.HoldScene;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Headless check of the Puzzles pass (MinePasses.Puzzles) over 4x4 blocks of mine regions at depths 0..7 for a
    /// few seeds. Every door / gate: a Door cell between two walls, exactly one key / lever with its link (links unique
    /// per region), a chokepoint (closing it alone cuts off cells, none of them a port); every key and lever reachable
    /// from the first port with all fixtures closed, lever on a wall; every walkable cell reachable with fixtures open;
    /// all ports mutually reachable with them closed; reward spots inside their door's pocket and given a strongbox by
    /// HoldLifePlan; a second generation identical. Prints counts by depth and timing.
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.PuzzleCheck.Run
    /// </summary>
    public static class PuzzleCheck
    {
        static readonly uint[] Seeds = { 1234, 77, 424242 };
        const int Side = 4, Depths = 8;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Puzzle Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 40) Debug.LogError($"[PuzzleCheck] FAIL {why}"); }
            if (config.Puzzles == null) Fail("no HoldConfig.puzzles");

            var doors = new int[Depths];
            var gates = new int[Depths];
            var withAny = new int[Depths];
            var rewards = new int[Depths];
            var pocketCells = new long[Depths];
            var keySteps = new long[Depths];
            var leverSteps = new long[Depths];
            var regions = 0;
            var caches = new List<CachePlan>();
            var creatures = new List<CreaturePlan>();
            var watch = new Stopwatch();
            foreach (var seed in Seeds)
            for (var depth = 0; depth < Depths; depth++)
            for (var z = 0; z < Side; z++)
            for (var x = 0; x < Side; x++)
            {
                var region = new int3(x - Side / 2, -depth, z - Side / 2);
                var name = $"seed {seed} region {region}";
                watch.Start();
                var g = MineGenerator.Region(seed, region, p, Allocator.Temp);
                watch.Stop();
                regions++;
                var again = MineGenerator.Region(seed, region, p, Allocator.Temp);
                if (!Same(g, again)) Fail($"{name}: not deterministic");
                again.Dispose();

                var ports = new NativeList<int3>(16, Allocator.Temp);
                MinePasses.PuzzlePorts(g, ports);
                // A depth seal closes its shaft on purpose (the band's key opens it): not a port the puzzles must keep.
                if (g.Fixtures.IsCreated)
                    for (var i = ports.Length - 1; i >= 0; i--)
                        foreach (var f in g.Fixtures)
                            if (f.Kind == FixtureKind.Seal && f.Cell.Equals(ports[i])) { ports.RemoveAtSwapBack(i); break; }
                var n = g.Cells.Length;
                var seen = new NativeArray<bool>(n, Allocator.Temp);
                var standable = 0;
                for (var i = 0; i < n; i++) if (HoldNav.Standable(g.Cells[i])) standable++;
                if (ports.Length == 0)
                {
                    Fail($"{name}: no ports");
                    seen.Dispose(); ports.Dispose(); g.Dispose();
                    continue;
                }

                // Open: everything; closed: all ports from each port.
                if (HoldNav.Flood(g, ports[0], false, seen) != standable) Fail($"{name}: not every cell reachable with fixtures open");
                foreach (var a in ports)
                {
                    HoldNav.Flood(g, a, true, seen);
                    foreach (var b in ports) if (!seen[g.Index(b)]) { Fail($"{name}: port {b} unreachable from {a} with fixtures closed"); break; }
                }
                HoldNav.Flood(g, ports[0], true, seen);
                var closed = new NativeArray<bool>(seen, Allocator.Temp);

                var links = new Dictionary<int, (int locks, int openers)>();
                var any = false;
                foreach (var f in g.Fixtures)
                {
                    if (f.Kind == FixtureKind.LockedDoor || f.Kind == FixtureKind.Portcullis)
                    {
                        any = true;
                        if (f.Kind == FixtureKind.LockedDoor) doors[depth]++; else gates[depth]++;
                        var e = links.TryGetValue(f.Link, out var v) ? v : default;
                        links[f.Link] = (e.locks + 1, e.openers);
                        var cell = g.Get(f.Cell);
                        if (cell.Kind != CellKind.Door) Fail($"{name}: {f.Kind} {f.Cell} on a {cell.Kind} cell");
                        bool wx = Solid(g, f.Cell + new int3(1, 0, 0)) && Solid(g, f.Cell - new int3(1, 0, 0));
                        bool wz = Solid(g, f.Cell + new int3(0, 0, 1)) && Solid(g, f.Cell - new int3(0, 0, 1));
                        if (wx == wz) Fail($"{name}: {f.Kind} {f.Cell} not between two walls");
                        if ((f.Dir == 0) != wz) Fail($"{name}: {f.Kind} {f.Cell} Dir {f.Dir} doesn't match its passage");
                        foreach (var port in ports) if (port.Equals(f.Cell)) Fail($"{name}: {f.Kind} on port {port}");
                        // Chokepoint: closing it alone cuts off a pocket with no port in it.
                        var alone = g;
                        alone.Fixtures = new NativeList<HoldFixture>(1, Allocator.Temp);
                        alone.Fixtures.Add(f);
                        HoldNav.Flood(alone, ports[0], true, seen);
                        alone.Fixtures.Dispose();
                        var pocket = 0;
                        for (var i = 0; i < n; i++)
                            if (!seen[i] && HoldNav.Standable(g.Cells[i]) && i != g.Index(f.Cell)) pocket++;
                        foreach (var port in ports) if (!seen[g.Index(port)]) Fail($"{name}: closing {f.Kind} {f.Cell} cuts off port {port}");
                        if (pocket == 0) Fail($"{name}: {f.Kind} {f.Cell} isn't a chokepoint");
                        pocketCells[depth] += pocket;
                        if (MinePasses.PuzzleReward(g, f, out var spot, out _))
                        {
                            rewards[depth]++;
                            if (seen[g.Index(spot)]) Fail($"{name}: door {f.Cell} reward {spot} isn't behind it");
                        }
                    }
                    else if (f.Kind == FixtureKind.Key || f.Kind == FixtureKind.Lever)
                    {
                        var e = links.TryGetValue(f.Link, out var v) ? v : default;
                        links[f.Link] = (e.locks, e.openers + 1);
                        if (!closed[g.Index(f.Cell)]) Fail($"{name}: {f.Kind} {f.Cell} not reachable with fixtures closed");
                        if (!HoldNav.Standable(g.Get(f.Cell))) Fail($"{name}: {f.Kind} {f.Cell} not standable");
                        if (f.Kind == FixtureKind.Lever && !Solid(g, f.Cell + MinePasses.PuzzleDirStep(f.Dir))) Fail($"{name}: lever {f.Cell} has no wall on side {f.Dir}");
                        var steps = Steps(g, f.Cell, g.Fixtures, f.Link);
                        if (steps < 0) Fail($"{name}: {f.Kind} {f.Cell}: its door/gate not found");
                        if (f.Kind == FixtureKind.Key) keySteps[depth] += steps; else leverSteps[depth] += steps;
                    }
                }
                foreach (var pair in links)
                    if (pair.Value.locks != 1 || pair.Value.openers != 1)
                        Fail($"{name}: link {pair.Key}: {pair.Value.locks} doors/gates, {pair.Value.openers} keys/levers");
                foreach (var f in g.Fixtures)
                {
                    if (f.Kind == FixtureKind.Key && Kind(g, f.Link, FixtureKind.LockedDoor) < 0) Fail($"{name}: key {f.Cell} link {f.Link} has no door");
                    if (f.Kind == FixtureKind.Lever && Kind(g, f.Link, FixtureKind.Portcullis) < 0) Fail($"{name}: lever {f.Cell} link {f.Link} has no gate");
                }
                if (any) withAny[depth]++;

                // HoldLifePlan: every reward spot gets a Locked strongbox, nothing sits on a fixture.
                if (config.Life != null)
                {
                    HoldLifePlan.Plan(seed, region, g, config.Life, caches, creatures);
                    var locked = 0;
                    foreach (var c in caches)
                    {
                        if (c.Spot == LifeSpot.Locked) locked++;
                        foreach (var f in g.Fixtures) if (g.ToWorld(f.Cell).Equals(c.Cell)) Fail($"{name}: cache on fixture {f.Kind} {f.Cell}");
                    }
                    var expected = 0;
                    foreach (var f in g.Fixtures) if (MinePasses.PuzzleReward(g, f, out _, out _)) expected++;
                    if (locked != math.min(expected, config.Life.CachesMax)) Fail($"{name}: {locked} locked caches for {expected} reward spots");
                }
                closed.Dispose();
                seen.Dispose();
                ports.Dispose();
                g.Dispose();
            }

            // The pass's own cost: the same 64 fresh regions dug with and without puzzles.
            var bare = p;
            bare.PuzzleDoors = bare.PuzzleGates = 0;
            double With(in MineParams q)
            {
                var w = Stopwatch.StartNew();
                for (var i = 0; i < 64; i++) MineGenerator.Region(9, new int3(i % 8 + 40, -(i / 8), 7), q, Allocator.Temp).Dispose();
                return w.Elapsed.TotalMilliseconds / 64.0;
            }
            With(bare);
            var msBare = With(bare);
            var msPuzzles = With(p);

            var report = new StringBuilder();
            report.AppendLine($"[PuzzleCheck] seeds {string.Join(", ", Seeds)}: {Side}x{Side} regions at depths 0..{Depths - 1} ({regions} regions), " +
                              $"{watch.Elapsed.TotalMilliseconds / regions:F2} ms/region (whole dig); pass alone {msPuzzles - msBare:F2} ms/region ({msBare:F2} without, {msPuzzles:F2} with)");
            for (var d = 0; d < Depths; d++)
            {
                var per = Seeds.Length * Side * Side;
                var locks = doors[d] + gates[d];
                report.AppendLine($"  depth {d}: {doors[d]} doors, {gates[d]} gates in {withAny[d]}/{per} regions; {rewards[d]} reward spots; " +
                                  $"avg pocket {(locks > 0 ? pocketCells[d] / (float)locks : 0):F1} cells, key {(doors[d] > 0 ? keySteps[d] / (float)doors[d] : 0):F1} steps, " +
                                  $"lever {(gates[d] > 0 ? leverSteps[d] / (float)gates[d] : 0):F1} steps");
            }
            Debug.Log(report.ToString());
            Debug.Log(failures == 0 ? "[PuzzleCheck] PASS" : $"[PuzzleCheck] {failures} failure(s)");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static bool Solid(in HoldGrid g, int3 c) => g.Get(c).Kind == CellKind.Solid;

        static int Kind(in HoldGrid g, ushort link, FixtureKind kind)
        {
            for (var i = 0; i < g.Fixtures.Length; i++) if (g.Fixtures[i].Kind == kind && g.Fixtures[i].Link == link) return i;
            return -1;
        }

        /// <summary>Steps (fixtures open) from a key / lever to the door / gate of its link; -1 if none.</summary>
        static int Steps(in HoldGrid g, int3 from, NativeList<HoldFixture> fixtures, ushort link)
        {
            var target = new int3(-1);
            foreach (var f in fixtures)
                if ((f.Kind == FixtureKind.LockedDoor || f.Kind == FixtureKind.Portcullis) && f.Link == link) target = f.Cell;
            if (target.x < 0) return -1;
            var dist = new Dictionary<int3, int> { [from] = 0 };
            var queue = new Queue<int3>();
            queue.Enqueue(from);
            var moves = new FixedList128Bytes<int3>();
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                if (c.Equals(target)) return dist[c];
                moves.Clear();
                HoldNav.Moves(g, c, ref moves);
                foreach (var to in moves)
                {
                    if (!g.InBounds(to) || dist.ContainsKey(to) || !HoldNav.Standable(g.Get(to))) continue;
                    dist[to] = dist[c] + 1;
                    queue.Enqueue(to);
                }
            }
            return -1;
        }

        static bool Same(in HoldGrid a, in HoldGrid b)
        {
            if (a.Cells.Length != b.Cells.Length || a.Fixtures.Length != b.Fixtures.Length) return false;
            for (var i = 0; i < a.Cells.Length; i++)
            {
                HoldCell u = a.Cells[i], v = b.Cells[i];
                if (u.Kind != v.Kind || u.Flags != v.Flags || u.Palette != v.Palette) return false;
            }
            for (var i = 0; i < a.Fixtures.Length; i++)
            {
                HoldFixture u = a.Fixtures[i], v = b.Fixtures[i];
                if (u.Kind != v.Kind || !u.Cell.Equals(v.Cell) || u.Dir != v.Dir || u.Link != v.Link || u.Data != v.Data) return false;
            }
            return true;
        }
    }
}
