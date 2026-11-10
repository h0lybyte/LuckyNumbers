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

        public readonly struct Box
        {
            public readonly Vector3 Center, Size;
            public Box(Vector3 center, Vector3 size) { Center = center; Size = size; }
        }

        // Piece names = the kits' FBX file names. Everything is the starship kit's cream panels: corridors lean in,
        // rooms use the same walls with the lean pushed upright (Flat: StationView derives those meshes).
        public const string Flat = "@flat";
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
        public readonly List<Piece> Pieces = new();
        public readonly List<Box> Boxes = new();
        readonly Kind[,] _cells;

        readonly int _ceilingEvery;

        /// <param name="ceilingLightEvery">Rooms light every nth row of ceiling tiles (corridors: every tile).</param>
        public StationPlan(IReadOnlyList<string> rows, int lightEvery, int ceilingLightEvery = 1)
        {
            _ceilingEvery = Math.Max(1, ceilingLightEvery);
            Depth = rows.Count;
            foreach (var r in rows) Width = Math.Max(Width, r.Length);
            _cells = new Kind[Width, Depth];
            for (var row = 0; row < Depth; row++)
            for (var x = 0; x < Width; x++)
                _cells[x, Depth - 1 - row] = x < rows[row].Length ? Parse(rows[row][x]) : Kind.Solid;

            var corners = new Dictionary<Vector2Int, int>();   // grid vertex (in metres) -> axes of room walls meeting there
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
                    if (Walkable(next))
                    {
                        // Ribs across a corridor between two of its cells (once per edge: north/east side only).
                        if (kind == Kind.Corridor && next == Kind.Corridor && d < 2)
                            Pieces.Add(new Piece(Rib, Kit.Starship, edge, turn, cell));
                        continue;
                    }

                    var lit = lightEvery > 0 && (x + z + d) % lightEvery == 0;
                    string name;
                    var kit = Kit.Starship;
                    if (kind == Kind.Corridor)
                    {
                        name = next == Kind.Window ? Porthole : lit ? WallCorridorLights : WallCorridor;
                    }
                    else
                    {
                        name = next == Kind.Window && kind == Kind.Room ? PortholeRoom : lit ? WallRoomLights : WallRoom;
                        // Posts go where room walls turn a corner: note both ends of this wall and its axis.
                        var axis = dir.x != 0 ? 1 : 2;   // wall runs along Z (1) or X (2)
                        var side = new Vector2Int(dir.y, -dir.x);   // along the wall
                        var mid = new Vector2Int(Mathf.RoundToInt(edge.x), Mathf.RoundToInt(edge.z));
                        foreach (var end in new[] { mid + side, mid - side })
                            corners[end] = corners.TryGetValue(end, out var a) ? a | axis : axis;
                    }
                    Pieces.Add(new Piece(name, kit, edge, turn, cell));
                    WallBox(edge, dir);
                }
            }

            foreach (var (vertex, axes) in corners)
            {
                if (axes != 3) continue;   // a straight run: the walls meet flush, no post
                var p = new Vector3(vertex.x, 0, vertex.y);
                Pieces.Add(new Piece(Post, Kit.Starship, p, Quaternion.identity, CellOf(p)));
            }
        }

        public Kind At(Vector2Int c) =>
            c.x >= 0 && c.y >= 0 && c.x < Width && c.y < Depth ? _cells[c.x, c.y] : Kind.Solid;

        public static Vector3 Centre(Vector2Int cell) => new((cell.x + 0.5f) * Cell, 0, (cell.y + 0.5f) * Cell);

        public static bool Walkable(Kind k) => k == Kind.Room || k == Kind.Corridor || k == Kind.Doorway;

        static Vector2Int CellOf(Vector3 p) => new(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));

        static Kind Parse(char c) => c switch
        {
            '.' => Kind.Room,
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
            Pieces.Add(new Piece(Ceiling, Kit.Starship, centre, alongX ? Quaternion.identity : Quaternion.Euler(0, 90, 0),
                                 cell, unlit));
            Boxes.Add(new Box(centre - Vector3.up * 0.05f, new Vector3(Cell, 0.1f, Cell)));
            Boxes.Add(new Box(up + Vector3.up * 0.05f, new Vector3(Cell, 0.1f, Cell)));
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
