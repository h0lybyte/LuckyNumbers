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
    /// Headless check of HallGenerator over many seeds: each hall validates first time or after re-rolls (no fallback),
    /// its shaft sits over mine region (0,0,0)'s entrance ShaftUp, the gate is on the edge with the spawn inside it,
    /// generation is deterministic; timing. ASCII maps (both levels) of the first seeds go to Logs/hall-ascii.txt.
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.HallCheck.Run
    /// </summary>
    public static class HallCheck
    {
        static readonly uint[] AsciiSeeds = { 1234, 1, 7, 42 };
        const int Seeds = 200;

        [UnityEditor.MenuItem("LuckyNumbers/Hold/Hall Check")]
        public static void Run()
        {
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            var failures = 0;
            void Fail(string why) { failures++; if (failures <= 40) Debug.LogError($"[HallCheck] FAIL {why}"); }
            var ascii = new StringBuilder();
            ascii.AppendLine("HallGenerator: region (0,1,0); north up, east right. # rock  . hall  r room  f forge  , corridor  + door  " +
                             "G gate  v shaft down  o pillar  * lantern  : open air (level 1 over a tall space)  ^ mines' shaft up (below)");

            HallGenerator.Generate(99, config, Allocator.Temp, out _).Dispose();   // warm up
            int attempts = 0, maxAttempts = 0, rooms = 0, lanterns = 0, pillars = 0, doors = 0;
            double totalMs = 0, worstMs = 0;
            var watch = new Stopwatch();
            var rerolls = new System.Collections.Generic.Dictionary<string, int>();
            for (var k = 0; k < Seeds; k++)
            {
                var seed = k < AsciiSeeds.Length ? AsciiSeeds[k] : (uint)(k * 7919 + 13);
                watch.Restart();
                var hall = HallGenerator.Generate(seed, config, Allocator.Temp, out var plan);
                var ms = watch.Elapsed.TotalMilliseconds;
                totalMs += ms;
                worstMs = math.max(worstMs, ms);
                attempts += plan.Attempts;
                maxAttempts = math.max(maxAttempts, plan.Attempts);
                rooms += plan.Rooms.Count;
                foreach (var r in plan.Rerolls)
                {
                    var key = System.Text.RegularExpressions.Regex.Replace(r ?? "?", @"[-\d]+", "#");
                    rerolls[key] = rerolls.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                var name = $"seed {seed}";
                if (!plan.Valid) Fail($"{name}: no valid hall ({plan.Failure})");
                var entrance = MineGenerator.Entrance(seed, p);
                if (!HallGenerator.Validate(hall, entrance, out var why)) Fail($"{name}: {why}");

                // The entrance: the hall's ShaftDown over mine region (0,0,0)'s top-level ShaftUp, same world x, z.
                var mine = MineGenerator.Region(seed, int3.zero, p, Allocator.Temp);
                var top = new int3(plan.Shaft.x, mine.Size.y - 1, plan.Shaft.z);
                var shaftWorld = hall.ToWorld(plan.Shaft);
                var upWorld = mine.ToWorld(top);
                if (hall.Get(plan.Shaft).Kind != CellKind.ShaftDown) Fail($"{name}: plan shaft {plan.Shaft} is {hall.Get(plan.Shaft).Kind}");
                if (mine.Get(top).Kind != CellKind.ShaftUp) Fail($"{name}: mines have {mine.Get(top).Kind} under the hall's shaft");
                if (!shaftWorld.xz.Equals(upWorld.xz) || shaftWorld.y != upWorld.y + 1) Fail($"{name}: shaft {shaftWorld} not right over {upWorld}");

                // Gate on the edge, the outside beyond it, the spawn inside and open.
                foreach (var g in plan.Gate)
                {
                    if (hall.Get(g).Kind != CellKind.Gate) Fail($"{name}: gate cell {g} is {hall.Get(g).Kind}");
                    if (hall.InBounds(g + plan.Outward)) Fail($"{name}: gate cell {g} not on the edge");
                }
                var spawn = hall.Get(plan.Spawn);
                if (!spawn.Walkable || (spawn.Flags & CellFlags.Pillar) != 0) Fail($"{name}: spawn {plan.Spawn} blocked");
                if (!hall.Get(plan.Spawn - plan.Outward).Walkable) Fail($"{name}: spawn faces rock");
                var kinds = new System.Collections.Generic.HashSet<string>();
                foreach (var room in plan.Rooms) kinds.Add(room.Name);
                foreach (var rule in config.Hall.Rooms)
                    if (rule.CountMin > 0 && !kinds.Contains(rule.Name)) Fail($"{name}: no {rule.Name}");

                for (var i = 0; i < hall.Cells.Length; i++)
                {
                    var c = hall.Cells[i];
                    if ((c.Flags & CellFlags.Lantern) != 0) lanterns++;
                    if ((c.Flags & CellFlags.Pillar) != 0) pillars++;
                    if (c.Kind == CellKind.Door) doors++;
                }

                // Deterministic.
                var again = HallGenerator.Generate(seed, config, Allocator.Temp, out _);
                for (var i = 0; i < hall.Cells.Length; i++)
                {
                    HoldCell a = hall.Cells[i], b = again.Cells[i];
                    if (a.Kind != b.Kind || a.Flags != b.Flags || a.Palette != b.Palette) { Fail($"{name}: cell {i} differs on regeneration"); break; }
                }
                again.Dispose();

                if (k < AsciiSeeds.Length) Ascii(ascii, hall, mine, plan, config.Textures);
                mine.Dispose();
                hall.Dispose();
            }

            File.WriteAllText(Path.Combine(Application.dataPath, "..", "Logs", "hall-ascii.txt"), ascii.ToString());
            Debug.Log($"[HallCheck] {Seeds} seeds: {rooms / (float)Seeds:F1} rooms, {doors / (float)Seeds:F1} doors, " +
                      $"{lanterns / (float)Seeds:F1} lanterns, {pillars / (float)Seeds:F1} pillar cells per hall; " +
                      $"attempts {attempts / (float)Seeds:F2} avg, {maxAttempts} max");
            foreach (var pair in rerolls) Debug.Log($"[HallCheck] re-rolls: {pair.Value} x {pair.Key}");
            Debug.Log($"[HallCheck] timing: {totalMs / Seeds:F2} ms/hall avg, {worstMs:F2} ms worst");
            Debug.Log(failures == 0 ? "[HallCheck] PASS layout, entrance, gate, determinism" : $"[HallCheck] {failures} failure(s)");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static void Ascii(StringBuilder sb, HoldGrid g, HoldGrid mine, HallPlan plan, HoldTextures textures)
        {
            sb.AppendLine();
            sb.Append($"== seed {plan.Seed} (attempt {plan.Attempts}): gate out {plan.Outward}, shaft {plan.Shaft}; rooms:");
            foreach (var r in plan.Rooms) sb.Append($" {r.Name} {r.Rect.z}x{r.Rect.w}{(r.Tall ? " tall" : "")}");
            sb.AppendLine(" ==");
            for (var l = g.Size.y - 1; l >= 0; l--)
            {
                sb.AppendLine($"-- level {l} --");
                for (var z = g.Size.z - 1; z >= 0; z--)
                {
                    for (var x = 0; x < g.Size.x; x++)
                    {
                        var c = g.Get(new int3(x, l, z));
                        var below = l > 0 ? g.Get(new int3(x, l - 1, z)) : default;
                        sb.Append(Char(c, l > 0 && below.Walkable && (below.Flags & CellFlags.OpenAbove) != 0, textures,
                            l == 0 && mine.Get(new int3(x, mine.Size.y - 1, z)).Kind == CellKind.ShaftUp));
                    }
                    sb.AppendLine();
                }
            }
        }

        static char Char(HoldCell c, bool air, HoldTextures textures, bool upBelow)
        {
            if (!c.Walkable) return upBelow ? '^' : '#';
            if ((c.Flags & CellFlags.Pillar) != 0) return 'o';
            if ((c.Flags & CellFlags.Lantern) != 0) return '*';
            switch (c.Kind)
            {
                case CellKind.Gate: return 'G';
                case CellKind.Door: return '+';
                case CellKind.ShaftDown: return 'v';
                case CellKind.Corridor: return ',';
            }
            if (air) return ':';
            var name = c.Palette < textures.Palettes.Count ? textures.Palettes[c.Palette].Name : "";
            return name == "hall" ? '.' : name == "forge" ? 'f' : 'r';
        }
    }
}
