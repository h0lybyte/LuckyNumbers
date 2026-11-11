using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Seeded station layouts (StationScene.generator): a hub in the middle, the gallery north of it (towards the
    /// planet), the docking bay west, comms east (with the shooting range north of it), and a corridor south of it with
    /// storage, the cabins and engineering hanging off it. Sizes, offsets and corridor lengths come from the lucky number; each room joins
    /// its corridor through a doorway; windows go on walls that face away from the hub. Every layout is checked
    /// (doorways between two opposite walkable cells, corridors touching rooms only through doorways, windows on a
    /// room, nothing walkable on the border, everything reachable) and re-rolled until one passes.
    /// Plan coordinates throughout: x = column (west to east), y = row (north to south).
    /// </summary>
    public static class LayoutGenerator
    {
        const int MaxAttempts = 60;

        sealed class Room
        {
            public StationRoomRule Rule;
            public string Name;
            public RectInt Rect;   // x, y = top-left cell (plan coordinates)
            public bool Yours;
        }

        public static StationLayout Generate(StationScene config, uint seed)
        {
            var rules = config.Generator;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var rng = new Random((seed ^ (0x9E3779B9u * (uint)(attempt + 1))) | 1u);
                if (TryLayout(config, rules, ref rng, out var layout, out var why))
                {
                    layout.Seed = seed;
                    Debug.Log($"[LayoutGenerator] seed {seed}: {layout.Plan[0].Length}x{layout.Plan.Count} cells, " +
                              $"{layout.Rooms.Count} rooms (attempt {attempt + 1})");
                    return layout;
                }
                if (attempt == MaxAttempts - 1) Debug.LogWarning($"[LayoutGenerator] seed {seed}: gave up ({why}); hand plan");
            }
            return StationLayout.FromContent(config);
        }

        static bool TryLayout(StationScene config, StationGenerator rules, ref Random seeded, out StationLayout layout,
                              out string why)
        {
            layout = null;
            var rng = seeded;   // a local: local functions below draw from it (they can't capture a ref parameter)
            StationRoomRule Rule(StationRoomRole role)
            {
                foreach (var r in rules.Rooms) if (r.Role == role) return r;
                return null;
            }
            int Between(ref Random r, int min, int max) => r.NextInt(Math.Min(min, max), Math.Max(min, max) + 1);
            var minC = Math.Max(1, rules.MinCorridor);
            var maxC = Math.Max(minC, rules.MaxCorridor);

            var cells = new Dictionary<Vector2Int, char>();   // everything that isn't solid
            var rooms = new List<Room>();
            void Paint(Vector2Int c, char ch) => cells[c] = ch;
            Room AddRoom(StationRoomRule rule, string name, RectInt rect)
            {
                var room = new Room { Rule = rule, Name = name, Rect = rect };
                for (var y = rect.yMin; y < rect.yMax; y++)
                for (var x = rect.xMin; x < rect.xMax; x++)
                    Paint(new Vector2Int(x, y), '.');
                rooms.Add(room);
                return room;
            }
            RectInt Size(ref Random r, StationRoomRule rule) =>
                new(0, 0, Between(ref r, rule.MinWidth, rule.MaxWidth), Between(ref r, rule.MinDepth, rule.MaxDepth));

            // Hub at the origin.
            var hubRule = Rule(StationRoomRole.Hub);
            if (hubRule == null) { why = "no hub rule"; return false; }
            var hubSize = Size(ref rng, hubRule);
            var hub = AddRoom(hubRule, hubRule.Name, new RectInt(0, 0, hubSize.width, hubSize.height));
            var h = hub.Rect;

            // North: the gallery, through a corridor from the hub's north wall.
            var galleryRule = Rule(StationRoomRole.Gallery);
            if (galleryRule != null)
            {
                var size = Size(ref rng, galleryRule);
                var x = Between(ref rng, h.xMin + 1, h.xMax - 2);
                var length = Between(ref rng, minC, maxC);
                Paint(new Vector2Int(x, h.yMin - 1), 'D');
                for (var i = 0; i < length; i++) Paint(new Vector2Int(x, h.yMin - 2 - i), 'c');
                var door = h.yMin - 2 - length;
                Paint(new Vector2Int(x, door), 'D');
                var x0 = x - Between(ref rng, 1, size.width - 2);
                AddRoom(galleryRule, galleryRule.Name, new RectInt(x0, door - size.height, size.width, size.height));
            }

            // West / east: the bay and comms, through corridors from the hub's side walls.
            Room Side(StationRoomRule rule, int dir)
            {
                if (rule == null) return null;
                var size = Size(ref rng, rule);
                var y = Between(ref rng, h.yMin + 1, h.yMax - 2);
                var length = Between(ref rng, minC, maxC);
                var wall = dir < 0 ? h.xMin - 1 : h.xMax;
                Paint(new Vector2Int(wall, y), 'D');
                for (var i = 1; i <= length; i++) Paint(new Vector2Int(wall + dir * i, y), 'c');
                var door = wall + dir * (length + 1);
                Paint(new Vector2Int(door, y), 'D');
                var y0 = y - Between(ref rng, 1, size.height - 2);
                var x0 = dir < 0 ? door - size.width : door + 1;
                return AddRoom(rule, rule.Name, new RectInt(x0, y0, size.width, size.height));
            }
            Side(Rule(StationRoomRole.Bay), -1);
            var comms = Side(Rule(StationRoomRole.Comms), +1);

            // North of comms: the shooting range, long north-south, its door in comms' north wall through a corridor
            // (door and bench at its south end, targets at the north).
            var rangeRule = Rule(StationRoomRole.Range);
            if (rangeRule != null && comms != null)
            {
                var c = comms.Rect;
                var size = Size(ref rng, rangeRule);
                var x = Between(ref rng, c.xMin + 1, c.xMax - 2);
                var length = Between(ref rng, minC, maxC);
                Paint(new Vector2Int(x, c.yMin - 1), 'D');
                for (var i = 0; i < length; i++) Paint(new Vector2Int(x, c.yMin - 2 - i), 'c');
                var door = c.yMin - 2 - length;
                Paint(new Vector2Int(x, door), 'D');
                var x0 = x - Between(ref rng, 1, size.width - 2);
                AddRoom(rangeRule, rangeRule.Name, new RectInt(x0, door - size.height, size.width, size.height));
            }

            // South: a corridor down from the hub, then a spine running west-east with rooms hanging below it.
            var below = new List<(StationRoomRule rule, string name, bool yours)>();
            var storage = Rule(StationRoomRole.Storage);
            var cabin = Rule(StationRoomRole.Cabin);
            var engineering = Rule(StationRoomRole.Engineering);
            if (storage != null) below.Add((storage, storage.Name, false));
            if (cabin != null)
            {
                var count = Math.Max(1, cabin.Count);
                var yours = count / 2;
                for (var i = 0; i < count; i++)
                {
                    var name = i == yours && !string.IsNullOrEmpty(cabin.YourName) ? cabin.YourName : $"{cabin.Name} {(char)('A' + i)}";
                    below.Add((cabin, name, i == yours));
                }
            }
            if (engineering != null) below.Add((engineering, engineering.Name, false));
            if (below.Count > 0)
            {
                var sx = Between(ref rng, h.xMin + 1, h.xMax - 2);
                var drop = Between(ref rng, minC, maxC);
                Paint(new Vector2Int(sx, h.yMax), 'D');
                for (var i = 1; i <= drop; i++) Paint(new Vector2Int(sx, h.yMax + i), 'c');
                var spine = h.yMax + drop + 1;
                // Lay the rooms out west to east, one wall (or two) apart, centred on the drop corridor.
                var sizes = new List<RectInt>();
                var total = 0;
                foreach (var b in below)
                {
                    var s = Size(ref rng, b.rule);
                    sizes.Add(s);
                    total += s.width;
                }
                var gaps = new int[below.Count];
                for (var i = 1; i < gaps.Length; i++) { gaps[i] = Between(ref rng, 1, 2); total += gaps[i]; }
                var x = sx - total / 2;
                var doors = new List<int>();
                for (var i = 0; i < below.Count; i++)
                {
                    x += gaps[i];
                    var s = sizes[i];
                    var doorX = x + Between(ref rng, 1, s.width - 2);
                    Paint(new Vector2Int(doorX, spine + 1), 'D');
                    var room = AddRoom(below[i].rule, below[i].name, new RectInt(x, spine + 2, s.width, s.height));
                    room.Yours = below[i].yours;
                    doors.Add(doorX);
                    x += s.width;
                }
                var from = Math.Min(sx, doors[0]);
                var to = Math.Max(sx, doors[doors.Count - 1]);
                for (var cx = from; cx <= to; cx++) Paint(new Vector2Int(cx, spine), 'c');
            }

            // Windows on the walls facing away from the hub (every other cell; doors stay doors).
            foreach (var room in rooms)
            {
                if (!room.Rule.Windows) continue;
                var r = room.Rect;
                IEnumerable<Vector2Int> Wall() => room.Rule.Role switch
                {
                    StationRoomRole.Gallery => Row(r.xMin, r.xMax, r.yMin - 1),
                    StationRoomRole.Bay => Concat(Row(r.xMin, r.xMax, r.yMin - 1), Column(r.xMin - 1, r.yMin, r.yMax)),
                    StationRoomRole.Comms or StationRoomRole.Engineering => Column(r.xMax, r.yMin, r.yMax),
                    StationRoomRole.Cabin or StationRoomRole.Storage => Row(r.xMin, r.xMax, r.yMax),
                    _ => Array.Empty<Vector2Int>(),
                };
                var i = 0;
                foreach (var c in Wall())
                    if (i++ % 2 == 1 && !cells.ContainsKey(c)) Paint(c, 'W');
            }

            // Overlaps: a room or corridor painted over another shows up as a broken rule below.
            if (rooms.Exists(a => rooms.Exists(b => a != b && Grow(a.Rect).Overlaps(b.Rect))))
            {
                why = "rooms too close";
                return false;
            }

            // Into a grid, with a solid border.
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in cells.Keys)
            {
                minX = Math.Min(minX, c.x); minY = Math.Min(minY, c.y);
                maxX = Math.Max(maxX, c.x); maxY = Math.Max(maxY, c.y);
            }
            var ox = 2 - minX;   // a wall ring around the walls
            var oy = 2 - minY;
            var width = maxX - minX + 5;
            var depth = maxY - minY + 5;
            var grid = new char[depth][];
            for (var y = 0; y < depth; y++) grid[y] = new string('#', width).ToCharArray();
            foreach (var (c, ch) in cells) grid[c.y + oy][c.x + ox] = ch;
            var yourRoom = rooms.Find(r => r.Yours) ?? hub;
            var spawn = new Vector2Int(yourRoom.Rect.xMin + yourRoom.Rect.width / 2 + ox, yourRoom.Rect.yMin + yourRoom.Rect.height / 2 + oy);
            grid[spawn.y][spawn.x] = 'P';

            var plan = new List<string>();
            foreach (var row in grid) plan.Add(new string(row));
            why = Check(plan);
            if (why != null) return false;

            // Names, props, consoles and the dock, placed across each room (u, v -> plan coordinates).
            layout = new StationLayout { CallRoom = hub.Name };
            layout.Plan.AddRange(plan);
            foreach (var room in rooms)
            {
                var r = room.Rect;
                layout.Rooms.Add(new StationRoom { Name = room.Name, Col = r.xMin + r.width / 2 + ox, Row = r.yMin + r.height / 2 + oy });
                foreach (var item in room.Rule.Items)
                {
                    var col = r.xMin + ox + Across(item.U, r.width);
                    var row = r.yMin + oy + Across(item.V, r.height);
                    if (item.Action != StationAction.Unspecified)
                        layout.Switches.Add(new StationSwitch
                            { Col = col, Row = row, Action = item.Action, Prompt = item.Prompt, Piece = item.Piece, Yaw = item.Yaw });
                    else
                        layout.Props.Add(new StationProp { Piece = item.Piece, Col = col, Row = row, Yaw = item.Yaw, Solid = item.Solid });
                }
                if (room.Rule.Role == StationRoomRole.Bay && config.Dock != null)
                    layout.Dock = new StationDock
                    {
                        Col = r.xMin + ox + (r.width - 1) / 2f, Row = r.yMin + oy + (r.height - 1) / 2f, Yaw = 0f,
                        Length = config.Dock.Length, BoardDistance = config.Dock.BoardDistance, BoardPrompt = config.Dock.BoardPrompt,
                    };
            }
            return true;
        }

        /// <summary>0..1 = first..last cell centre across the room; beyond that, cells past it (towards the wall).</summary>
        static float Across(float t, int cells) =>
            t < 0f ? t : t > 1f ? (cells - 1) + (t - 1f) : t * (cells - 1);

        static RectInt Grow(RectInt r) => new(r.xMin - 1, r.yMin - 1, r.width + 2, r.height + 2);
        static IEnumerable<Vector2Int> Row(int x0, int x1, int y) { for (var x = x0; x < x1; x++) yield return new Vector2Int(x, y); }
        static IEnumerable<Vector2Int> Column(int x, int y0, int y1) { for (var y = y0; y < y1; y++) yield return new Vector2Int(x, y); }
        static IEnumerable<Vector2Int> Concat(IEnumerable<Vector2Int> a, IEnumerable<Vector2Int> b) { foreach (var c in a) yield return c; foreach (var c in b) yield return c; }

        /// <summary>The plan's rules (null = fine, else what's wrong). Same checks as the hand-drawn plans get.</summary>
        public static string Check(IReadOnlyList<string> plan)
        {
            var h = plan.Count;
            var w = plan.Count > 0 ? plan[0].Length : 0;
            char At(int x, int y) => x >= 0 && y >= 0 && y < h && x < plan[y].Length ? plan[y][x] : '#';
            bool Walk(char c) => c == '.' || c == 'c' || c == 'D' || c == 'P' || c == 'E';
            bool RoomLike(char c) => c == '.' || c == 'P' || c == 'E';
            var total = 0;
            var start = new Vector2Int(-1, -1);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = At(x, y);
                if (Walk(c)) total++;
                if (c == 'P') start = new Vector2Int(x, y);
                char n = At(x, y - 1), s = At(x, y + 1), e = At(x + 1, y), wv = At(x - 1, y);
                if (c == 'D' && !(Walk(n) && Walk(s) && !Walk(e) && !Walk(wv)) && !(Walk(e) && Walk(wv) && !Walk(n) && !Walk(s)))
                    return $"door {x},{y} isn't between two opposite walkable cells";
                if (c == 'c' && (RoomLike(n) || RoomLike(s) || RoomLike(e) || RoomLike(wv))) return $"corridor {x},{y} touches a room";
                if (c == 'W' && !(Walk(n) || Walk(s) || Walk(e) || Walk(wv))) return $"window {x},{y} faces nothing";
                if (Walk(c) && (x == 0 || y == 0 || x == w - 1 || y == h - 1)) return $"walkable cell {x},{y} on the border";
            }
            if (start.x < 0) return "no start cell";
            var seen = new HashSet<Vector2Int> { start };
            var todo = new Stack<Vector2Int>();
            todo.Push(start);
            while (todo.Count > 0)
            {
                var p = todo.Pop();
                foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                {
                    var q = p + d;
                    if (!seen.Contains(q) && Walk(At(q.x, q.y))) { seen.Add(q); todo.Push(q); }
                }
            }
            return seen.Count == total ? null : $"{total - seen.Count} cells unreachable";
        }
    }
}
