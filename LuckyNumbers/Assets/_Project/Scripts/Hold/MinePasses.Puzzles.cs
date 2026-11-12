using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Hold
{
    /// <summary>PuzzleRules (HoldConfig.puzzles) flattened for the Puzzles pass.</summary>
    public partial struct MineParams
    {
        public float PuzzleDoorChance, PuzzleDoorPerDepth, PuzzleDoorMax;
        public float PuzzleGateChance, PuzzleGatePerDepth, PuzzleGateMax;
        public int PuzzleDoors, PuzzleGates;
        public int PuzzleBehindMin, PuzzleBehindMax, PuzzleKeyMin, PuzzleKeyMax, PuzzleLeverMin, PuzzleLeverMax, PuzzleRewardBonus;

        partial void FromPuzzles(HoldConfig config)
        {
            var r = config.Puzzles;
            if (r == null) return;   // no rules: no puzzles
            PuzzleDoorChance = math.saturate(r.DoorChance);
            PuzzleDoorPerDepth = r.DoorChancePerDepth;
            PuzzleDoorMax = math.saturate(r.DoorChanceMax);
            PuzzleDoors = math.clamp(r.DoorsMax, 0, 8);
            PuzzleGateChance = math.saturate(r.GateChance);
            PuzzleGatePerDepth = r.GateChancePerDepth;
            PuzzleGateMax = math.saturate(r.GateChanceMax);
            PuzzleGates = math.clamp(r.GatesMax, 0, 8);
            PuzzleBehindMin = math.max(1, r.BehindMinCells);
            PuzzleBehindMax = math.max(PuzzleBehindMin, r.BehindMaxCells);
            PuzzleKeyMin = math.max(2, r.KeyMinCells);
            PuzzleKeyMax = math.max(PuzzleKeyMin, r.KeyMaxCells);
            PuzzleLeverMin = math.max(2, r.LeverMinCells);
            PuzzleLeverMax = math.max(PuzzleLeverMin, r.LeverMaxCells);
            PuzzleRewardBonus = math.clamp(r.RewardDepthBonus, 0, 255);
        }
    }

    /// <summary>
    /// Puzzles pass: locked doors + keys, portcullises + levers. Per region (hash(seed, region) only), up to doors_max
    /// doors and gates_max gates, each rolled at its chance for the depth. A door or gate goes on a chokepoint: a
    /// one-wide tunnel cell (two opposite walls, so the mesher frames it as a Door cell) whose closing cuts off a pocket
    /// of behind_min..behind_max cells on its own level holding no opening, shaft, stair or other fixture, while every
    /// port (openings on the margin, shafts and great shafts out of the region) stays reachable from the others. Its
    /// key (or lever, on a wall) goes on the open side, found by walking with every door and gate closed, preferring
    /// dead ends and chambers far from the door. Behind a locked door the pass also marks a strongbox spot (Data, see
    /// <see cref="PuzzleReward"/>) for HoldLifePlan. The finished region is re-validated (every walkable cell reachable
    /// with fixtures open as before; every port, key and lever reachable with them closed) and the pass undoes itself
    /// if that ever fails.
    /// Chokepoints come from one depth-first search per placement over the HoldNav.Moves graph (doors and gates
    /// closed) rooted at the first port: a tunnel cell v whose DFS child c has low(c) >= disc(v) is a cut vertex and
    /// c's subtree (contiguous discovery times) is the pocket; subtree size, ports, level span and anything
    /// unsuitable in it are summed on the way back up, so every candidate is judged in that one pass. Moves are
    /// taken as symmetric (they are: ladders, open-above, great shafts; stairs are expected to be too).
    /// </summary>
    public static partial class MinePasses
    {
        const uint TagPuzzles = 0x50555A4Cu;
        const int PuzzleMaxTries = 8, PuzzleMaxMoves = 8;

        static readonly int3[] PuzzleSides = { new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1) };

        /// <summary>Unit step of a fixture direction (0 +x, 1 -x, 2 +z, 3 -z).</summary>
        public static int3 PuzzleDirStep(int dir) => PuzzleSides[dir & 3];

        /// <summary>Regions down from the top (region y 0: 0).</summary>
        public static int PuzzleDepth(int3 region) => math.max(0, -region.y);

        /// <summary>
        /// A locked door's reward spot (local cell behind it) and how many regions deeper its find is rolled; false
        /// if this fixture has none. Encoding of HoldFixture.Data on a LockedDoor: (depth bonus &lt;&lt; 16) | (cell index + 1).
        /// </summary>
        public static bool PuzzleReward(in HoldGrid g, in HoldFixture f, out int3 local, out int depthBonus)
        {
            local = default;
            depthBonus = 0;
            if (f.Kind != FixtureKind.LockedDoor) return false;
            var i = (f.Data & 0xFFFF) - 1;
            if (i < 0 || i >= g.Cells.Length) return false;
            local = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
            depthBonus = (f.Data >> 16) & 0xFF;
            return true;
        }

        /// <summary>Where you can come into a region from outside it: margin openings, shafts and great shafts on its
        /// bottom / top level, and any stair cell up there (it may climb out). Local cells, scan order.</summary>
        public static void PuzzlePorts(in HoldGrid g, NativeList<int3> ports)
        {
            ports.Clear();
            for (var y = 0; y < g.Size.y; y++)
            for (var z = 0; z < g.Size.z; z++)
            for (var x = 0; x < g.Size.x; x++)
            {
                var c = new int3(x, y, z);
                var cell = g.Get(c);
                if (!HoldNav.Standable(cell)) continue;
                var margin = x == 0 || z == 0 || x == g.Size.x - 1 || z == g.Size.z - 1;
                var bottom = y == 0 && (cell.Kind == CellKind.ShaftDown || cell.Kind == CellKind.GreatShaft || cell.Kind == CellKind.Stair);
                var top = y == g.Size.y - 1 && (cell.Kind == CellKind.ShaftUp || cell.Kind == CellKind.GreatShaft || cell.Kind == CellKind.Stair);
                if (margin || bottom || top) ports.Add(c);
            }
        }

        static float PuzzleChance(float at0, float perDepth, int depth, float max) => math.clamp(at0 + perDepth * depth, 0f, max);

        static partial void Puzzles(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            if (!grid.Fixtures.IsCreated || !grid.Cells.IsCreated || p.PuzzleDoors + p.PuzzleGates <= 0) return;
            var depth = PuzzleDepth(region);
            var rng = new Random(MineGenerator.Hash(seed, TagPuzzles, region.x, region.y, region.z, 0) | 1u);
            var doorChance = PuzzleChance(p.PuzzleDoorChance, p.PuzzleDoorPerDepth, depth, p.PuzzleDoorMax);
            var gateChance = PuzzleChance(p.PuzzleGateChance, p.PuzzleGatePerDepth, depth, p.PuzzleGateMax);
            int doors = 0, gates = 0;
            for (var i = 0; i < p.PuzzleDoors; i++) if (rng.NextFloat() < doorChance) doors++;
            for (var i = 0; i < p.PuzzleGates; i++) if (rng.NextFloat() < gateChance) gates++;
            if (doors + gates == 0) return;

            var work = new PuzzleWork(grid, Allocator.Temp);
            if (work.Ports.Length > 0)
            {
                // Doors and gates take turns (door first), so neither uses up the region's few chokepoints.
                for (int d = 0, gt = 0; d < doors || gt < gates;)
                {
                    var door = d < doors && (d <= gt || gt >= gates);
                    if (door) d++; else gt++;
                    work.TryPlace(ref grid, ref rng, door ? FixtureKind.LockedDoor : FixtureKind.Portcullis, p);
                }
                if (!work.Valid(grid)) work.Revert(ref grid);
            }
            work.Dispose();
        }

        /// <summary>The pass's working state for one region (Temp arrays, disposed at the end).</summary>
        struct PuzzleWork
        {
            public NativeList<int3> Ports;
            NativeArray<bool> _seen, _pocket, _locked, _used, _port;
            NativeArray<int> _dist, _nbr, _cnt;
            // Depth-first search (cut vertices): discovery, low link, parent, next move, subtree sums, separated child.
            NativeArray<int> _disc, _low, _parent, _it, _size, _ports, _bad, _minY, _maxY, _cut;
            NativeList<int> _stack, _queue, _valid;
            NativeList<int3> _candidates, _usedCells, _pick;
            NativeList<int> _backupIndex;
            NativeList<HoldCell> _backupCell;
            int _fixturesBefore, _openBefore;
            ushort _link;

            public PuzzleWork(in HoldGrid g, Allocator a)
            {
                this = default;
                var n = g.Cells.Length;
                Ports = new NativeList<int3>(16, a);
                MinePasses.PuzzlePorts(g, Ports);
                _seen = new NativeArray<bool>(n, a);
                _pocket = new NativeArray<bool>(n, a);
                _locked = new NativeArray<bool>(n, a);
                _used = new NativeArray<bool>(n, a);
                _port = new NativeArray<bool>(n, a);
                _dist = new NativeArray<int>(n, a);
                _nbr = new NativeArray<int>(n * PuzzleMaxMoves, a, NativeArrayOptions.UninitializedMemory);
                _cnt = new NativeArray<int>(n, a);
                _disc = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _low = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _parent = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _it = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _size = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _ports = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _bad = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _minY = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _maxY = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _cut = new NativeArray<int>(n, a, NativeArrayOptions.UninitializedMemory);
                _stack = new NativeList<int>(256, a);
                _queue = new NativeList<int>(256, a);
                _valid = new NativeList<int>(32, a);
                _candidates = new NativeList<int3>(64, a);
                _usedCells = new NativeList<int3>(16, a);
                _pick = new NativeList<int3>(64, a);
                _backupIndex = new NativeList<int>(8, a);
                _backupCell = new NativeList<HoldCell>(8, a);
                _fixturesBefore = g.Fixtures.Length;
                _openBefore = Ports.Length > 0 ? HoldNav.Flood(g, Ports[0], false, _seen) : 0;
                foreach (var port in Ports) _port[g.Index(port)] = true;
                _link = 1;
                foreach (var f in g.Fixtures)
                {
                    if (!g.InBounds(f.Cell)) continue;
                    Use(g, f.Cell);
                    // Cells that close (other passes' doors): never part of anyone's open side or pocket.
                    if (f.Kind == FixtureKind.LockedDoor || f.Kind == FixtureKind.Portcullis || f.Kind == FixtureKind.Seal)
                        _locked[g.Index(f.Cell)] = true;
                    if (f.Link >= _link) _link = (ushort)(f.Link + 1);
                }
                BuildMoves(g);
                FindCandidates(g);
            }

            public void Dispose()
            {
                Ports.Dispose(); _seen.Dispose(); _pocket.Dispose(); _locked.Dispose(); _used.Dispose(); _port.Dispose();
                _dist.Dispose(); _nbr.Dispose(); _cnt.Dispose();
                _disc.Dispose(); _low.Dispose(); _parent.Dispose(); _it.Dispose(); _size.Dispose(); _ports.Dispose();
                _bad.Dispose(); _minY.Dispose(); _maxY.Dispose(); _cut.Dispose();
                _stack.Dispose(); _queue.Dispose(); _valid.Dispose();
                _candidates.Dispose(); _usedCells.Dispose(); _pick.Dispose();
                _backupIndex.Dispose(); _backupCell.Dispose();
            }

            void Use(in HoldGrid g, int3 c)
            {
                _usedCells.Add(c);
                _used[g.Index(c)] = true;
            }

            /// <summary>Every standable cell's moves (HoldNav.Moves) to standable cells in the grid, as indices.</summary>
            void BuildMoves(in HoldGrid g)
            {
                var moves = new FixedList128Bytes<int3>();
                for (var i = 0; i < g.Cells.Length; i++)
                {
                    if (!HoldNav.Standable(g.Cells[i])) continue;
                    moves.Clear();
                    HoldNav.Moves(g, Cell(g, i), ref moves);
                    var k = 0;
                    foreach (var to in moves)
                    {
                        if (k >= PuzzleMaxMoves || !g.InBounds(to)) continue;
                        var j = g.Index(to);
                        if (!HoldNav.Standable(g.Cells[j])) continue;
                        var dup = false;
                        for (var m = 0; m < k; m++) dup |= _nbr[i * PuzzleMaxMoves + m] == j;
                        if (!dup) _nbr[i * PuzzleMaxMoves + k++] = j;
                    }
                    _cnt[i] = k;
                }
            }

            static bool Shafty(CellKind k) =>
                k == CellKind.ShaftDown || k == CellKind.ShaftUp || k == CellKind.GreatShaft || k == CellKind.Stair;

            /// <summary>No shaft, great shaft or stair within one cell (this level and the ones above / below).</summary>
            static bool ClearOfShafts(in HoldGrid g, int3 c)
            {
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (Shafty(g.Get(c + new int3(dx, dy, dz)).Kind)) return false;
                return true;
            }

            static bool Inner(in HoldGrid g, int3 c) => c.x >= 2 && c.z >= 2 && c.x <= g.Size.x - 3 && c.z <= g.Size.z - 3;

            static bool Solid(in HoldGrid g, int3 c) => g.Get(c).Kind == CellKind.Solid;

            static bool PlainMine(HoldCell c) => c.Kind == CellKind.Mine && (c.Flags & CellFlags.Pillar) == 0;

            static int3 Cell(in HoldGrid g, int i) => new(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);

            /// <summary>
            /// One-wide tunnel cells: plain mine (no prop, lantern, open-above), walls (Solid) on two opposite sides and
            /// plain mine on the other two, clear of shafts and the region's margin. Scan order.
            /// </summary>
            void FindCandidates(in HoldGrid g)
            {
                for (var y = 0; y < g.Size.y; y++)
                for (var z = 2; z < g.Size.z - 2; z++)
                for (var x = 2; x < g.Size.x - 2; x++)
                {
                    var c = new int3(x, y, z);
                    var cell = g.Get(c);
                    if (cell.Kind != CellKind.Mine || cell.Flags != CellFlags.None) continue;
                    if (Tunnel(g, c, 0) == Tunnel(g, c, 2)) continue;
                    if ((g.Get(c - new int3(0, 1, 0)).Flags & CellFlags.OpenAbove) != 0 || !ClearOfShafts(g, c)) continue;
                    _candidates.Add(c);
                }
            }

            /// <summary>Passage along axis (0 x, 2 z): plain mine both ways along it, rock both ways across.</summary>
            static bool Tunnel(in HoldGrid g, int3 c, int axis)
            {
                var along = axis == 0 ? new int3(1, 0, 0) : new int3(0, 0, 1);
                var across = axis == 0 ? new int3(0, 0, 1) : new int3(1, 0, 0);
                return PlainMine(g.Get(c + along)) && PlainMine(g.Get(c - along)) && Solid(g, c + across) && Solid(g, c - across);
            }

            bool NearUsed(int3 c, int radius)
            {
                foreach (var u in _usedCells)
                    if (u.y == c.y && math.cmax(math.abs(u.xz - c.xz)) <= radius) return true;
                return false;
            }

            /// <summary>Somewhere to put a key or lever: plain mine, in from the margin, clear of shafts and fixtures.</summary>
            bool Spot(in HoldGrid g, int3 c) =>
                PlainMine(g.Get(c)) && Inner(g, c) && ClearOfShafts(g, c) && !NearUsed(c, 1);

            /// <summary>
            /// Iterative DFS from the first port over unlocked cells (cut vertices, see the class notes). False if a
            /// port isn't reached (then nothing can be placed safely).
            /// </summary>
            bool Cuts(in HoldGrid g)
            {
                for (var i = 0; i < _disc.Length; i++) { _disc[i] = -1; _cut[i] = -1; }
                var root = g.Index(Ports[0]);
                if (_locked[root]) return false;
                var t = 0;
                _stack.Clear();
                Enter(g, root, -1, ref t);
                while (_stack.Length > 0)
                {
                    var v = _stack[_stack.Length - 1];
                    if (_it[v] < _cnt[v])
                    {
                        var w = _nbr[v * PuzzleMaxMoves + _it[v]++];
                        if (_locked[w]) continue;
                        if (_disc[w] < 0) Enter(g, w, v, ref t);
                        else _low[v] = math.min(_low[v], _disc[w]);
                        continue;
                    }
                    _stack.RemoveAt(_stack.Length - 1);
                    var p = _parent[v];
                    if (p < 0) continue;
                    _low[p] = math.min(_low[p], _low[v]);
                    _size[p] += _size[v];
                    _ports[p] += _ports[v];
                    _bad[p] += _bad[v];
                    _minY[p] = math.min(_minY[p], _minY[v]);
                    _maxY[p] = math.max(_maxY[p], _maxY[v]);
                    if (_low[v] >= _disc[p]) _cut[p] = v;
                }
                foreach (var port in Ports) if (_disc[g.Index(port)] < 0) return false;
                return true;
            }

            void Enter(in HoldGrid g, int w, int parent, ref int t)
            {
                _disc[w] = _low[w] = t++;
                _parent[w] = parent;
                _it[w] = 0;
                _size[w] = 1;
                _ports[w] = _port[w] ? 1 : 0;
                _bad[w] = g.Cells[w].Kind != CellKind.Mine || _used[w] ? 1 : 0;
                _minY[w] = _maxY[w] = w / (g.Size.x * g.Size.z);
                _stack.Add(w);
            }

            public bool TryPlace(ref HoldGrid g, ref Random rng, FixtureKind kind, in MineParams p)
            {
                if (!Cuts(g)) return false;
                // Candidates that cut off a fitting pocket: right size, one level, plain mine, no port, nothing in it.
                _valid.Clear();
                for (var k = 0; k < _candidates.Length; k++)
                {
                    var c = _candidates[k];
                    var v = g.Index(c);
                    var ch = _disc[v] >= 0 && !_locked[v] ? _cut[v] : -1;
                    if (ch < 0 || _size[ch] < p.PuzzleBehindMin || _size[ch] > p.PuzzleBehindMax || _ports[ch] != 0 || _bad[ch] != 0) continue;
                    if (_minY[ch] != c.y || _maxY[ch] != c.y || NearUsed(c, 2)) continue;
                    _valid.Add(k);
                }
                // A few tries, bigger pockets likelier (weight = pocket size).
                for (var attempt = 0; attempt < PuzzleMaxTries && _valid.Length > 0; attempt++)
                {
                    var total = 0;
                    foreach (var k in _valid) total += _size[_cut[g.Index(_candidates[k])]];
                    var roll = rng.NextInt(total);
                    var at = 0;
                    for (; at < _valid.Length - 1; at++)
                    {
                        roll -= _size[_cut[g.Index(_candidates[_valid[at]])]];
                        if (roll < 0) break;
                    }
                    var c = _candidates[_valid[at]];
                    _valid.RemoveAtSwapBack(at);
                    if (TryAt(ref g, ref rng, kind, c, p)) return true;
                }
                return false;
            }

            bool TryAt(ref HoldGrid g, ref Random rng, FixtureKind kind, int3 c, in MineParams p)
            {
                // Pocket = c's separated DFS subtree (contiguous discovery times); open side = the rest that was reached.
                var ci = g.Index(c);
                var ch = _cut[ci];
                int lo = _disc[ch], hi = _disc[ch] + _size[ch];
                for (var i = 0; i < _disc.Length; i++)
                {
                    var d = _disc[i];
                    _pocket[i] = d >= lo && d < hi;
                    _seen[i] = d >= 0 && !_pocket[i] && i != ci;
                }

                // Steps from the door over the open side (fixtures closed).
                Distances(g, ci, _seen);
                var door = kind == FixtureKind.LockedDoor;
                int3 spot;
                byte dir = 0;
                if (door)
                {
                    if (!PickKey(g, ref rng, p.PuzzleKeyMin, p.PuzzleKeyMax, out spot) &&
                        !PickKey(g, ref rng, p.PuzzleKeyMin, int.MaxValue, out spot)) return false;
                }
                else if (!PickLever(g, ref rng, p.PuzzleLeverMin, p.PuzzleLeverMax, out spot, out dir)) return false;

                // Reward spot behind a locked door: the farthest wall cell of the pocket that a strongbox may use.
                var data = 0;
                if (door && PickReward(g, ci, out var reward)) data = (p.PuzzleRewardBonus << 16) | (g.Index(reward) + 1);

                // Commit: the cell becomes a Door (framed between its walls); the pocket and the door are locked.
                for (var i = 0; i < _pocket.Length; i++) if (_pocket[i]) _locked[i] = true;
                _locked[ci] = true;
                _backupIndex.Add(ci);
                _backupCell.Add(g.Cells[ci]);
                var doorCell = g.Cells[ci];
                doorCell.Kind = CellKind.Door;
                g.Cells[ci] = doorCell;
                var link = _link++;
                // Door / gate Dir: the passage axis (0 along x, 2 along z). Lever Dir: the wall it's on.
                g.Fixtures.Add(new HoldFixture { Kind = kind, Cell = c, Dir = (byte)(Tunnel(g, c, 0) ? 0 : 2), Link = link, Data = data });
                g.Fixtures.Add(new HoldFixture { Kind = door ? FixtureKind.Key : FixtureKind.Lever, Cell = spot, Dir = dir, Link = link });
                Use(g, c);
                Use(g, spot);
                return true;
            }

            /// <summary>BFS steps from cell `from` (not entering it again) over cells in `mask`; -1 elsewhere.</summary>
            void Distances(in HoldGrid g, int from, NativeArray<bool> mask)
            {
                for (var i = 0; i < _dist.Length; i++) _dist[i] = -1;
                _queue.Clear();
                _dist[from] = 0;
                _queue.Add(from);
                for (var head = 0; head < _queue.Length; head++)
                {
                    var v = _queue[head];
                    var d = _dist[v] + 1;
                    for (var k = 0; k < _cnt[v]; k++)
                    {
                        var w = _nbr[v * PuzzleMaxMoves + k];
                        if (_dist[w] >= 0 || !mask[w]) continue;
                        _dist[w] = d;
                        _queue.Add(w);
                    }
                }
            }

            int OpenSides(in HoldGrid g, int3 c)
            {
                var n = 0;
                foreach (var s in PuzzleSides) if (g.Get(c + s).Walkable) n++;
                return n;
            }

            static bool Chamber(in HoldGrid g, int3 c)
            {
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                    if (!g.Get(c + new int3(dx, 0, dz)).Walkable) return false;
                return true;
            }

            /// <summary>Key: reachable, keyMin..keyMax steps away (keyMin less if the open side is small); score =
            /// steps + a bonus for dead ends and chamber middles; one of the best quarter at random.</summary>
            bool PickKey(in HoldGrid g, ref Random rng, int keyMin, int keyMax, out int3 spot)
            {
                spot = default;
                _pick.Clear();
                var scores = new NativeList<int>(64, Allocator.Temp);
                var far = 0;
                for (var i = 0; i < _dist.Length; i++) far = math.max(far, _dist[i]);
                var min = math.min(keyMin, math.max(4, far * 2 / 3));
                for (var i = 0; i < _dist.Length; i++)
                {
                    if (_dist[i] < min || _dist[i] > keyMax) continue;
                    var c = Cell(g, i);
                    if (!Spot(g, c)) continue;
                    var bonus = OpenSides(g, c) == 1 ? 12 : Chamber(g, c) ? 6 : 0;
                    _pick.Add(c);
                    scores.Add(_dist[i] + bonus);
                }
                var found = _pick.Length > 0;
                if (found)
                {
                    var top = math.max(1, _pick.Length / 4);
                    for (var k = 0; k < top; k++)
                    {
                        var best = k;
                        for (var j = k + 1; j < _pick.Length; j++) if (scores[j] > scores[best]) best = j;
                        (_pick[k], _pick[best]) = (_pick[best], _pick[k]);
                        (scores[k], scores[best]) = (scores[best], scores[k]);
                    }
                    spot = _pick[rng.NextInt(top)];
                }
                scores.Dispose();
                return found;
            }

            /// <summary>Lever: a reachable cell leverMin..leverMax steps away with rock on a side (its wall).</summary>
            bool PickLever(in HoldGrid g, ref Random rng, int leverMin, int leverMax, out int3 spot, out byte dir)
            {
                spot = default;
                dir = 0;
                for (var round = 0; round < 2; round++)
                {
                    var hi = round == 0 ? leverMax : leverMax * 3;
                    _pick.Clear();
                    for (var i = 0; i < _dist.Length; i++)
                    {
                        if (_dist[i] < leverMin || _dist[i] > hi) continue;
                        var c = Cell(g, i);
                        if (!Spot(g, c)) continue;
                        var walled = false;
                        foreach (var s in PuzzleSides) walled |= Solid(g, c + s);
                        if (walled) _pick.Add(c);
                    }
                    if (_pick.Length == 0) continue;
                    spot = _pick[rng.NextInt(_pick.Length)];
                    var start = rng.NextInt(4);
                    for (var k = 0; k < 4; k++)
                    {
                        var d = (start + k) & 3;
                        if (!Solid(g, spot + PuzzleSides[d])) continue;
                        dir = (byte)d;
                        break;
                    }
                    return true;
                }
                return false;
            }

            /// <summary>Farthest pocket cell from the door with rock on a side, clear of shafts and the margin.</summary>
            bool PickReward(in HoldGrid g, int door, out int3 spot)
            {
                spot = default;
                Distances(g, door, _pocket);
                var best = 0;
                for (var i = 0; i < _dist.Length; i++)
                {
                    if (_dist[i] <= best) continue;
                    var c = Cell(g, i);
                    if (!PlainMine(g.Cells[i]) || !Inner(g, c) || !ClearOfShafts(g, c) || OpenSides(g, c) >= 4) continue;
                    best = _dist[i];
                    spot = c;
                }
                return best > 0;
            }

            /// <summary>The solvability contract over the finished region (see MinePasses; moves taken as symmetric).</summary>
            public bool Valid(in HoldGrid g)
            {
                if (g.Fixtures.Length == _fixturesBefore) return true;
                if (HoldNav.Flood(g, Ports[0], false, _seen) != _openBefore) return false;
                HoldNav.Flood(g, Ports[0], true, _seen);
                foreach (var port in Ports) if (!_seen[g.Index(port)]) return false;
                for (var i = _fixturesBefore; i < g.Fixtures.Length; i++)
                {
                    var f = g.Fixtures[i];
                    if ((f.Kind == FixtureKind.Key || f.Kind == FixtureKind.Lever) && !_seen[g.Index(f.Cell)]) return false;
                }
                return true;
            }

            public void Revert(ref HoldGrid g)
            {
                for (var i = 0; i < _backupIndex.Length; i++) g.Cells[_backupIndex[i]] = _backupCell[i];
                g.Fixtures.Length = _fixturesBefore;
                UnityEngine.Debug.LogWarning($"[MinePasses] puzzles in region at {g.Origin}: failed validation, removed");
            }
        }
    }
}
