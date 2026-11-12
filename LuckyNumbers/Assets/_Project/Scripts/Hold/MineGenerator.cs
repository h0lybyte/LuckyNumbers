using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Hold
{
    /// <summary>MineRules and region size flattened to plain values, so the digger needs no managed objects.</summary>
    public struct MineParams
    {
        public int Cells, Levels;
        public int OpeningsMin, OpeningsMax;
        public float ShaftChance, TurnChance;
        public int ChambersMin, ChambersMax, ChamberSizeMin, ChamberSizeMax;
        public int CellsPerLantern;
        public byte Palette;

        public static MineParams From(HoldConfig config)
        {
            var r = config.Mines ?? new MineRules();
            var p = new MineParams
            {
                Cells = math.max(12, config.RegionCells),
                Levels = math.max(1, config.RegionLevels),
                OpeningsMin = math.max(0, r.OpeningsMin),
                ShaftChance = math.saturate(r.ShaftChance),
                TurnChance = math.saturate(r.TurnChance),
                ChambersMin = math.max(0, r.ChambersMin),
                ChamberSizeMin = math.max(2, r.ChamberSizeMin),
                CellsPerLantern = math.max(1, r.CellsPerLantern),
                Palette = config.Textures != null ? HoldPalettes.Find(config.Textures, r.Palette) : (byte)0,
            };
            p.OpeningsMax = math.max(p.OpeningsMin, r.OpeningsMax);
            p.ChambersMax = math.max(p.ChambersMin, r.ChambersMax);
            p.ChamberSizeMax = math.clamp(r.ChamberSizeMax, p.ChamberSizeMin, p.Cells - 4);
            p.ChamberSizeMin = math.min(p.ChamberSizeMin, p.ChamberSizeMax);
            return p;
        }
    }

    /// <summary>
    /// The endless mines (docs/holds.md, "Mines"): one region of region_cells x region_levels x region_cells, dug from
    /// (seed, region, rules) alone. Openings on each side come from a hash of that shared edge and the shaft to the
    /// region below from a hash of that pair, so neighbours always line up without seeing each other. Inside, every
    /// opening and shaft tunnels (wandering, turn_chance) to its level's hub or into an earlier tunnel, chambers grow
    /// off the tunnels, levels join through internal shafts, then timber props and lanterns. Each attempt is validated
    /// (solid margin except openings, shafts paired, everything walkable connected around the props) and re-rolled
    /// from an attempt counter until it passes. Integer hashing + Unity.Mathematics.Random only: identical everywhere.
    /// Local cells are (x, level, z); level 0 is the region's lowest storey.
    /// </summary>
    public static class MineGenerator
    {
        const int MaxAttempts = 16;
        const uint TagRegion = 0x52474E31u, TagEdge = 0x45444745u, TagShaft = 0x53484654u;

        public static HoldGrid Region(uint seed, int3 region, HoldConfig config, Allocator allocator) =>
            Region(seed, region, MineParams.From(config), allocator);

        public static HoldGrid Region(uint seed, int3 region, in MineParams p, Allocator allocator)
        {
            var n = p.Cells;
            var size = new int3(n, p.Levels, n);
            var grid = new HoldGrid(size, region * size, allocator);
            var dig = new Digger(p, Allocator.Temp);
            AddFixedPorts(seed, region, p, ref dig.Ports);
            var fixedPorts = dig.Ports.Length;
            var ok = false;
            for (var attempt = 0; attempt < MaxAttempts && !ok; attempt++)
            {
                dig.Reset(fixedPorts, Rng(Hash(seed, TagRegion, region.x, region.y, region.z, attempt)));
                ok = dig.Run() && dig.Validate();
            }
            if (!ok) Debug.LogWarning($"[MineGenerator] seed {seed} region {region}: no valid dig in {MaxAttempts} attempts");
            dig.WriteTo(ref grid);
            dig.Dispose();
            return grid;
        }

        /// <summary>
        /// Whether the region `upper` has a shaft down into the region below it, and where (local x, z; the upper's
        /// level 0 gets ShaftDown there, the lower's top level ShaftUp). For region y = 0 called with (x, 1, z) this is
        /// where the hold above can drop its way into the mines.
        /// </summary>
        public static bool ShaftBelow(uint seed, int3 upper, in MineParams p, out int2 cell)
        {
            var rng = Rng(Hash(seed, TagShaft, upper.x, upper.y, upper.z, 0));
            var has = rng.NextFloat() < p.ShaftChance;
            cell = rng.NextInt2(new int2(2), new int2(p.Cells - 2));
            return has;
        }

        /// <summary>
        /// Openings (positions along the edge, 3..cells-4) on the edge between `lower` and its +x (axis 0) or +z
        /// (axis 1) neighbour, on one level. Both regions sharing the edge get the same list.
        /// </summary>
        public static void EdgeOpenings(uint seed, int3 lower, int axis, int level, in MineParams p, ref FixedList64Bytes<int> along)
        {
            along.Clear();
            var rng = Rng(Hash(seed, TagEdge, lower.x, lower.y, lower.z, axis * 4096 + level));
            var room = math.max(1, (p.Cells - 6) / 3);
            var count = math.min(rng.NextInt(p.OpeningsMin, p.OpeningsMax + 1), math.min(room, 12));
            // Never sealed: east-west edges on the lowest level always open (both sides compute the same rule).
            if (count == 0 && axis == 0 && level == 0) count = 1;
            for (var i = 0; i < count; i++)
            for (var tries = 0; tries < 24; tries++)
            {
                var t = rng.NextInt(3, p.Cells - 3);
                var clear = true;
                for (var j = 0; j < along.Length; j++) clear &= math.abs(along[j] - t) >= 3;
                if (!clear) continue;
                along.Add(t);
                break;
            }
        }

        // ------------------------------------------------------------------------------------------- hashing

        static uint Rotl(uint x, int r) => (x << r) | (x >> (32 - r));

        static uint Step(uint h, uint k)
        {
            k *= 0xCC9E2D51u; k = Rotl(k, 15); k *= 0x1B873593u;
            h ^= k; h = Rotl(h, 13);
            return h * 5u + 0xE6546B64u;
        }

        /// <summary>Murmur3-style hash of (seed, tag, a, b, c, d): integers only, so identical on every platform.</summary>
        public static uint Hash(uint seed, uint tag, int a, int b, int c, int d)
        {
            var h = Step(seed, tag);
            h = Step(h, (uint)a); h = Step(h, (uint)b); h = Step(h, (uint)c); h = Step(h, (uint)d);
            h ^= 24u;
            h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            return h;
        }

        static Random Rng(uint h) => new(h == 0 ? 0x6C8E9CF5u : h);

        // ------------------------------------------------------------------------------------------- ports

        internal enum PortKind : byte { Opening, ShaftDown, ShaftUp }

        internal struct Port
        {
            public int3 Cell;      // local (x, level, z); for openings the margin cell
            public int2 Inward;    // first tunnel step direction (openings), zero for shafts
            public PortKind Kind;
        }

        static void AddFixedPorts(uint seed, int3 region, in MineParams p, ref NativeList<Port> ports)
        {
            var n = p.Cells;
            var along = new FixedList64Bytes<int>();
            for (var l = 0; l < p.Levels; l++)
            {
                // West (edge owned by the region to the west), east, south, north.
                EdgeOpenings(seed, region - new int3(1, 0, 0), 0, l, p, ref along);
                for (var i = 0; i < along.Length; i++) ports.Add(Opening(new int3(0, l, along[i]), new int2(1, 0)));
                EdgeOpenings(seed, region, 0, l, p, ref along);
                for (var i = 0; i < along.Length; i++) ports.Add(Opening(new int3(n - 1, l, along[i]), new int2(-1, 0)));
                EdgeOpenings(seed, region - new int3(0, 0, 1), 1, l, p, ref along);
                for (var i = 0; i < along.Length; i++) ports.Add(Opening(new int3(along[i], l, 0), new int2(0, 1)));
                EdgeOpenings(seed, region, 1, l, p, ref along);
                for (var i = 0; i < along.Length; i++) ports.Add(Opening(new int3(along[i], l, n - 1), new int2(0, -1)));
            }
            if (ShaftBelow(seed, region, p, out var down))
                ports.Add(new Port { Cell = new int3(down.x, 0, down.y), Kind = PortKind.ShaftDown });
            if (ShaftBelow(seed, region + new int3(0, 1, 0), p, out var up))
                ports.Add(new Port { Cell = new int3(up.x, p.Levels - 1, up.y), Kind = PortKind.ShaftUp });
        }

        static Port Opening(int3 cell, int2 inward) => new() { Cell = cell, Inward = inward, Kind = PortKind.Opening };

        // ------------------------------------------------------------------------------------------- digging

        /// <summary>All working state for one region: plain NativeArrays, reused across attempts (Burst-friendly).</summary>
        struct Digger
        {
            MineParams p;
            int n, levels;
            Random rng;
            public NativeList<Port> Ports;
            NativeArray<byte> kind;       // CellKind
            NativeArray<byte> flags;      // CellFlags
            NativeArray<ushort> owner;    // which dig made the cell (0 = solid)
            NativeArray<byte> chamber;    // 1 = inside a chamber
            NativeList<int4> chambers;    // x0, z0, w, d (plus level in a parallel list)
            NativeList<int> chamberLevel;
            NativeArray<int> queue;
            NativeArray<int2> hubs;
            ushort nextId;

            public Digger(in MineParams p, Allocator a)
            {
                this.p = p;
                n = p.Cells;
                levels = p.Levels;
                rng = new Random(1);
                var count = n * n * levels;
                Ports = new NativeList<Port>(32, a);
                kind = new NativeArray<byte>(count, a);
                flags = new NativeArray<byte>(count, a);
                owner = new NativeArray<ushort>(count, a);
                chamber = new NativeArray<byte>(count, a);
                chambers = new NativeList<int4>(16, a);
                chamberLevel = new NativeList<int>(16, a);
                queue = new NativeArray<int>(count, a);
                hubs = new NativeArray<int2>(levels, a);
                nextId = 1;
            }

            public void Dispose()
            {
                Ports.Dispose(); kind.Dispose(); flags.Dispose(); owner.Dispose(); chamber.Dispose();
                chambers.Dispose(); chamberLevel.Dispose(); queue.Dispose(); hubs.Dispose();
            }

            readonly int Idx(int x, int l, int z) => x + n * (z + n * l);
            readonly int Idx(int2 c, int l) => Idx(c.x, l, c.y);
            readonly bool Interior(int2 c) => c.x >= 1 && c.y >= 1 && c.x <= n - 2 && c.y <= n - 2;

            public void Reset(int fixedPorts, Random seeded)
            {
                rng = seeded;
                Ports.Length = fixedPorts;
                for (var i = 0; i < kind.Length; i++) { kind[i] = 0; flags[i] = 0; owner[i] = 0; chamber[i] = 0; }
                chambers.Clear();
                chamberLevel.Clear();
                nextId = 1;
            }

            public bool Run()
            {
                if (!AddInternalShafts()) return false;
                for (var l = 0; l < levels; l++) DigLevel(l);
                // Shaft cells keep their kind on top of whatever was carved through them.
                for (var i = 0; i < Ports.Length; i++)
                {
                    var port = Ports[i];
                    if (port.Kind == PortKind.Opening) continue;
                    kind[Idx(port.Cell.x, port.Cell.y, port.Cell.z)] =
                        (byte)(port.Kind == PortKind.ShaftDown ? CellKind.ShaftDown : CellKind.ShaftUp);
                }
                PlaceProps();
                PlaceLanterns();
                return true;
            }

            /// <summary>Levels l and l-1 join through one or two shafts, clear of every other shaft in the region.</summary>
            bool AddInternalShafts()
            {
                for (var l = 1; l < levels; l++)
                {
                    var count = 1 + (rng.NextFloat() < 0.3f ? 1 : 0);
                    for (var s = 0; s < count; s++)
                    {
                        var placed = false;
                        for (var tries = 0; tries < 64 && !placed; tries++)
                        {
                            var c = rng.NextInt2(new int2(2), new int2(n - 2));
                            var clear = true;
                            for (var i = 0; i < Ports.Length && clear; i++)
                                if (Ports[i].Kind != PortKind.Opening)
                                    clear = math.cmax(math.abs(Ports[i].Cell.xz - c)) >= 3;
                            if (!clear) continue;
                            Ports.Add(new Port { Cell = new int3(c.x, l, c.y), Kind = PortKind.ShaftDown });
                            Ports.Add(new Port { Cell = new int3(c.x, l - 1, c.y), Kind = PortKind.ShaftUp });
                            placed = true;
                        }
                        if (!placed && s == 0) return false;
                    }
                }
                return true;
            }

            void DigLevel(int l)
            {
                var chamberCount = rng.NextInt(p.ChambersMin, p.ChambersMax + 1);
                var hub = rng.NextInt2(new int2(n / 4), new int2(n - n / 4));
                hubs[l] = hub;
                var hubId = nextId++;
                if (chamberCount > 0) { CarveChamber(hub, l, hubId); chamberCount--; }
                else Carve(hub, l, hubId);

                // Ports on this level, in a shuffled order, tunnel to the hub or the first earlier dig they meet.
                var order = new FixedList512Bytes<int>();
                for (var i = 0; i < Ports.Length && order.Length < order.Capacity; i++)
                    if (Ports[i].Cell.y == l) order.Add(i);
                for (var i = order.Length - 1; i > 0; i--)
                {
                    var j = rng.NextInt(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                for (var k = 0; k < order.Length; k++)
                {
                    var port = Ports[order[k]];
                    var id = nextId++;
                    if (port.Kind == PortKind.Opening)
                    {
                        Carve(port.Cell.xz, l, id);
                        Tunnel(port.Cell.xz + port.Inward, port.Inward, 2, hub, l, id);
                    }
                    else Tunnel(port.Cell.xz, RandomDir(), 0, hub, l, id);
                }

                // Chambers grow off the tunnels; a few dead-end spurs for the look of worked-out seams.
                for (var c = 0; c < chamberCount; c++)
                    if (RandomDug(l, out var at)) CarveChamber(at, l, nextId++);
                var spurs = rng.NextInt(1, 4);
                for (var s = 0; s < spurs; s++)
                    if (RandomDug(l, out var at)) Spur(at, l, rng.NextInt(3, 9), nextId++);
            }

            void Carve(int2 c, int l, ushort id)
            {
                var i = Idx(c, l);
                if (owner[i] == 0) owner[i] = id;
                kind[i] = (byte)CellKind.Mine;
            }

            int2 RandomDir()
            {
                var r = rng.NextInt(4);
                return r == 0 ? new int2(1, 0) : r == 1 ? new int2(-1, 0) : r == 2 ? new int2(0, 1) : new int2(0, -1);
            }

            /// <summary>One step towards the target along the axis with the larger gap (delta != 0).</summary>
            static int2 Toward(int2 delta) =>
                math.abs(delta.x) >= math.abs(delta.y) ? new int2((int)math.sign(delta.x), 0) : new int2(0, (int)math.sign(delta.y));

            /// <summary>Back onto the target, along an axis picked in proportion to the remaining gap.</summary>
            int2 Steer(int2 delta)
            {
                var ax = math.abs(delta.x);
                var span = ax + math.abs(delta.y);
                if (span == 0) return RandomDir();
                return rng.NextInt(span) < ax ? new int2((int)math.sign(delta.x), 0) : new int2(0, (int)math.sign(delta.y));
            }

            /// <summary>A real turn (perpendicular), usually the way that closes the gap.</summary>
            int2 Turn(int2 delta, int2 dir)
            {
                var perp = new int2(-dir.y, dir.x);
                var along = math.dot(perp, delta);
                var side = along != 0 && rng.NextFloat() < 0.75f ? (int)math.sign(along) : rng.NextBool() ? 1 : -1;
                return perp * side;
            }

            /// <summary>
            /// Wander from `start` towards `target`, turning with turn_chance (mostly towards the target), and stop on
            /// the first cell an earlier dig made: every earlier dig already reaches the hub, so this one does too.
            /// </summary>
            void Tunnel(int2 start, int2 dir, int straight, int2 target, int l, ushort id)
            {
                var pos = start;
                if (Joined(pos, l, id)) return;
                Carve(pos, l, id);
                var maxSteps = n * 6;
                for (var step = 0; !pos.Equals(target); step++)
                {
                    var delta = target - pos;
                    if (step >= maxSteps) dir = Toward(delta);
                    else if (step >= straight && math.dot(dir, delta) < 0) dir = Steer(delta);
                    else if (step >= straight && rng.NextFloat() < p.TurnChance) dir = Turn(delta, dir);
                    if (!Interior(pos + dir) || dir.Equals(int2.zero)) dir = Toward(delta);
                    pos += dir;
                    if (Joined(pos, l, id)) return;
                    Carve(pos, l, id);
                }
            }

            readonly bool Joined(int2 c, int l, ushort id)
            {
                var o = owner[Idx(c, l)];
                return o != 0 && o != id;
            }

            void Spur(int2 start, int l, int length, ushort id)
            {
                var pos = start;
                var dir = RandomDir();
                for (var s = 0; s < length; s++)
                {
                    if (rng.NextFloat() < p.TurnChance) dir = new int2(-dir.y, dir.x) * (rng.NextBool() ? 1 : -1);
                    if (!Interior(pos + dir)) break;
                    pos += dir;
                    Carve(pos, l, id);
                }
            }

            void CarveChamber(int2 at, int l, ushort id)
            {
                var w = rng.NextInt(p.ChamberSizeMin, p.ChamberSizeMax + 1);
                var d = rng.NextInt(p.ChamberSizeMin, p.ChamberSizeMax + 1);
                var x0 = math.clamp(at.x - rng.NextInt(w), 1, n - 1 - w);
                var z0 = math.clamp(at.y - rng.NextInt(d), 1, n - 1 - d);
                for (var z = z0; z < z0 + d; z++)
                for (var x = x0; x < x0 + w; x++)
                {
                    Carve(new int2(x, z), l, id);
                    chamber[Idx(x, l, z)] = 1;
                }
                chambers.Add(new int4(x0, z0, w, d));
                chamberLevel.Add(l);
            }

            bool RandomDug(int l, out int2 at)
            {
                for (var tries = 0; tries < 256; tries++)
                {
                    at = rng.NextInt2(new int2(1), new int2(n - 1));
                    if (kind[Idx(at, l)] != 0) return true;
                }
                at = default;
                return false;
            }

            /// <summary>
            /// Timber props on interior cells of chambers at least 4x4, never two touching (even diagonally), so the
            /// chamber's ring and the cells between props always stay connected.
            /// </summary>
            void PlaceProps()
            {
                for (var c = 0; c < chambers.Length; c++)
                {
                    var r = chambers[c];
                    if (r.z < 4 || r.w < 4 || rng.NextFloat() >= 0.6f) continue;
                    var l = chamberLevel[c];
                    for (var z = r.y + 1; z <= r.y + r.w - 2; z += 2)
                    for (var x = r.x + 1; x <= r.x + r.z - 2; x += 2)
                    {
                        if (kind[Idx(x, l, z)] != (byte)CellKind.Mine || !ChamberAround(x, l, z) || Near(x, l, z, 1, CellFlags.Pillar))
                            continue;
                        flags[Idx(x, l, z)] |= (byte)CellFlags.Pillar;
                    }
                }
            }

            readonly bool ChamberAround(int x, int l, int z)
            {
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var i = Idx(x + dx, l, z + dz);
                    if (chamber[i] == 0 || kind[i] == 0) return false;
                }
                return true;
            }

            /// <summary>Any cell within Chebyshev `radius` on this level carrying `flag`.</summary>
            readonly bool Near(int x, int l, int z, int radius, CellFlags flag)
            {
                for (var dz = -radius; dz <= radius; dz++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= n || cz >= n) continue;
                    if ((flags[Idx(cx, l, cz)] & (byte)flag) != 0) return true;
                }
                return false;
            }

            readonly int WalkableNeighbours(int x, int l, int z)
            {
                var count = 0;
                if (x > 0 && kind[Idx(x - 1, l, z)] != 0) count++;
                if (x < n - 1 && kind[Idx(x + 1, l, z)] != 0) count++;
                if (z > 0 && kind[Idx(x, l, z - 1)] != 0) count++;
                if (z < n - 1 && kind[Idx(x, l, z + 1)] != 0) count++;
                return count;
            }

            /// <summary>
            /// About one lantern per cells_per_lantern walkable cells: chambers and junctions first, spaced out, the
            /// spacing relaxed (never below 2, so none touch) until the count is met.
            /// </summary>
            void PlaceLanterns()
            {
                var cells = new NativeList<int>(kind.Length / 2, Allocator.Temp);
                for (var i = 0; i < kind.Length; i++)
                    if (kind[i] != 0 && (flags[i] & (byte)CellFlags.Pillar) == 0) cells.Add(i);
                for (var i = cells.Length - 1; i > 0; i--)
                {
                    var j = rng.NextInt(i + 1);
                    (cells[i], cells[j]) = (cells[j], cells[i]);
                }
                var target = math.max(1, (cells.Length + p.CellsPerLantern / 2) / p.CellsPerLantern);
                var placed = 0;
                var spacing = math.max(2, (int)math.round(math.sqrt(p.CellsPerLantern)));
                for (var pass = 0; placed < target && spacing >= 2; pass++)
                {
                    var priorityOnly = pass == 0;
                    for (var k = 0; k < cells.Length && placed < target; k++)
                    {
                        var i = cells[k];
                        int x = i % n, z = i / n % n, l = i / (n * n);
                        if (x == 0 || z == 0 || x == n - 1 || z == n - 1) continue;   // not in the opening mouths
                        if (priorityOnly && chamber[i] == 0 && WalkableNeighbours(x, l, z) < 3) continue;
                        if (Near(x, l, z, spacing - 1, CellFlags.Lantern)) continue;
                        flags[i] |= (byte)CellFlags.Lantern;
                        placed++;
                    }
                    if (!priorityOnly) spacing--;
                }
                cells.Dispose();
            }

            // --------------------------------------------------------------------------------------- validation

            public bool Validate()
            {
                // Margin: solid except the openings.
                var openings = 0;
                for (var l = 0; l < levels; l++)
                for (var z = 0; z < n; z++)
                for (var x = 0; x < n; x++)
                {
                    if (x != 0 && z != 0 && x != n - 1 && z != n - 1) continue;
                    if (kind[Idx(x, l, z)] != 0) openings++;
                }
                var expected = 0;
                for (var i = 0; i < Ports.Length; i++)
                {
                    var port = Ports[i];
                    var k = (CellKind)kind[Idx(port.Cell.x, port.Cell.y, port.Cell.z)];
                    if (port.Kind == PortKind.Opening) { expected++; if (k != CellKind.Mine) return false; }
                    else if (k != (port.Kind == PortKind.ShaftDown ? CellKind.ShaftDown : CellKind.ShaftUp)) return false;
                    if ((flags[Idx(port.Cell.x, port.Cell.y, port.Cell.z)] & (byte)CellFlags.Pillar) != 0) return false;
                }
                if (openings != expected || expected == 0) return false;

                // Internal shafts pair up vertically.
                for (var l = 0; l < levels; l++)
                for (var z = 0; z < n; z++)
                for (var x = 0; x < n; x++)
                {
                    var k = (CellKind)kind[Idx(x, l, z)];
                    if (k == CellKind.ShaftDown && l > 0 && kind[Idx(x, l - 1, z)] != (byte)CellKind.ShaftUp) return false;
                    if (k == CellKind.ShaftUp && l < levels - 1 && kind[Idx(x, l + 1, z)] != (byte)CellKind.ShaftDown) return false;
                }

                // Lanterns never touch.
                for (var i = 0; i < kind.Length; i++)
                    if ((flags[i] & (byte)CellFlags.Lantern) != 0)
                    {
                        int x = i % n, z = i / n % n, l = i / (n * n);
                        flags[i] &= unchecked((byte)~CellFlags.Lantern);
                        var touching = Near(x, l, z, 1, CellFlags.Lantern);
                        flags[i] |= (byte)CellFlags.Lantern;
                        if (touching) return false;
                    }

                // Connected: one flood fill from the first opening, across levels through shafts, around the props.
                return Flood(Ports[0].Cell) == CountOpen();
            }

            readonly bool Open(int i) => kind[i] != 0 && (flags[i] & (byte)CellFlags.Pillar) == 0;

            readonly int CountOpen()
            {
                var count = 0;
                for (var i = 0; i < kind.Length; i++) if (Open(i)) count++;
                return count;
            }

            /// <summary>Cells reachable from `from` (4-neighbours on a level, ShaftDown/ShaftUp pairs between levels).</summary>
            public int Flood(int3 from)
            {
                for (var i = 0; i < chamber.Length; i++) chamber[i] &= 1;   // bit 1 = visited (cleared on Reset)
                var start = Idx(from.x, from.y, from.z);
                if (!Open(start)) return 0;
                int head = 0, tail = 0;
                queue[tail++] = start;
                chamber[start] |= 2;
                while (head < tail)
                {
                    var i = queue[head++];
                    int x = i % n, z = i / n % n, l = i / (n * n);
                    var k = (CellKind)kind[i];
                    if (x > 0) Visit(Idx(x - 1, l, z), ref tail);
                    if (x < n - 1) Visit(Idx(x + 1, l, z), ref tail);
                    if (z > 0) Visit(Idx(x, l, z - 1), ref tail);
                    if (z < n - 1) Visit(Idx(x, l, z + 1), ref tail);
                    if (k == CellKind.ShaftDown && l > 0 && kind[Idx(x, l - 1, z)] == (byte)CellKind.ShaftUp)
                        Visit(Idx(x, l - 1, z), ref tail);
                    if (k == CellKind.ShaftUp && l < levels - 1 && kind[Idx(x, l + 1, z)] == (byte)CellKind.ShaftDown)
                        Visit(Idx(x, l + 1, z), ref tail);
                }
                return tail;
            }

            void Visit(int i, ref int tail)
            {
                if (!Open(i) || (chamber[i] & 2) != 0) return;
                chamber[i] |= 2;
                queue[tail++] = i;
            }

            public readonly void WriteTo(ref HoldGrid grid)
            {
                // Digger and HoldGrid share the x + n * (z + n * level) layout.
                for (var i = 0; i < kind.Length; i++)
                {
                    if (kind[i] == 0) continue;
                    grid.Cells[i] = new HoldCell { Kind = (CellKind)kind[i], Palette = p.Palette, Flags = (CellFlags)flags[i] };
                }
            }
        }
    }
}
