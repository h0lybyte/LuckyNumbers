using Unity.Collections;
using Unity.Mathematics;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// Where you can walk to within one grid: the shared notion of "connected" for the mine passes' validation, the
    /// creatures' paths and any check. A move is to a horizontal neighbour (both walkable, no pillar in either), or
    /// vertical: ShaftDown to the ShaftUp below (and back up a ladder), through an OpenAbove link, along a great
    /// shaft (its hoist), or anything the Vertical pass adds (stairs) via ExtraMoves. With fixturesClosed, cells
    /// holding a LockedDoor, Portcullis or Seal fixture can't be entered (use it to prove a key is reachable without
    /// passing its door).
    /// </summary>
    public static partial class HoldNav
    {
        public static bool Standable(HoldCell c) => c.Walkable && (c.Flags & CellFlags.Pillar) == 0;

        /// <summary>A creature may step from one cell to the next (same level, both roamable). Not through a doorway:
        /// the puzzles' doors and portcullises (MinePasses.Puzzles) stand in Door cells; not into a shaft.</summary>
        public static bool Roamable(HoldCell c) =>
            Standable(c) && c.Kind != CellKind.ShaftDown && c.Kind != CellKind.ShaftUp && c.Kind != CellKind.Door;

        /// <summary>Every cell index reachable from `from` (local); returns how many. `seen` must be Cells.Length.</summary>
        public static int Flood(in HoldGrid g, int3 from, bool fixturesClosed, NativeArray<bool> seen)
        {
            for (var i = 0; i < seen.Length; i++) seen[i] = false;
            if (!g.InBounds(from) || !Standable(g.Get(from))) return 0;
            var blocked = new NativeArray<bool>(seen.Length, Allocator.Temp);
            if (fixturesClosed && g.Fixtures.IsCreated)
                foreach (var f in g.Fixtures)
                    if ((f.Kind == FixtureKind.LockedDoor || f.Kind == FixtureKind.Portcullis || f.Kind == FixtureKind.Seal) && g.InBounds(f.Cell))
                        blocked[g.Index(f.Cell)] = true;
            var queue = new NativeList<int3>(256, Allocator.Temp);
            queue.Add(from);
            seen[g.Index(from)] = true;
            var count = 0;
            var moves = new FixedList128Bytes<int3>();
            for (var head = 0; head < queue.Length; head++)
            {
                var c = queue[head];
                count++;
                moves.Clear();
                Moves(g, c, ref moves);
                foreach (var to in moves)
                {
                    if (!g.InBounds(to)) continue;
                    var i = g.Index(to);
                    if (seen[i] || blocked[i] || !Standable(g.Get(to))) continue;
                    seen[i] = true;
                    queue.Add(to);
                }
            }
            queue.Dispose();
            blocked.Dispose();
            return count;
        }

        /// <summary>Cells one move from c (may be out of the grid or unwalkable: Flood filters).</summary>
        public static void Moves(in HoldGrid g, int3 c, ref FixedList128Bytes<int3> moves)
        {
            var here = g.Get(c);
            moves.Add(c + new int3(1, 0, 0));
            moves.Add(c + new int3(-1, 0, 0));
            moves.Add(c + new int3(0, 0, 1));
            moves.Add(c + new int3(0, 0, -1));
            var up = c + new int3(0, 1, 0);
            var down = c + new int3(0, -1, 0);
            var above = g.Get(up);
            var below = g.Get(down);
            if (here.Kind == CellKind.ShaftDown && below.Kind == CellKind.ShaftUp) moves.Add(down);
            if (here.Kind == CellKind.ShaftUp && above.Kind == CellKind.ShaftDown) moves.Add(up);
            if ((here.Flags & CellFlags.OpenAbove) != 0 && above.Walkable) moves.Add(up);
            if ((below.Flags & CellFlags.OpenAbove) != 0 && below.Walkable) moves.Add(down);
            if (here.Kind == CellKind.GreatShaft)
            {
                if (above.Kind == CellKind.GreatShaft) moves.Add(up);
                if (below.Kind == CellKind.GreatShaft) moves.Add(down);
            }
            ExtraMoves(g, c, here, ref moves);
        }

        /// <summary>The Vertical pass's moves (stairs): implemented in HoldNav.Stairs.cs.</summary>
        static partial void ExtraMoves(in HoldGrid g, int3 c, HoldCell here, ref FixedList128Bytes<int3> moves);
    }
}
