using Unity.Collections;
using Unity.Mathematics;

namespace LuckyNumbers.Hold
{
    public static partial class HoldNav
    {
        /// <summary>
        /// Stairs (StairCell): the top step joins the landing past it on the storey above, and that landing joins
        /// back down. Along the flight the plain moves do the rest (steps in a row, the bottom approach, each step's
        /// OpenAbove into its well cell).
        /// </summary>
        static partial void ExtraMoves(in HoldGrid g, int3 c, HoldCell here, ref FixedList128Bytes<int3> moves)
        {
            var up = new int3(0, 1, 0);
            if (StairCell.IsStep(here))
            {
                if (!StairCell.NextIsStep(g, c, here)) moves.Add(c + StairCell.Vec(StairCell.Dir(here)) + up);
                return;
            }
            if (here.Kind == CellKind.Stair) return;
            // A landing: a top step one storey down, one cell back, climbing towards us.
            for (var dir = 0; dir < 4; dir++)
            {
                var step = c - up - StairCell.Vec(dir);
                var cell = g.Get(step);
                if (StairCell.IsStep(cell) && StairCell.Dir(cell) == dir && !StairCell.NextIsStep(g, step, cell)) moves.Add(step);
            }
        }
    }
}
