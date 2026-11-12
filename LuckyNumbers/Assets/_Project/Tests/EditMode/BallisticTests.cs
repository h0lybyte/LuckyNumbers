using LuckyNumbers.Planet;
using NUnit.Framework;
using Unity.Mathematics;

namespace LuckyNumbers.Tests
{
    /// <summary>Ballistic (a thrower's flask): the arc lands where it's aimed, lobs no flatter than asked, falls short out of reach, leads.</summary>
    public sealed class BallisticTests
    {
        const float G = 9.81f, Speed = 14f;
        static readonly float Lob = math.radians(35f);   // CombatPresenter.Ranged's LobAngle

        static readonly float3 From = new(1f, 1.5f, -2f);

        // Near, far (just inside v^2/g), uphill, downhill, at your feet, off to the side: all within +-20 degrees of level.
        static readonly float3[] Targets =
        {
            new(10f, 0f, 0f), new(0f, 0f, 19f), new(6f, 2f, 3f), new(15f, -3f, 4f), new(2f, 0f, 1f), new(-7f, 0.5f, -7f),
        };

        static float Elevation(float3 v) => math.atan2(v.y, math.length(v.xz));

        /// <summary>Flies the arc in small steps (not At's closed form) and returns where it is at `time`.</summary>
        static float3 Fly(float3 from, float3 velocity, float time)
        {
            const int steps = 2000;
            var dt = time / steps;
            var p = from;
            var v = velocity;
            for (var i = 0; i < steps; i++)
            {
                v.y -= G * dt * 0.5f;   // half-step gravity either side of the move: exact for constant acceleration
                p += v * dt;
                v.y -= G * dt * 0.5f;
            }
            return p;
        }

        static void AssertNear(float3 expected, float3 actual, float within, string what)
        {
            Assert.LessOrEqual(math.distance(expected, actual), within, $"{what}: expected {expected}, got {actual}");
        }

        [Test]
        public void Solve_LandsOnTheTarget()
        {
            foreach (var minAngle in new[] { 0f, Lob })
            foreach (var offset in Targets)
            {
                var to = From + offset;
                Assert.IsTrue(Ballistic.Solve(From, to, Speed, G, out var v, out var t, minAngle), $"{offset} reaches");
                Assert.Greater(t, 0f);
                Assert.LessOrEqual(math.length(v), Speed + 1e-3f, "never faster than its arm");
                AssertNear(to, Fly(From, v, t), 0.02f, $"{offset}, min {math.degrees(minAngle):0} deg (flown)");
                AssertNear(to, Ballistic.At(From, v, G, t), 0.02f, $"{offset} (At)");
                Assert.Greater(math.dot(v.xz, offset.xz), 0f, "toward it");
            }
        }

        [Test]
        public void Solve_NeverFlatterThanTheLobAngle()
        {
            foreach (var offset in Targets)
            {
                Ballistic.Solve(From, From + offset, Speed, G, out var v, out _, Lob);
                Assert.GreaterOrEqual(Elevation(v), Lob - 1e-3f, $"{offset}: {math.degrees(Elevation(v)):0.0} deg");
            }
            // Near: the full-speed low arc would be a dart (15 deg at 10 m); it's slowed to a 35 deg lob instead.
            Ballistic.Solve(From, From + new float3(10f, 0f, 0f), Speed, G, out var near, out _, Lob);
            Assert.AreEqual(35f, math.degrees(Elevation(near)), 0.05f);
            Assert.Less(math.length(near), Speed - 1f);
            // Far: the low arc is already steeper than 35 deg (36 at 19 m): thrown at full speed on it.
            Ballistic.Solve(From, From + new float3(19f, 0f, 0f), Speed, G, out var far, out _, Lob);
            Assert.AreEqual(Speed, math.length(far), 1e-3f);
            Assert.Greater(Elevation(far), Lob);
        }

        [Test]
        public void Solve_WithoutAMinimum_TakesTheLowArc()
        {
            // 10 m on the level at 14 m/s: sin 2a = g x / v^2 = 0.5, so the low arc is 15 deg.
            Ballistic.Solve(From, From + new float3(10f, 0f, 0f), Speed, G, out var v, out _);
            Assert.AreEqual(15f, math.degrees(Elevation(v)), 0.05f);
            Assert.AreEqual(Speed, math.length(v), 1e-3f);
        }

        [Test]
        public void Solve_OutOfReach_ThrowsItsFarthest()
        {
            var to = From + new float3(30f, 0f, 30f);
            Assert.Greater(math.length((to - From).xz), Ballistic.Reach(Speed, G));
            Assert.IsFalse(Ballistic.Solve(From, to, Speed, G, out var v, out var t, Lob));
            Assert.AreEqual(45f, math.degrees(Elevation(v)), 0.01f);
            Assert.AreEqual(Speed, math.length(v), 1e-3f);
            var toward = math.normalize((to - From).xz);
            AssertNear(new float3(toward.x, 0f, toward.y), math.normalize(new float3(v.x, 0f, v.z)), 1e-4f, "toward it");
            Assert.AreEqual(2f * Speed * math.sin(math.radians(45f)) / G, t, 1e-4f, "time: back down to its own height");
            var landed = Fly(From, v, t);
            Assert.AreEqual(From.y, landed.y, 0.01f);
            Assert.AreEqual(Ballistic.Reach(Speed, G), math.length((landed - From).xz), 0.02f, "it lands short, at v^2 / g");
        }

        [Test]
        public void SolveLeading_LeadsByLeadTimesVelocityTimesFlight()
        {
            var to = From + new float3(10f, 0f, 2f);
            var moving = new float3(0f, 0f, 3f);
            const float lead = 0.5f;
            Ballistic.Solve(From, to, Speed, G, out _, out var flight, Lob);
            var aim = to + moving * (lead * flight);

            Assert.IsTrue(Ballistic.SolveLeading(From, to, moving, lead, Speed, G, out var v, out var t, Lob));
            Ballistic.Solve(From, aim, Speed, G, out var expected, out var expectedT, Lob);
            AssertNear(expected, v, 1e-4f, "the led solve");
            Assert.AreEqual(expectedT, t, 1e-5f);
            AssertNear(aim, Fly(From, v, t), 0.02f, "lands on the led point");
        }

        [Test]
        public void SolveLeading_NoLead_IsSolve()
        {
            var to = From + new float3(8f, 1f, -3f);
            Ballistic.Solve(From, to, Speed, G, out var plain, out var plainT, Lob);
            foreach (var (moving, lead) in new[] { (new float3(2f, 0f, 1f), 0f), (float3.zero, 1f), (new float3(0f, 5f, 0f), 1f) })
            {
                Ballistic.SolveLeading(From, to, moving, lead, Speed, G, out var v, out var t, Lob);
                AssertNear(plain, v, 1e-5f, $"moving {moving}, lead {lead}: only its flat motion is led");
                Assert.AreEqual(plainT, t, 1e-6f);
            }
        }

        [Test]
        public void SolveLeading_LeadIsCappedAtOne()
        {
            var to = From + new float3(9f, 0f, 0f);
            var moving = new float3(-2f, 0f, 2f);
            Ballistic.SolveLeading(From, to, moving, 1f, Speed, G, out var full, out _, Lob);
            Ballistic.SolveLeading(From, to, moving, 3f, Speed, G, out var over, out _, Lob);
            AssertNear(full, over, 1e-5f, "lead saturates");
        }
    }
}
