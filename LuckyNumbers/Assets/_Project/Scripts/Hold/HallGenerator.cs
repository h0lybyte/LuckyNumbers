using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Hold
{
    /// <summary>A placed hall room: local footprint (x, z, width, depth) on level 0.</summary>
    public struct HallRoomPlaced
    {
        public string Name;
        public int4 Rect;
        public bool Tall;
    }

    /// <summary>What HallGenerator laid out, beside the grid: the gate (spawn, way out), the shaft, the rooms.</summary>
    public sealed class HallPlan
    {
        public uint Seed;
        /// <summary>Attempts used (1 = the first roll passed). Valid false = no roll passed; the last one is used.</summary>
        public int Attempts;
        public bool Valid;
        public string Failure;
        /// <summary>Why each re-rolled attempt failed (diagnostics: HallCheck tallies them).</summary>
        public readonly List<string> Rerolls = new();
        public int3 Region;
        /// <summary>Local unit direction out through the gate (the gate's cells sit on the region edge that way).</summary>
        public int3 Outward;
        /// <summary>The gate's level-0 cells (local), on the region edge.</summary>
        public int3[] Gate;
        /// <summary>Just inside the gate, middle of its width (local, level 0); face -Outward.</summary>
        public int3 Spawn;
        /// <summary>The ShaftDown cell (local, level 0), over mine region (0,0,0)'s entrance ShaftUp.</summary>
        public int3 Shaft;
        /// <summary>The great hall's floor (local x, z, width, depth).</summary>
        public int4 Hall;
        public readonly List<HallRoomPlaced> Rooms = new();
    }

    /// <summary>
    /// The hold's top (docs/holds.md, "Hall"): region (0, 1, 0), laid out from (seed, HallRules) alone. Plan, in a
    /// frame where the gate is on the v = 0 edge: a two-storey gate passage in from the cliff; the great hall (a tall
    /// nave between two rows of pillars, low aisles outside them, lanterns hung from both ceilings); the stair room
    /// round the mines' entrance column (MineGenerator.Entrance); the other rooms (forge, records, barracks,
    /// storerooms) placed as close to the hall and each other as fits, one wall apart; each joined to what's already
    /// connected by a door straight through a shared wall or by the shortest corridor (doors at both ends, always
    /// through a single wall line). The frame is then turned onto the edge farthest from the entrance. Every attempt
    /// is validated (<see cref="Validate"/>) and re-rolled from an attempt counter until one passes. Integer hashing
    /// and Unity.Mathematics.Random only: identical everywhere. Managed (runs once per visit, well under a ms).
    /// </summary>
    public static class HallGenerator
    {
        public static readonly int3 HallRegion = new(0, 1, 0);
        const int MaxAttempts = 64;
        const uint TagHall = 0x48414C4Cu;

        /// <summary>Regions at y >= 1 are the hold's top: the hall at HallRegion, solid rock everywhere else.</summary>
        public static bool IsTop(int3 region) => region.y >= 1;

        /// <summary>Solid rock: the top regions round the hall (nothing to mesh).</summary>
        public static HoldGrid Rock(int3 region, HoldConfig config, Allocator allocator)
        {
            var p = MineParams.From(config);
            var size = new int3(p.Cells, p.Levels, p.Cells);
            return new HoldGrid(size, region * size, allocator);
        }

        public static HoldGrid Generate(uint seed, HoldConfig config, Allocator allocator, out HallPlan plan)
        {
            var mp = MineParams.From(config);
            var n = mp.Cells;
            var size = new int3(n, mp.Levels, n);
            var entrance = MineGenerator.Entrance(seed, mp);
            var rules = config.Hall ?? new HallRules();
            var grid = new HoldGrid(size, HallRegion * size, allocator);
            var planner = new Planner(n, rules, config.Textures);
            plan = null;
            string why = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var rng = new Random(MineGenerator.Hash(seed, TagHall, attempt, 0, 0, 0) | 1u);
                var candidate = new HallPlan { Seed = seed, Region = HallRegion, Attempts = attempt + 1 };
                for (var i = 0; i < grid.Cells.Length; i++) grid.Cells[i] = HoldCell.Solid;
                if (planner.Run(rng, entrance, candidate, out why))
                {
                    planner.WriteTo(ref grid);
                    candidate.Valid = Validate(grid, entrance, out why);
                }
                if (plan != null) candidate.Rerolls.AddRange(plan.Rerolls);
                plan = candidate;
                if (plan.Valid) break;
                plan.Rerolls.Add(why);
            }
            if (!plan.Valid)
            {
                plan.Failure = why;
                Debug.LogWarning($"[HallGenerator] seed {seed}: no valid hall in {MaxAttempts} attempts ({why}); using the last");
            }
            return grid;
        }

        // ------------------------------------------------------------------------------------------- validation

        static readonly int3[] Dirs = { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) };

        static bool Open(HoldCell c) => c.Walkable && (c.Flags & CellFlags.Pillar) == 0;

        /// <summary>
        /// A finished hall grid is sound: walkable only on levels 0 and 1; the margin solid except the gate, whose cells
        /// sit on one edge; every door between two walls (open along one axis, walled on the other); exactly one
        /// ShaftDown, at the entrance; level 1 is only the air over open-above cells (and every open-above cell has
        /// it); pillars and lanterns where they belong; every open level-0 cell reachable from the gate.
        /// </summary>
        public static bool Validate(in HoldGrid g, int2 entrance, out string why)
        {
            var size = g.Size;
            int3 outward = default;
            int gates = 0, shafts = 0, open = 0;
            var start = new int3(-1);
            for (var y = 0; y < size.y; y++)
            for (var z = 0; z < size.z; z++)
            for (var x = 0; x < size.x; x++)
            {
                var c = new int3(x, y, z);
                var cell = g.Get(c);
                if (!cell.Walkable) continue;
                var margin = x == 0 || z == 0 || x == size.x - 1 || z == size.z - 1;
                if (y > 1) { why = $"walkable above level 1 at {c}"; return false; }
                if (y == 1)
                {
                    var below = g.Get(c - new int3(0, 1, 0));
                    if (!below.Walkable || (below.Flags & CellFlags.OpenAbove) == 0) { why = $"level-1 cell {c} not over open air"; return false; }
                    if ((cell.Flags & CellFlags.Pillar) != (below.Flags & CellFlags.Pillar)) { why = $"pillar at {c} broken between levels"; return false; }
                    if (cell.Kind is CellKind.Door or CellKind.ShaftDown or CellKind.ShaftUp) { why = $"{cell.Kind} on level 1 at {c}"; return false; }
                    if (margin && cell.Kind != CellKind.Gate) { why = $"level-1 margin cell {c}"; return false; }
                    continue;
                }
                if ((cell.Flags & CellFlags.OpenAbove) != 0 && !g.Get(c + new int3(0, 1, 0)).Walkable) { why = $"open-above {c} has rock above"; return false; }
                if ((cell.Flags & CellFlags.Pillar) != 0 && cell.Kind != CellKind.Floor) { why = $"pillar on {cell.Kind} at {c}"; return false; }
                if ((cell.Flags & (CellFlags.Pillar | CellFlags.Lantern)) == (CellFlags.Pillar | CellFlags.Lantern)) { why = $"lantern in a pillar at {c}"; return false; }
                if (Open(cell)) { open++; if (start.x < 0 && cell.Kind == CellKind.Gate) start = c; }
                if (margin)
                {
                    if (cell.Kind != CellKind.Gate) { why = $"margin cell {c} is {cell.Kind}"; return false; }
                    var o = x == 0 ? new int3(-1, 0, 0) : x == size.x - 1 ? new int3(1, 0, 0) : z == 0 ? new int3(0, 0, -1) : new int3(0, 0, 1);
                    if (gates > 0 && !o.Equals(outward)) { why = "gate cells on more than one edge"; return false; }
                    if ((x == 0 || x == size.x - 1) && (z == 0 || z == size.z - 1)) { why = $"gate in a corner {c}"; return false; }
                    outward = o;
                    gates++;
                }
                else if (cell.Kind == CellKind.Gate) { why = $"gate cell {c} off the edge"; return false; }
                if (cell.Kind == CellKind.ShaftUp) { why = $"shaft up in the hall at {c}"; return false; }
                if (cell.Kind == CellKind.ShaftDown)
                {
                    shafts++;
                    if (x != entrance.x || z != entrance.y) { why = $"shaft {c} not over the entrance {entrance}"; return false; }
                }
                if (cell.Kind == CellKind.Door)
                {
                    bool wx = HoldRules.HasWall(g, c, Dirs[0]) && HoldRules.HasWall(g, c, Dirs[1]);
                    bool wz = HoldRules.HasWall(g, c, Dirs[2]) && HoldRules.HasWall(g, c, Dirs[3]);
                    bool ox = Open(g.Get(c + Dirs[0])) && Open(g.Get(c + Dirs[1]));
                    bool oz = Open(g.Get(c + Dirs[2])) && Open(g.Get(c + Dirs[3]));
                    if (!(wx && oz && !wz) && !(wz && ox && !wx)) { why = $"door {c} not between two walls"; return false; }
                }
                if ((cell.Flags & CellFlags.Lantern) != 0)
                    for (var dz = -1; dz <= 1; dz++)
                    for (var dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dz != 0) && (g.Get(c + new int3(dx, 0, dz)).Flags & CellFlags.Lantern) != 0 && g.Get(c + new int3(dx, 0, dz)).Walkable)
                        { why = $"lanterns touch at {c}"; return false; }
            }
            // Level-1 lanterns never touch either.
            for (var z = 0; z < size.z; z++)
            for (var x = 0; x < size.x; x++)
            {
                var c = new int3(x, 1, z);
                if ((g.Get(c).Flags & CellFlags.Lantern) == 0) continue;
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dz != 0) && (g.Get(c + new int3(dx, 0, dz)).Flags & CellFlags.Lantern) != 0) { why = $"lanterns touch at {c}"; return false; }
            }
            if (gates == 0 || start.x < 0) { why = "no gate"; return false; }
            if (shafts != 1) { why = $"{shafts} shafts down"; return false; }

            // Reachable: flood level 0 from the gate round the pillars.
            var seen = new NativeArray<bool>(g.Cells.Length, Allocator.Temp);
            var queue = new NativeList<int3>(open, Allocator.Temp);
            queue.Add(start);
            seen[g.Index(start)] = true;
            for (var head = 0; head < queue.Length; head++)
            {
                var c = queue[head];
                for (var i = 0; i < 4; i++)
                {
                    var nb = c + Dirs[i];
                    if (!g.InBounds(nb) || seen[g.Index(nb)] || !Open(g.Get(nb))) continue;
                    seen[g.Index(nb)] = true;
                    queue.Add(nb);
                }
            }
            var reached = queue.Length;
            seen.Dispose();
            queue.Dispose();
            if (reached != open) { why = $"{reached}/{open} cells reachable from the gate"; return false; }
            why = null;
            return true;
        }

        // ------------------------------------------------------------------------------------------- planning

        /// <summary>
        /// One attempt in the plan frame: u across the gate, v inward from it (v = 0 is the gate's edge), two levels.
        /// Plain managed arrays, reused across attempts.
        /// </summary>
        sealed class Planner
        {
            sealed class Box
            {
                public string Name;
                public int U, V, W, D;
                public byte Palette;
                public bool Tall, Pillars, Connected, Stair, Joinable;
                public int CellsPerLantern;
                public bool Contains(int u, int v) => u >= U && u < U + W && v >= V && v < V + D;
                public bool Near(int u, int v, int m) => u >= U - m && u < U + W + m && v >= V - m && v < V + D + m;
            }

            readonly int n;
            readonly HallRules r;
            readonly byte hallPal, corridorPal;
            readonly byte[] roomPal;
            readonly byte[] kind, flags, pal;
            readonly List<Box> boxes = new();
            readonly List<List<int>> corridors = new();
            readonly int[] dist, prev, from;
            readonly List<int> queue = new();
            int side;
            Random rng;

            static readonly int2[] Steps = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

            public Planner(int n, HallRules rules, HoldTextures textures)
            {
                this.n = n;
                r = rules;
                byte Pal(string name) => textures != null ? HoldPalettes.Find(textures, name) : (byte)0;
                hallPal = Pal(rules.HallPalette);
                corridorPal = Pal(rules.CorridorPalette);
                roomPal = new byte[rules.Rooms.Count];
                for (var i = 0; i < roomPal.Length; i++) roomPal[i] = Pal(rules.Rooms[i].Palette);
                kind = new byte[n * n * 2];
                flags = new byte[n * n * 2];
                pal = new byte[n * n * 2];
                dist = new int[n * n];
                prev = new int[n * n];
                from = new int[n * n];
            }

            int I(int u, int v, int l = 0) => u + n * (v + n * l);
            bool Interior(int u, int v) => u >= 1 && v >= 1 && u <= n - 2 && v <= n - 2;
            int Between(int min, int max) => rng.NextInt(math.min(min, max), math.max(min, max) + 1);

            /// <summary>Plan (u, v) to local (x, z) for the chosen gate side (0 south, 1 north, 2 west, 3 east).</summary>
            int2 ToLocal(int u, int v) => side switch
            {
                0 => new int2(u, v), 1 => new int2(n - 1 - u, n - 1 - v), 2 => new int2(v, n - 1 - u), _ => new int2(n - 1 - v, u),
            };

            int2 ToPlan(int2 xz) => side switch
            {
                0 => xz, 1 => new int2(n - 1 - xz.x, n - 1 - xz.y), 2 => new int2(n - 1 - xz.y, xz.x), _ => new int2(xz.y, n - 1 - xz.x),
            };

            static int3 OutwardOf(int side) => side switch
            {
                0 => new int3(0, 0, -1), 1 => new int3(0, 0, 1), 2 => new int3(-1, 0, 0), _ => new int3(1, 0, 0),
            };

            void Set(int u, int v, int l, CellKind k, byte palette, CellFlags f = CellFlags.None)
            {
                var i = I(u, v, l);
                kind[i] = (byte)k;
                pal[i] = palette;
                flags[i] = (byte)f;
            }

            public bool Run(Random seeded, int2 entrance, HallPlan plan, out string why)
            {
                rng = seeded;
                System.Array.Clear(kind, 0, kind.Length);
                System.Array.Clear(flags, 0, flags.Length);
                System.Array.Clear(pal, 0, pal.Length);
                boxes.Clear();
                corridors.Clear();

                // The gate opens on the edge farthest from the entrance column (sometimes the next farthest).
                var d = new[] { entrance.y, n - 1 - entrance.y, entrance.x, n - 1 - entrance.x };
                int best = 0, second = -1;
                for (var s = 1; s < 4; s++) if (d[s] > d[best] || d[s] == d[best] && rng.NextBool()) best = s;
                for (var s = 0; s < 4; s++) if (s != best && (second < 0 || d[s] > d[second])) second = s;
                side = rng.NextFloat() < 0.75f ? best : second;
                var e = ToPlan(entrance);

                if (!LayHall(e, out why)) return false;

                // Rooms: the stair room round the entrance first, then the others in content order.
                for (var k = 0; k < r.Rooms.Count; k++)
                {
                    var rule = r.Rooms[k];
                    var count = rule.Stair ? 1 : Between(rule.CountMin, rule.CountMax);
                    for (var c = 0; c < count; c++)
                    {
                        var box = Place(rule, roomPal[k], rule.Stair ? e : new int2(-1));
                        if (box == null)
                        {
                            if (rule.Stair || c < rule.CountMin) { why = $"no room for {rule.Name}"; return false; }
                            break;
                        }
                        FillRoom(box, rule.Stair ? e : new int2(-1));
                    }
                }
                if (!HasStair()) { why = "no stair room rule"; return false; }

                // Join every room to what's connected, nearest first.
                for (var pass = 0; pass < boxes.Count; pass++)
                {
                    Box next = null;
                    var nextGap = int.MaxValue;
                    foreach (var b in boxes)
                    {
                        if (b.Connected || !b.Joinable) continue;
                        var g = GapToConnected(b);
                        if (g < nextGap) { nextGap = g; next = b; }
                    }
                    if (next == null) break;
                    if (!Connect(next)) { why = $"could not join {next.Name}"; return false; }
                }

                foreach (var b in boxes) if (b.Joinable && b.Name != "hall") Lanterns(b);
                CorridorLanterns();

                // The plan, in local cells.
                plan.Outward = OutwardOf(side);
                var hall = boxes[0];
                var passage = boxes[1];
                plan.Gate = new int3[passage.W];
                for (var i = 0; i < passage.W; i++) { var xz = ToLocal(passage.U + i, 0); plan.Gate[i] = new int3(xz.x, 0, xz.y); }
                var sp = ToLocal(passage.U + passage.W / 2, 1);
                plan.Spawn = new int3(sp.x, 0, sp.y);
                plan.Shaft = new int3(entrance.x, 0, entrance.y);
                plan.Hall = Local(hall);
                foreach (var b in boxes)
                    if (b.Joinable && b.Name != "hall") plan.Rooms.Add(new HallRoomPlaced { Name = b.Name, Rect = Local(b), Tall = b.Tall });
                why = null;
                return true;
            }

            int4 Local(Box b)
            {
                int2 a = ToLocal(b.U, b.V), c = ToLocal(b.U + b.W - 1, b.V + b.D - 1);
                var lo = math.min(a, c);
                var hi = math.max(a, c);
                return new int4(lo.x, lo.y, hi.x - lo.x + 1, hi.y - lo.y + 1);
            }

            bool HasStair()
            {
                foreach (var b in boxes) if (b.Stair) return true;
                return false;
            }

            // ------------------------------------------------------------------ the gate and the great hall

            bool LayHall(int2 e, out string why)
            {
                var gw = math.max(1, r.GateWidth);
                var aisle = math.max(1, r.AisleWidth);
                var w = Between(r.HallWidthMin, r.HallWidthMax);
                if (w % 2 == 0) w += w + 1 <= math.max(r.HallWidthMin, r.HallWidthMax) ? 1 : -1;
                if ((gw % 2) != (w % 2)) gw++;   // gate centred on the nave
                var l = Between(r.HallLengthMin, r.HallLengthMax);
                var p = math.max(2, Between(r.PassageMin, r.PassageMax));
                var nave = w - 2 * aisle - 2;
                if (nave < gw || w > n - 2 || p + 1 + l > n - 1) { why = "hall rules don't fit the region"; return false; }

                // Across: anywhere that leaves the entrance column clear of the hall and its wall (the stair room goes
                // round it); failing that, a hall short enough to end before it.
                var v0 = p + 1;
                var fits = new List<int>();
                for (var u = 1; u + w <= n - 1; u++)
                    if (e.y >= v0 + l + 1 || e.x <= u - 2 || e.x >= u + w + 1) fits.Add(u);
                if (fits.Count == 0)
                {
                    l = math.min(l, e.y - 2 - v0);
                    if (l < math.max(8, r.HallLengthMin - 4)) { why = "entrance column under the hall"; return false; }
                    for (var u = 1; u + w <= n - 1; u++) fits.Add(u);
                }
                var u0 = fits[rng.NextInt(fits.Count)];
                var hall = new Box { Name = "hall", U = u0, V = v0, W = w, D = l, Palette = hallPal, Tall = true, Connected = true, Joinable = true };
                var gc = u0 + w / 2;
                var passage = new Box { Name = "passage", U = gc - gw / 2, V = 0, W = gw, D = p + 1, Palette = hallPal };
                boxes.Add(hall);
                boxes.Add(passage);

                // Passage: two storeys, the gate cells on the edge open to the outside at both.
                for (var v = 0; v <= p; v++)
                for (var u = passage.U; u < passage.U + gw; u++)
                {
                    var k = v == 0 ? CellKind.Gate : CellKind.Floor;
                    Set(u, v, 0, k, hallPal, CellFlags.OpenAbove);
                    Set(u, v, 1, k, hallPal);
                }

                // Hall: low aisles on the outside, then the pillar rows, the tall nave between them.
                int rowA = u0 + aisle, rowB = u0 + w - 1 - aisle;
                var spacing = math.max(2, r.PillarSpacing);
                var first = v0 + 1 + (l - 3) % spacing / 2;
                for (var v = v0; v < v0 + l; v++)
                for (var u = u0; u < u0 + w; u++)
                {
                    var tall = u >= rowA && u <= rowB;
                    var pillar = (u == rowA || u == rowB) && v >= first && v <= v0 + l - 2 && (v - first) % spacing == 0;
                    var f = (tall ? CellFlags.OpenAbove : 0) | (pillar ? CellFlags.Pillar : 0);
                    Set(u, v, 0, CellKind.Floor, hallPal, f);
                    if (tall) Set(u, v, 1, CellKind.Floor, hallPal, pillar ? CellFlags.Pillar : 0);
                }

                // Lanterns: the aisles under their low ceiling (between the pillars), the nave from the high one.
                var ls = math.max(2, r.LanternSpacing);
                for (var v = first + 1; v <= v0 + l - 2; v += ls)
                {
                    Lantern(u0, v, 0);
                    Lantern(u0 + w - 1, v, 0);
                }
                int naveLo = rowA + 1, naveHi = rowB - 1;
                for (var v = v0 + 1; v <= v0 + l - 2; v += ls)
                {
                    if (naveHi - naveLo >= 4) { Lantern(naveLo + 1, v, 1); Lantern(naveHi - 1, v, 1); }
                    else Lantern((naveLo + naveHi) / 2, v, 1);
                }
                // Up in the arcade, between the pillars: lights the columns and the clerestory wall over the aisles.
                for (var v = first + 1; v <= v0 + l - 2; v += ls)
                {
                    Lantern(rowA, v, 1);
                    Lantern(rowB, v, 1);
                }
                Lantern(gc, 1, 1);
                why = null;
                return true;
            }

            void Lantern(int u, int v, int l)
            {
                var i = I(u, v, l);
                if (kind[i] == 0 || (flags[i] & (byte)CellFlags.Pillar) != 0 || kind[i] == (byte)CellKind.ShaftDown) return;
                if (NearLantern(u, v, l, 1)) return;
                flags[i] |= (byte)CellFlags.Lantern;
            }

            bool NearLantern(int u, int v, int l, int radius)
            {
                for (var dv = -radius; dv <= radius; dv++)
                for (var du = -radius; du <= radius; du++)
                {
                    int a = u + du, b = v + dv;
                    if (a < 0 || b < 0 || a >= n || b >= n) continue;
                    if ((flags[I(a, b, l)] & (byte)CellFlags.Lantern) != 0) return true;
                }
                return false;
            }

            // ------------------------------------------------------------------ rooms

            /// <summary>A free spot (one wall from everything), as close to the hall and the other rooms as fits.</summary>
            Box Place(HallRoom rule, byte palette, int2 mustContain)
            {
                Box best = null;
                var bestScore = float.MaxValue;
                for (var tries = 0; tries < 80; tries++)
                {
                    var w = math.max(3, Between(rule.WidthMin, rule.WidthMax));
                    var d = math.max(3, Between(rule.DepthMin, rule.DepthMax));
                    if (rng.NextBool()) (w, d) = (d, w);
                    int u, v;
                    if (mustContain.x >= 0)
                    {
                        // The shaft somewhere inside, never on a door's way in (kept off the room's edge when it fits).
                        u = mustContain.x - rng.NextInt(w > 3 ? 1 : 0, w > 3 ? w - 1 : w);
                        v = mustContain.y - rng.NextInt(d > 3 ? 1 : 0, d > 3 ? d - 1 : d);
                    }
                    else
                    {
                        u = rng.NextInt(1, math.max(2, n - w));
                        v = rng.NextInt(1, math.max(2, n - d));
                    }
                    if (u < 1 || v < 1 || u + w > n - 1 || v + d > n - 1) continue;
                    var ok = true;
                    var score = float.MaxValue;
                    foreach (var b in boxes)
                    {
                        int gu = math.max(b.U - (u + w), u - (b.U + b.W)), gv = math.max(b.V - (v + d), v - (b.V + b.D));
                        var gap = math.max(gu, gv);
                        if (gu < 1 && gv < 1) { ok = false; break; }   // one wall cell between, at least
                        if (!b.Joinable) continue;
                        // Facing across one wall with room for a door: best of all.
                        var overlap = gu == 1 ? math.min(v + d, b.V + b.D) - math.max(v, b.V) : gv == 1 ? math.min(u + w, b.U + b.W) - math.max(u, b.U) : 0;
                        var s = gap == 1 && overlap >= 3 ? 0f : gap + 2f;
                        if (b.Name == "hall") s -= 0.5f;
                        score = math.min(score, s);
                    }
                    if (!ok) continue;
                    score += rng.NextFloat() * 0.9f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = new Box
                    {
                        Name = rule.Name, U = u, V = v, W = w, D = d, Palette = palette, Tall = rule.Tall, Pillars = rule.Pillars,
                        Stair = rule.Stair, Joinable = true, CellsPerLantern = math.max(4, rule.CellsPerLantern),
                    };
                }
                if (best != null) boxes.Add(best);
                return best;
            }

            void FillRoom(Box b, int2 shaft)
            {
                for (var v = b.V; v < b.V + b.D; v++)
                for (var u = b.U; u < b.U + b.W; u++)
                {
                    Set(u, v, 0, CellKind.Floor, b.Palette, b.Tall ? CellFlags.OpenAbove : 0);
                    if (b.Tall) Set(u, v, 1, CellKind.Floor, b.Palette);
                }
                if (b.Pillars && b.W >= 5 && b.D >= 5)
                    for (var v = b.V + 1; v <= b.V + b.D - 2; v += 2)
                    for (var u = b.U + 1; u <= b.U + b.W - 2; u += 2)
                    {
                        if (shaft.x >= 0 && math.abs(u - shaft.x) <= 1 && math.abs(v - shaft.y) <= 1) continue;
                        flags[I(u, v, 0)] |= (byte)CellFlags.Pillar;
                        if (b.Tall) flags[I(u, v, 1)] |= (byte)CellFlags.Pillar;
                    }
                if (shaft.x >= 0) Set(shaft.x, shaft.y, 0, CellKind.ShaftDown, b.Palette);
            }

            int GapToConnected(Box a)
            {
                var gap = int.MaxValue;
                foreach (var b in boxes)
                    if (b.Connected && b.Joinable)
                        gap = math.min(gap, math.max(math.max(b.U - (a.U + a.W), a.U - (b.U + b.W)), math.max(b.V - (a.V + a.D), a.V - (b.V + b.D))));
                return gap;
            }

            void Lanterns(Box b)
            {
                var level = b.Tall ? 1 : 0;
                var cells = new List<int2>();
                for (var v = b.V; v < b.V + b.D; v++)
                for (var u = b.U; u < b.U + b.W; u++) cells.Add(new int2(u, v));
                for (var i = cells.Count - 1; i > 0; i--)
                {
                    var j = rng.NextInt(i + 1);
                    (cells[i], cells[j]) = (cells[j], cells[i]);
                }
                var target = math.max(1, (b.W * b.D + b.CellsPerLantern / 2) / b.CellsPerLantern);
                var placed = 0;
                for (var spacing = math.max(2, (int)math.round(math.sqrt(b.CellsPerLantern))); spacing >= 2 && placed < target; spacing--)
                    foreach (var c in cells)
                    {
                        if (placed >= target) break;
                        var i = I(c.x, c.y, level);
                        if ((flags[i] & (byte)(CellFlags.Pillar | CellFlags.Lantern)) != 0 || kind[I(c.x, c.y, 0)] == (byte)CellKind.ShaftDown) continue;
                        if (NearLantern(c.x, c.y, level, spacing - 1)) continue;
                        flags[i] |= (byte)CellFlags.Lantern;
                        placed++;
                    }
            }

            void CorridorLanterns()
            {
                var ls = math.max(2, r.LanternSpacing);
                foreach (var path in corridors)
                    for (var k = path.Count > ls ? ls / 2 : path.Count / 2; k < path.Count; k += ls)
                    {
                        int u = path[k] % n, v = path[k] / n;
                        if (!NearLantern(u, v, 0, 1)) flags[I(u, v, 0)] |= (byte)CellFlags.Lantern;
                    }
            }

            // ------------------------------------------------------------------ doors and corridors

            bool InAnyBox(int u, int v)
            {
                foreach (var b in boxes) if (b.Contains(u, v)) return true;
                return false;
            }

            /// <summary>Rock a corridor may run through: inside the margin, not a room or any room's wall.</summary>
            bool Free(int u, int v)
            {
                if (!Interior(u, v) || kind[I(u, v)] != 0) return false;
                foreach (var b in boxes) if (b.Near(u, v, 1)) return false;
                return true;
            }

            /// <summary>A doorway at (u, v) going along `s`: rock in a wall line, rock either side of it.</summary>
            bool DoorOk(int u, int v, int2 s)
            {
                if (!Interior(u, v) || kind[I(u, v)] != 0 || InAnyBox(u, v)) return false;
                var t = new int2(-s.y, s.x);
                int2 a = new int2(u, v) + t, b = new int2(u, v) - t;
                return kind[I(a.x, a.y)] == 0 && kind[I(b.x, b.y)] == 0 && InAnyBox(a.x, a.y) == false && InAnyBox(b.x, b.y) == false;
            }

            /// <summary>The cell inside a connected room/hall that a door at its edge opens onto (not a corner, open floor).</summary>
            bool Landing(int u, int v, int2 s, out Box into)
            {
                into = null;
                foreach (var b in boxes)
                {
                    if (!b.Connected || !b.Joinable || !b.Contains(u, v)) continue;
                    var t = new int2(-s.y, s.x);
                    if (!b.Contains(u + t.x, v + t.y) || !b.Contains(u - t.x, v - t.y)) return false;
                    var i = I(u, v);
                    if (kind[i] != (byte)CellKind.Floor || (flags[i] & (byte)CellFlags.Pillar) != 0) return false;
                    into = b;
                    return true;
                }
                return false;
            }

            void Door(int u, int v, byte palette) => Set(u, v, 0, CellKind.Door, palette);

            bool Connect(Box room)
            {
                for (var i = 0; i < dist.Length; i++) dist[i] = -1;
                queue.Clear();
                var directs = new List<int3>();   // door u, v, palette
                var order = Shuffled();
                foreach (var si in order)
                {
                    var s = Steps[si];
                    var t = new int2(-s.y, s.x);
                    // Non-corner cells on this side of the room.
                    int len = s.x != 0 ? room.D : room.W;
                    for (var k = 1; k < len - 1; k++)
                    {
                        var edge = s.x != 0
                            ? new int2(s.x > 0 ? room.U + room.W - 1 : room.U, room.V + k)
                            : new int2(room.U + k, s.y > 0 ? room.V + room.D - 1 : room.V);
                        var inner = I(edge.x, edge.y);
                        if (kind[inner] != (byte)CellKind.Floor || (flags[inner] & (byte)CellFlags.Pillar) != 0) continue;
                        var door = edge + s;
                        var c0 = door + s;
                        if (!DoorOk(door.x, door.y, s) || !Interior(c0.x, c0.y)) continue;
                        if (Landing(c0.x, c0.y, s, out var into)) directs.Add(new int3(door, into.Name == "hall" ? hallPal : room.Palette));
                        else if (kind[I(c0.x, c0.y)] == (byte)CellKind.Corridor) directs.Add(new int3(door, room.Palette));
                        else if (Free(c0.x, c0.y) && dist[I(c0.x, c0.y)] < 0)
                        {
                            var ci = I(c0.x, c0.y);
                            dist[ci] = 0;
                            prev[ci] = -1;
                            from[ci] = I(door.x, door.y);
                            queue.Add(ci);
                        }
                    }
                }
                if (directs.Count > 0)
                {
                    var pick = directs[rng.NextInt(directs.Count)];
                    Door(pick.x, pick.y, (byte)pick.z);
                    room.Connected = true;
                    return true;
                }

                for (var head = 0; head < queue.Count; head++)
                {
                    var x = queue[head];
                    int u = x % n, v = x / n;
                    // Goal: beside a corridor, or a door away from a connected room.
                    foreach (var si in order)
                    {
                        var s = Steps[si];
                        int2 y = new int2(u, v) + s, beyond = y + s;
                        if (kind[I(y.x, y.y)] == (byte)CellKind.Corridor)
                        {
                            Carve(x, -1, 0, room);
                            return true;
                        }
                        if (Interior(beyond.x, beyond.y) && DoorOk(y.x, y.y, s) && Landing(beyond.x, beyond.y, s, out var into))
                        {
                            Carve(x, I(y.x, y.y), into.Palette, room);
                            return true;
                        }
                    }
                    foreach (var si in order)
                    {
                        var s = Steps[si];
                        int a = u + s.x, b = v + s.y;
                        if (!Free(a, b) || dist[I(a, b)] >= 0) continue;
                        var ni = I(a, b);
                        dist[ni] = dist[x] + 1;
                        prev[ni] = x;
                        queue.Add(ni);
                    }
                }
                return false;
            }

            void Carve(int end, int farDoor, byte farPalette, Box room)
            {
                var path = new List<int>();
                var c = end;
                while (true)
                {
                    path.Add(c);
                    if (prev[c] < 0) break;
                    c = prev[c];
                }
                path.Reverse();
                var start = path[0];
                Door(from[start] % n, from[start] / n, room.Palette);
                foreach (var p in path) Set(p % n, p / n, 0, CellKind.Corridor, corridorPal);
                if (farDoor >= 0) Door(farDoor % n, farDoor / n, farPalette);
                corridors.Add(path);
                room.Connected = true;
            }

            int[] Shuffled()
            {
                var o = new[] { 0, 1, 2, 3 };
                for (var i = 3; i > 0; i--)
                {
                    var j = rng.NextInt(i + 1);
                    (o[i], o[j]) = (o[j], o[i]);
                }
                return o;
            }

            public void WriteTo(ref HoldGrid grid)
            {
                for (var l = 0; l < 2 && l < grid.Size.y; l++)
                for (var v = 0; v < n; v++)
                for (var u = 0; u < n; u++)
                {
                    var i = I(u, v, l);
                    if (kind[i] == 0) continue;
                    var xz = ToLocal(u, v);
                    grid.Set(new int3(xz.x, l, xz.y), new HoldCell { Kind = (CellKind)kind[i], Palette = pal[i], Flags = (CellFlags)flags[i] });
                }
            }
        }
    }
}
