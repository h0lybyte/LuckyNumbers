using System;
using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// An ASCII station plan (StationScene.plan) -> kit pieces to place and boxes to collide with. Pure layout,
    /// no rendering: StationView merges the pieces into meshes. Built from the PSX starship kit (cream panels).
    ///
    /// Grid (both PSX kits): 2 m cells, 3 m high. Cell (x, z) has its centre at (2x + 1, 0, 2z + 1); row 0 of the
    /// plan is the north edge (+Z). A wall stands on a cell's edge, front facing into the cell: the kit's walls
    /// face their local -Z, so a wall on the edge towards direction d is turned with LookRotation(d).
    /// </summary>
    public sealed class StationPlan
    {
        public const float Cell = 2f;
        public const float Height = 3f;

        public enum Kind { Solid, Room, Corridor, Doorway, Window }

        /// <summary>Which kit a piece comes from: each kit is one atlas, so one material and one mesh per chunk.</summary>
        public enum Kit { Station, Starship }

        public readonly struct Piece
        {
            public readonly string Name;
            public readonly Kit Kit;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly Vector2Int Cell;
            /// <summary>Its painted lights stay dark in every mode (e.g. every other room ceiling).</summary>
            public readonly bool Unlit;

            public Piece(string name, Kit kit, Vector3 position, Quaternion rotation, Vector2Int cell, bool unlit = false)
            {
                Name = name;
                Kit = kit;
                Position = position;
                Rotation = rotation;
                Cell = cell;
                Unlit = unlit;
            }
        }

        /// <summary>
        /// A convex corridor corner (the inside of a turn, or either side of a T): two leaning walls end at
        /// <see cref="Vertex"/> without meeting. A leans towards <see cref="NormalA"/> and would carry on past the
        /// corner along <see cref="NormalB"/> (and B the other way round); StationView fills the wedge between them.
        /// </summary>
        public readonly struct Hip
        {
            public readonly Vector3 Vertex, NormalA, NormalB;
            public readonly Vector2Int Cell;
            public Hip(Vector3 vertex, Vector3 a, Vector3 b, Vector2Int cell) { Vertex = vertex; NormalA = a; NormalB = b; Cell = cell; }
        }

        public readonly struct Box
        {
            public readonly Vector3 Center, Size;
            public Box(Vector3 center, Vector3 size) { Center = center; Size = size; }
        }

        // Piece names = the kits' FBX file names. Everything is the starship kit's cream panels: corridors lean in,
        // rooms use the same walls with the lean pushed upright (Flat: StationView derives those meshes).
        public const string Flat = "@flat";
        /// <summary>The same wall with its lean pushed fully upright: lower storeys of tall rooms.</summary>
        public const string Upright = "@upright";
        public const string FloorDeck = "floor_deck", Ceiling = "ceiling_corridor";
        public const string WallCorridor = "wall_corridor", WallCorridorLights = "wall_corridor_lights";
        public const string Porthole = "wall_corridor_porthole", Rib = "corridor_rib";
        public const string WallRoom = WallCorridor + Flat, WallRoomLights = WallCorridorLights + Flat;
        public const string PortholeRoom = Porthole + Flat;
        public const string Post = "post_ship", DoorwayShip = "wall_doorway_ship";

        /// <summary>Doorway opening (wall_doorway_ship, fits door_ship: 1.2 x 2.19 m).</summary>
        const float DoorWidth = 1.2f, DoorHeight = 2.19f, WallThickness = 0.2f;

        static readonly Vector2Int[] Dirs = { new(0, 1), new(1, 0), new(0, -1), new(-1, 0) }; // N, E, S, W

        public readonly int Width, Depth;
        /// <summary>The 'P' cell (player start), or the first room cell.</summary>
        public Vector2Int Spawn { get; private set; } = new(-1, -1);
        /// <summary>'E' cells: walking onto one leaves the station.</summary>
        public readonly List<Vector2Int> Exits = new();
        public readonly List<Piece> Pieces = new();
        public readonly List<Box> Boxes = new();
        public readonly List<Hip> Hips = new();
        readonly Kind[,] _cells;

        readonly int _ceilingEvery;
        int[,] _levels;   // storeys per cell (1, or more in tall rooms)

        /// <summary>Storeys (3 m each) over a cell.</summary>
        public int Levels(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < Width && c.y < Depth ? _levels[c.x, c.y] : 1;

        /// <param name="ceilingLightEvery">Rooms light every nth row of ceiling tiles (corridors: every tile).</param>
        /// <param name="tallRoomCells">Rooms with at least this many cells get <paramref name="tallLevels"/> storeys.</param>
        public StationPlan(IReadOnlyList<string> rows, int lightEvery, int ceilingLightEvery = 1, int tallRoomCells = 0,
                           int tallLevels = 1)
        {
            _ceilingEvery = Math.Max(1, ceilingLightEvery);
            Depth = rows.Count;
            foreach (var r in rows) Width = Math.Max(Width, r.Length);
            _cells = new Kind[Width, Depth];
            for (var row = 0; row < Depth; row++)
            for (var x = 0; x < Width; x++)
            {
                var ch = x < rows[row].Length ? rows[row][x] : '#';
                var cell = new Vector2Int(x, Depth - 1 - row);
                _cells[cell.x, cell.y] = Parse(ch);
                if (ch == 'P') Spawn = cell;
                if (ch == 'E') Exits.Add(cell);
            }
            if (Spawn.x < 0)
                for (var z = Depth - 1; z >= 0 && Spawn.x < 0; z--)
                for (var x = 0; x < Width && Spawn.x < 0; x++)
                    if (_cells[x, z] == Kind.Room) Spawn = new Vector2Int(x, z);

            _levels = new int[Width, Depth];
            for (var z = 0; z < Depth; z++)
            for (var x = 0; x < Width; x++)
                _levels[x, z] = 1;
            if (tallRoomCells > 0 && tallLevels > 1)
                foreach (var room in Rooms())
                    if (room.Count >= tallRoomCells)
                        foreach (var c in room) _levels[c.x, c.y] = tallLevels;

            var corners = new Dictionary<Vector2Int, int>();   // grid vertex (in metres) -> axes of room walls meeting there
            var cornerLevels = new Dictionary<Vector2Int, int>();
            for (var z = 0; z < Depth; z++)
            for (var x = 0; x < Width; x++)
            {
                var kind = _cells[x, z];
                if (!Walkable(kind)) continue;
                var cell = new Vector2Int(x, z);
                var centre = Centre(cell);
                Floor(kind, cell, centre);
                if (kind == Kind.Doorway) Doorway(cell, centre);

                for (var d = 0; d < 4; d++)
                {
                    var dir = Dirs[d];
                    var next = At(cell + dir);
                    var edge = centre + new Vector3(dir.x, 0, dir.y) * (Cell / 2f);
                    var turn = Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y));
                    var levels = Levels(cell);
                    if (Walkable(next))
                    {
                        // Ribs across a corridor between two of its cells (once per edge: north/east side only).
                        if (kind == Kind.Corridor && next == Kind.Corridor && d < 2)
                            Pieces.Add(new Piece(Rib, Kit.Starship, edge, turn, cell));
                        // A tall room over a lower neighbour (its doorway): close the wall above the opening.
                        for (var l = Levels(cell + dir); l < levels; l++)
                            Pieces.Add(new Piece(l == levels - 1 ? WallRoom : WallRoom.Replace(Flat, Upright), Kit.Starship,
                                                 edge + Vector3.up * (Height * l), turn, cell));
                        continue;
                    }

                    var lit = lightEvery > 0 && (x + z + d) % lightEvery == 0;
                    string name;
                    var kit = Kit.Starship;
                    if (kind == Kind.Corridor)
                    {
                        name = next == Kind.Window ? Porthole : lit ? WallCorridorLights : WallCorridor;
                    }
                    else if (levels > 1)
                    {
                        // Tall room: windows and upright walls below, the room's leaning wall on the top storey.
                        for (var l = 1; l < levels; l++)
                        {
                            var top = l == levels - 1;
                            var upper = top ? (lit ? WallRoomLights : WallRoom) : WallCorridor + Upright;
                            Pieces.Add(new Piece(upper, Kit.Starship, edge + Vector3.up * (Height * l), turn, cell));
                        }
                        name = next == Kind.Window ? Porthole + Upright : WallCorridor + Upright;
                    }
                    else
                    {
                        name = next == Kind.Window && kind == Kind.Room ? PortholeRoom : lit ? WallRoomLights : WallRoom;
                    }
                    if (kind != Kind.Corridor)
                    {
                        // Posts go where room walls turn a corner: note both ends of this wall, its axis and height.
                        var axis = dir.x != 0 ? 1 : 2;   // wall runs along Z (1) or X (2)
                        var side = new Vector2Int(dir.y, -dir.x);   // along the wall
                        var mid = new Vector2Int(Mathf.RoundToInt(edge.x), Mathf.RoundToInt(edge.z));
                        foreach (var end in new[] { mid + side, mid - side })
                        {
                            corners[end] = corners.TryGetValue(end, out var a) ? a | axis : axis;
                            cornerLevels[end] = Math.Max(levels, cornerLevels.TryGetValue(end, out var lv) ? lv : 1);
                        }
                    }
                    Pieces.Add(new Piece(name, kit, edge, turn, cell));
                    WallBox(edge, dir);
                }
            }

            // Deck: one slab under the whole plan (walls keep you inside), not one per cell.
            var span = new Vector3(Width * Cell, 0.1f, Depth * Cell);
            var middle = new Vector3(Width * Cell / 2f, 0f, Depth * Cell / 2f);
            Boxes.Add(new Box(middle - Vector3.up * 0.05f, span));   // (no ceiling slab: a jump never reaches 3 m)

            // Convex corridor corners: a grid vertex with corridor on three sides and something solid on the fourth.
            // The two walls round the solid cell end there, leaning away from each other: fill the gap (and post it).
            for (var vz = 1; vz < Depth; vz++)
            for (var vx = 1; vx < Width; vx++)
            {
                Vector2Int sw = new(vx - 1, vz - 1), se = new(vx, vz - 1), nw = new(vx - 1, vz), ne = new(vx, vz);
                var around = new[] { sw, se, ne, nw };   // counter-clockwise
                var solid = -1;
                var corridors = 0;
                for (var i = 0; i < 4; i++)
                {
                    var k = At(around[i]);
                    if (k == Kind.Corridor) corridors++;
                    else if (!Walkable(k)) solid = i;
                }
                if (corridors != 3 || solid < 0) continue;
                var s = around[solid];
                var c1 = around[(solid + 1) % 4];   // the solid cell's two corridor neighbours (by an edge)
                var c2 = around[(solid + 3) % 4];
                var a = new Vector3(c1.x - s.x, 0, c1.y - s.y);
                var b = new Vector3(c2.x - s.x, 0, c2.y - s.y);
                var v = new Vector3(vx * Cell, 0, vz * Cell);
                Hips.Add(new Hip(v, a, b, around[(solid + 2) % 4]));
                Pieces.Add(new Piece(Post, Kit.Starship, v, Quaternion.identity, around[(solid + 2) % 4]));
            }

            foreach (var (vertex, axes) in corners)
            {
                if (axes != 3) continue;   // a straight run: the walls meet flush, no post
                var p = new Vector3(vertex.x, 0, vertex.y);
                for (var l = 0; l < cornerLevels[vertex]; l++)
                    Pieces.Add(new Piece(Post, Kit.Starship, p + Vector3.up * (Height * l), Quaternion.identity, CellOf(p)));
            }
        }

        /// <summary>Rooms: connected room cells (doorways and corridors separate them), largest first.</summary>
        public List<List<Vector2Int>> Rooms()
        {
            var rooms = new List<List<Vector2Int>>();
            var seen = new bool[Width, Depth];
            for (var z = 0; z < Depth; z++)
            for (var x = 0; x < Width; x++)
            {
                if (seen[x, z] || _cells[x, z] != Kind.Room) continue;
                var room = new List<Vector2Int>();
                var todo = new Stack<Vector2Int>();
                todo.Push(new Vector2Int(x, z));
                seen[x, z] = true;
                while (todo.Count > 0)
                {
                    var c = todo.Pop();
                    room.Add(c);
                    foreach (var d in Dirs)
                    {
                        var n = c + d;
                        if (At(n) != Kind.Room || seen[n.x, n.y]) continue;
                        seen[n.x, n.y] = true;
                        todo.Push(n);
                    }
                }
                rooms.Add(room);
            }
            rooms.Sort((a, b) => b.Count.CompareTo(a.Count));
            return rooms;
        }

        /// <summary>The longest straight run of corridor cells (its two end cells).</summary>
        public (Vector2Int from, Vector2Int to) LongestCorridor()
        {
            var best = (from: Vector2Int.zero, to: Vector2Int.zero);
            var bestLength = -1;
            foreach (var step in new[] { Dirs[1], Dirs[0] })
                for (var z = 0; z < Depth; z++)
                for (var x = 0; x < Width; x++)
                {
                    var c = new Vector2Int(x, z);
                    if (At(c) != Kind.Corridor || At(c - step) == Kind.Corridor) continue;   // runs start here
                    var end = c;
                    while (At(end + step) == Kind.Corridor) end += step;
                    var length = Mathf.Abs(end.x - c.x) + Mathf.Abs(end.y - c.y);
                    if (length > bestLength) { bestLength = length; best = (c, end); }
                }
            return best;
        }

        public Kind At(Vector2Int c) =>
            c.x >= 0 && c.y >= 0 && c.x < Width && c.y < Depth ? _cells[c.x, c.y] : Kind.Solid;

        public static Vector3 Centre(Vector2Int cell) => new((cell.x + 0.5f) * Cell, 0, (cell.y + 0.5f) * Cell);

        public static bool Walkable(Kind k) => k == Kind.Room || k == Kind.Corridor || k == Kind.Doorway;

        public static Vector2Int CellOf(Vector3 p) => new(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));

        static Kind Parse(char c) => c switch
        {
            '.' or 'P' or 'E' => Kind.Room,
            'c' => Kind.Corridor,
            'D' => Kind.Doorway,
            'W' => Kind.Window,
            _ => Kind.Solid,
        };

        void Floor(Kind kind, Vector2Int cell, Vector3 centre)
        {
            var up = centre + Vector3.up * Height;
            Pieces.Add(new Piece(FloorDeck, Kit.Starship, centre, Quaternion.identity, cell));
            // The ceiling's light strip runs along a corridor (turned with the way through); rooms keep one
            // direction so their strips line up in rows.
            var alongX = kind != Kind.Corridor || Walkable(At(cell + Dirs[1])) || Walkable(At(cell + Dirs[3]));
            var unlit = kind != Kind.Corridor && cell.y % _ceilingEvery != 0;
            var raise = Vector3.up * (Height * (Levels(cell) - 1));   // the ceiling piece is modelled at 3 m
            Pieces.Add(new Piece(Ceiling, Kit.Starship, centre + raise, alongX ? Quaternion.identity : Quaternion.Euler(0, 90, 0),
                                 cell, unlit));
        }

        /// <summary>The doorway stands across the cell's middle, facing along the way through (N-S or E-W).</summary>
        void Doorway(Vector2Int cell, Vector3 centre)
        {
            var northSouth = Walkable(At(cell + Dirs[0])) || Walkable(At(cell + Dirs[2]));
            var facing = northSouth ? Vector3.forward : Vector3.right;
            Pieces.Add(new Piece(DoorwayShip, Kit.Starship, centre, Quaternion.LookRotation(facing), cell));

            // Jambs either side of the opening and a lintel over it.
            var across = northSouth ? Vector3.right : Vector3.forward;
            var jamb = (Cell - DoorWidth) / 2f;
            foreach (var s in new[] { -1f, 1f })
            {
                var c = centre + across * (s * (DoorWidth + jamb) / 2f) + Vector3.up * (Height / 2f);
                Boxes.Add(new Box(c, Size(across, jamb, Height, facing, WallThickness)));
            }
            var lintel = centre + Vector3.up * ((DoorHeight + Height) / 2f);
            Boxes.Add(new Box(lintel, Size(across, DoorWidth, Height - DoorHeight, facing, WallThickness)));
        }

        void WallBox(Vector3 edge, Vector2Int dir)
        {
            var normal = new Vector3(dir.x, 0, dir.y);
            var along = new Vector3(dir.y, 0, -dir.x);
            Boxes.Add(new Box(edge + Vector3.up * (Height / 2f), Size(along, Cell, Height, normal, WallThickness)));
        }

        static Vector3 Size(Vector3 along, float length, float height, Vector3 normal, float thickness)
        {
            var s = Abs(along) * length + Abs(normal) * thickness;
            return new Vector3(s.x, height, s.z);
        }

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
