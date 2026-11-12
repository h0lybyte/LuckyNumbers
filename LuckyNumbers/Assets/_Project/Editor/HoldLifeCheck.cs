using System.Collections.Generic;
using System.Text;
using LuckyNumbers.Hold;
using LuckyNumbers.HoldScene;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Headless check of HoldLifePlan: a 5x5 block of mine regions at each of depths 0..5. Asserts every cache and
    /// creature is on a standable mine cell, clear of shafts and two cells in from the region edge (the openings),
    /// a cache's wall really is rock, and a second plan is identical; prints counts and finds by depth.
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.HoldLifeCheck.Run
    /// </summary>
    public static class HoldLifeCheck
    {
        const uint Seed = 1234;
        const int Side = 5, Depths = 6;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Life Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var life = config.Life;
            var p = MineParams.From(config);
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 40) Debug.LogError($"[HoldLifeCheck] FAIL {why}"); }
            if (life == null) { Debug.LogError("[HoldLifeCheck] FAIL no HoldConfig.life"); return; }

            var caches = new List<CachePlan>();
            var creatures = new List<CreaturePlan>();
            var again = new List<CachePlan>();
            var againC = new List<CreaturePlan>();
            var report = new StringBuilder();
            report.AppendLine($"[HoldLifeCheck] seed {Seed}, {Side}x{Side} regions per depth");
            var totalCaches = 0;
            var totalCreatures = 0;
            for (var depth = 0; depth < Depths; depth++)
            {
                int chamberCaches = 0, endCaches = 0, creatureCount = 0, chambers = 0, deadEnds = 0;
                var tiers = new int[life.Tiers.Count + 1];
                var kinds = new Dictionary<string, int>();
                for (var z = 0; z < Side; z++)
                for (var x = 0; x < Side; x++)
                {
                    var region = new int3(x - Side / 2, -depth, z - Side / 2);
                    var g = MineGenerator.Region(Seed, region, p, Allocator.Temp);
                    chambers += HoldLifePlan.Chambers(g).Count;
                    deadEnds += HoldLifePlan.DeadEnds(g).Count;
                    HoldLifePlan.Plan(Seed, region, g, life, caches, creatures);
                    HoldLifePlan.Plan(Seed, region, g, life, again, againC);
                    if (caches.Count != again.Count || creatures.Count != againC.Count) Fail($"region {region}: not deterministic (counts)");
                    else
                    {
                        for (var i = 0; i < caches.Count; i++)
                            if (!caches[i].Cell.Equals(again[i].Cell) || caches[i].Tier != again[i].Tier || caches[i].Item != again[i].Item)
                                Fail($"region {region}: cache {i} differs");
                        for (var i = 0; i < creatures.Count; i++)
                            if (!creatures[i].Cell.Equals(againC[i].Cell) || creatures[i].Creature != againC[i].Creature)
                                Fail($"region {region}: creature {i} differs");
                    }
                    if (caches.Count > life.CachesMax) Fail($"region {region}: {caches.Count} caches > {life.CachesMax}");
                    if (creatures.Count > life.CreaturesMax) Fail($"region {region}: {creatures.Count} creatures > {life.CreaturesMax}");
                    var used = new HashSet<int3>();
                    foreach (var c in caches)
                    {
                        var local = c.Cell - g.Origin;
                        Check(g, local, $"region {region} cache {c.Cell}", Fail);
                        if (g.Get(local + new int3(c.Wall.x, 0, c.Wall.y)).Walkable) Fail($"region {region} cache {c.Cell}: its wall {c.Wall} isn't rock");
                        if (!used.Add(c.Cell)) Fail($"region {region}: two things on {c.Cell}");
                        if (c.Spot == LifeSpot.Chamber) chamberCaches++; else endCaches++;
                        tiers[c.Tier < 0 ? life.Tiers.Count : c.Tier]++;
                        if (c.Tier >= 0 && life.Tiers[c.Tier].Items.Count > 0 && (c.Item < 0 || c.Item >= life.Tiers[c.Tier].Items.Count))
                            Fail($"region {region} cache {c.Cell}: item {c.Item} out of range");
                    }
                    foreach (var c in creatures)
                    {
                        var local = c.Cell - g.Origin;
                        Check(g, local, $"region {region} creature {c.Cell}", Fail);
                        if (!used.Add(c.Cell)) Fail($"region {region}: two things on {c.Cell}");
                        if (depth < life.CreaturesFromDepth) Fail($"region {region}: creature above creatures_from_depth");
                        creatureCount++;
                        var id = life.Creatures[c.Creature].Monster;
                        kinds[id] = kinds.TryGetValue(id, out var k) ? k + 1 : 1;
                        // A creature can wander: at least one step to take from its cell.
                        var path = new List<int2>();
                        if (!CellPaths.Find(g, local.y, local.xz, local.xz, 10, path)) Fail($"region {region} creature {c.Cell}: no path to itself");
                    }
                    g.Dispose();
                }
                totalCaches += chamberCaches + endCaches;
                totalCreatures += creatureCount;
                var tierText = new StringBuilder();
                for (var i = 0; i < life.Tiers.Count; i++) tierText.Append($" {life.Tiers[i].Name} {tiers[i]}");
                if (tiers[life.Tiers.Count] > 0) tierText.Append($" none {tiers[life.Tiers.Count]}");
                var kindText = new StringBuilder();
                foreach (var pair in kinds) kindText.Append($" {pair.Key} {pair.Value}");
                report.AppendLine($"  depth {depth}: {chambers} chambers, {deadEnds} dead ends -> caches {chamberCaches + endCaches} " +
                                  $"({chamberCaches} chamber, {endCaches} dead end; tiers{tierText}); creatures {creatureCount}{(kindText.Length > 0 ? $" ({kindText.ToString().Trim()})" : "")}");
            }

            // Paths: from each creature in the first depth-2 region that has some, to every passable cell on its level.
            var probe = new List<int2>();
            int far = 0, reached = 0, cells = 0;
            var probeRegion = int3.zero;
            for (var i = 0; i < Side * Side && far == 0; i++)
            {
                probeRegion = new int3(i % Side - Side / 2, -2, i / Side - Side / 2);
                var pg = MineGenerator.Region(Seed, probeRegion, p, Allocator.Temp);
                HoldLifePlan.Plan(Seed, probeRegion, pg, life, caches, creatures);
                foreach (var c in creatures)
                {
                    var local = c.Cell - pg.Origin;
                    for (var z = 0; z < pg.Size.z; z++)
                    for (var x = 0; x < pg.Size.x; x++)
                    {
                        if (!HoldLifePlan.Passable(pg.Get(new int3(x, local.y, z)))) continue;
                        cells++;
                        if (!CellPaths.Find(pg, local.y, local.xz, new int2(x, z), 4096, probe)) continue;
                        reached++;
                        far = math.max(far, probe.Count);
                        for (var k = 1; k < probe.Count; k++)
                            if (math.csum(math.abs(probe[k] - probe[k - 1])) != 1) Fail($"path step {probe[k - 1]} -> {probe[k]} isn't one cell");
                    }
                }
                pg.Dispose();
            }
            report.AppendLine($"  paths in region {probeRegion}: {reached}/{cells} passable cells reached from its creatures, longest {far} cells");
            report.AppendLine($"  total: {totalCaches} caches, {totalCreatures} creatures in {Side * Side * Depths} regions");
            Debug.Log(report.ToString());
            Debug.Log(failures == 0 ? "[HoldLifeCheck] PASS" : $"[HoldLifeCheck] {failures} failure(s)");
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static void Check(in HoldGrid g, int3 local, string what, System.Action<string> fail)
        {
            if (!g.InBounds(local)) { fail($"{what}: outside its region"); return; }
            if (g.Get(local).Kind != CellKind.Mine) fail($"{what}: not a mine cell ({g.Get(local).Kind})");
            if (!HoldLifePlan.Standable(g, local)) fail($"{what}: not standable");
            if (!HoldLifePlan.Clear(g, local)) fail($"{what}: on / beside a shaft or within 2 of the edge");
        }
    }
}
