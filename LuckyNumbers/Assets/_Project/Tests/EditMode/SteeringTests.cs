using LuckyNumbers.Station;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Tests
{
    /// <summary>Steering (the rogues' Reynolds behaviours): arrival brakes in time, orbits respect the turn rate, avoidance parts ships.</summary>
    public sealed class SteeringTests
    {
        [Test]
        public void ArriveSpeed_IsBrakingDistance()
        {
            // v = sqrt(2 a (d - r)): 20 m/s² over 10 m left is 20 m/s.
            Assert.AreEqual(20f, Steering.ArriveSpeed(15f, 5f, 20f, 100f), 1e-3f);
            Assert.AreEqual(30f, Steering.ArriveSpeed(1000f, 5f, 20f, 30f), 1e-3f, "capped at max speed");
            Assert.AreEqual(0f, Steering.ArriveSpeed(3f, 5f, 20f, 30f), 1e-3f, "inside the stop radius: stop");
        }

        [Test]
        public void Arrive_MatchesAMovingGoal()
        {
            var goalVel = new Vector3(0f, 0f, 12f);
            var v = Steering.Arrive(Vector3.zero, new Vector3(2f, 0f, 0f), goalVel, 40f, 20f, 5f);
            Assert.That((v - goalVel).magnitude, Is.LessThan(1e-3f), "settled in the slot: just its velocity");
            var far = Steering.Arrive(Vector3.zero, new Vector3(500f, 0f, 0f), Vector3.zero, 40f, 20f, 5f);
            Assert.AreEqual(40f, far.magnitude, 1e-3f);
            Assert.Greater(far.x, 0f);
        }

        [Test]
        public void Arrive_StopsWithoutOvershoot()
        {
            // A point mass with the same braking limit, flying the arrival velocity, stops short of the goal.
            const float brake = 20f, dt = 1f / 60f;
            var p = Vector3.zero;
            var v = Vector3.zero;
            var goal = new Vector3(300f, 0f, 0f);
            var furthest = 0f;
            for (var i = 0; i < 60 * 30; i++)
            {
                var a = Steering.Command(Steering.Arrive(p, goal, Vector3.zero, 50f, brake, 4f), v, 0.1f, brake);
                v += a * dt;
                p += v * dt;
                furthest = math.max(furthest, p.x);
            }
            Assert.That(furthest, Is.LessThan(goal.x + 1f), "never overshoots");
            Assert.That(Vector3.Distance(p, goal), Is.LessThan(6f), "ends at the goal");
            Assert.That(v.magnitude, Is.LessThan(0.5f), "and stops");
        }

        [Test]
        public void Lead_IsCapped()
        {
            var target = new Vector3(0f, 0f, 1000f);
            var vel = new Vector3(30f, 0f, 0f);
            // 1000 m at 50 m/s is 20 s, but the look ahead stops at 1.5 s.
            Assert.AreEqual(45f, Steering.Lead(Vector3.zero, 50f, target, vel, 1.5f).x, 1e-3f);
            Assert.AreEqual(6f, Steering.Lead(Vector3.zero, 50f, new Vector3(0f, 0f, 10f), vel, 1.5f).x, 1e-3f);
        }

        [Test]
        public void OrbitSpeed_IsTurnRateTimesRadius()
        {
            // 60 m/s on a 100 m orbit needs 36 m/s² inward; a 30°/s turn gives v·w = 31.4: too little, so slower.
            var v = Steering.OrbitSpeed(60f, 100f, 30f);
            Assert.AreEqual(math.radians(30f) * 100f, v, 1e-3f);
            Assert.That(v * v / 100f, Is.LessThanOrEqualTo(v * math.radians(30f) + 1e-3f));
            Assert.AreEqual(20f, Steering.OrbitSpeed(20f, 100f, 120f), 1e-3f, "fast turner: its own speed");
        }

        [Test]
        public void Orbit_OnTheCircle_IsTangential()
        {
            var v = Steering.Orbit(new Vector3(100f, 0f, 0f), Vector3.zero, Vector3.zero, Vector3.up, 100f, 30f, 90f, 0.5f, 1);
            Assert.AreEqual(30f, v.magnitude, 1e-3f);
            Assert.AreEqual(0f, Vector3.Dot(v, Vector3.right), 1e-3f, "no radial part on the circle");
            var inside = Steering.Orbit(new Vector3(50f, 0f, 0f), Vector3.zero, Vector3.zero, Vector3.up, 100f, 30f, 90f, 0.5f, 1);
            Assert.Greater(inside.x, 0f, "inside: pushed out");
            var other = Steering.Orbit(new Vector3(100f, 0f, 0f), Vector3.zero, Vector3.zero, Vector3.up, 100f, 30f, 90f, 0.5f, -1);
            Assert.That((v + other).magnitude, Is.LessThan(1e-3f), "side flips the direction");
        }

        [Test]
        public void ClosestApproach_FindsTheMiss()
        {
            // Me 100 m behind, closing at 10 m/s, 5 m off to the side: closest at 10 s, 5 m apart.
            var t = Steering.ClosestApproach(new Vector3(5f, 0f, -100f), new Vector3(0f, 0f, 10f), 20f, out var gap);
            Assert.AreEqual(10f, t, 1e-3f);
            Assert.AreEqual(5f, gap.magnitude, 1e-3f);
            Assert.AreEqual(2f, Steering.ClosestApproach(new Vector3(5f, 0f, -100f), new Vector3(0f, 0f, 10f), 2f, out _), 1e-3f, "capped at the horizon");
        }

        [Test]
        public void AvoidShip_PartsHeadOnAndIgnoresClearPasses()
        {
            // Head on: both sides push, opposite ways.
            var a = Steering.AvoidShip(new Vector3(0f, 0f, -40f), new Vector3(0f, 0f, 40f), 30f, 1.5f);
            var b = Steering.AvoidShip(new Vector3(0f, 0f, 40f), new Vector3(0f, 0f, -40f), 30f, 1.5f);
            Assert.Greater(a.magnitude, 0.1f);
            Assert.Less(Vector3.Dot(a, b), 0f);
            // Passing 50 m apart with a 30 m safe gap: nothing.
            Assert.AreEqual(Vector3.zero, Steering.AvoidShip(new Vector3(50f, 0f, -40f), new Vector3(0f, 0f, 40f), 30f, 1.5f));
            // Moving apart: nothing.
            Assert.AreEqual(Vector3.zero, Steering.AvoidShip(new Vector3(0f, 0f, 40f), new Vector3(0f, 0f, 40f), 30f, 1.5f));
        }
    }
}
