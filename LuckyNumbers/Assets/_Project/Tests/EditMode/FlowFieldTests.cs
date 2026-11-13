using LuckyNumbers.Hold;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace LuckyNumbers.Tests
{
    /// <summary>FlowField (the deep creatures' shared chase): costs are breadth-first steps, steps go round walls, it stops where told.</summary>
    public sealed class FlowFieldTests
    {
        // 5 x 5, one level, mine floor; a wall down x = 2 from z = 0 to z = 3 (the way round is through z = 4).
        static HoldGrid Walled()
        {
            var g = new HoldGrid(new int3(5, 1, 5), int3.zero, Allocator.Persistent);
            for (var x = 0; x < 5; x++)
            for (var z = 0; z < 5; z++)
                g.Set(new int3(x, 0, z), new HoldCell { Kind = x == 2 && z < 4 ? CellKind.Solid : CellKind.Mine });
            return g;
        }

        [Test]
        public void Cost_IsStepsAroundTheWall()
        {
            var g = Walled();
            using var f = new FlowField();
            f.Build(g, 0, new int2(0, 0), 1000);
            Assert.AreEqual(0, f.Cost(new int2(0, 0)));
            Assert.AreEqual(1, f.Cost(new int2(1, 0)));
            Assert.AreEqual(FlowField.Unreached, f.Cost(new int2(2, 0)), "wall");
            // (0,0) -> (0,4): 4, across to (4,4): +4, down to (4,0): +4.
            Assert.AreEqual(12, f.Cost(new int2(4, 0)));
            g.Dispose();
        }

        [Test]
        public void Steps_WalkToTheGoal()
        {
            var g = Walled();
            using var f = new FlowField();
            f.Build(g, 0, new int2(0, 0), 1000);
            var at = new int2(4, 0);
            var steps = 0;
            while (f.Step(at, out var next))
            {
                Assert.AreEqual(1, math.csum(math.abs(next - at)), "one 4-connected step");
                Assert.AreNotEqual(CellKind.Solid, g.Get(new int3(next.x, 0, next.y)).Kind);
                at = next;
                Assert.Less(++steps, 100);
            }
            Assert.AreEqual(new int2(0, 0), at);
            Assert.AreEqual(12, steps);
            g.Dispose();
        }

        [Test]
        public void MaxVisit_LeavesFarCellsWithoutAStep()
        {
            var g = Walled();
            using var f = new FlowField();
            f.Build(g, 0, new int2(0, 0), 3);
            Assert.IsFalse(f.Step(new int2(4, 0), out _), "beyond the search: walk straight");
            Assert.IsTrue(f.Step(new int2(0, 1), out var next));
            Assert.AreEqual(new int2(0, 0), next);
            g.Dispose();
        }

        [Test]
        public void Goal_OnRockOrShaft_ReachesNothing()
        {
            var g = Walled();
            g.Set(new int3(0, 0, 4), new HoldCell { Kind = CellKind.ShaftDown });
            using var f = new FlowField();
            f.Build(g, 0, new int2(2, 0), 1000);
            Assert.AreEqual(FlowField.Unreached, f.Cost(new int2(0, 0)));
            f.Build(g, 0, new int2(0, 4), 1000);
            Assert.AreEqual(FlowField.Unreached, f.Cost(new int2(0, 3)), "creatures don't step into shafts");
            Assert.AreEqual(2, f.Version);
            g.Dispose();
        }
    }
}
