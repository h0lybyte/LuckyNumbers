using System.Collections.Generic;
using LuckyNumbers.Model;
using LuckyNumbers.Station;
using NUnit.Framework;
using UnityEngine;

namespace LuckyNumbers.Tests
{
    /// <summary>The race circuit (RaceTrack) and the mystery cubes' dice (RaceDice).</summary>
    public sealed class RaceTests
    {
        static RaceTrackRules Rules() => new()
        {
            Points = 9, Radius = 1100f, RadiusJitter = 0.3f, Height = 160f, GateSpacing = 260f, GateSize = 110f,
            CubeEvery = 3, CubeRow = 4, CubeSize = 9f, Hazards = 36, HazardSizeMin = 20f, HazardSizeMax = 70f, HazardSpread = 220f,
        };

        static readonly List<float> Weights = new() { 4f, 3f, 1f, 2f };

        [Test]
        public void Track_IsSeeded()
        {
            var a = RaceTrack.Generate(Rules(), 1234u, Vector3.zero, Vector3.left, Weights);
            var b = RaceTrack.Generate(Rules(), 1234u, Vector3.zero, Vector3.left, Weights);
            var c = RaceTrack.Generate(Rules(), 99u, Vector3.zero, Vector3.left, Weights);
            Assert.AreEqual(a.Length, b.Length, 1e-3f);
            Assert.AreEqual(a.Gates.Count, b.Gates.Count);
            Assert.AreEqual(a.Gates[3].Position, b.Gates[3].Position);
            Assert.AreNotEqual(a.Gates[3].Position, c.Gates[3].Position, "another seed, another course");
        }

        [Test]
        public void Track_StartsAtTheLine_AndLoops()
        {
            var start = new Vector3(100f, 20f, -50f);
            var t = RaceTrack.Generate(Rules(), 7u, start, Vector3.left, Weights);
            Assert.Less(Vector3.Distance(t.Gates[0].Position, start), 1f, "gate 0 is the start line");
            Assert.Less(Vector3.Distance(t.At(t.Length - 0.01f), start), 20f, "the loop closes back on the line");
            Assert.GreaterOrEqual(t.Gates.Count, 4);
            for (var g = 1; g < t.Gates.Count; g++)
                Assert.AreEqual(t.Spacing, t.Gates[g].Distance - t.Gates[g - 1].Distance, 1e-2f, "gates evenly spaced");
            // Away from the station: nothing on the line comes back far past the start toward it (+x here).
            foreach (var p in t.Samples) Assert.Less(p.x, start.x + 60f);
        }

        [Test]
        public void Track_KeepsRocksOffTheLine()
        {
            var t = RaceTrack.Generate(Rules(), 42u, Vector3.zero, Vector3.left, Weights);
            Assert.Greater(t.Hazards.Count, 0);
            var half = t.GateSize * 0.5f;
            foreach (var h in t.Hazards)
            {
                var nearest = t.Samples[t.Nearest(h.center)];
                var gap = Vector3.Distance(nearest, h.center) - h.extents.magnitude;
                Assert.Greater(gap, half * 0.5f, "a rock leaves the gate's width clear");
            }
        }

        [Test]
        public void Track_CubeRowsSitBetweenGates()
        {
            var t = RaceTrack.Generate(Rules(), 5u, Vector3.zero, Vector3.left, Weights);
            Assert.Greater(t.Cubes.Count, 0);
            foreach (var c in t.Cubes)
            {
                Assert.Greater(c.Distance, t.Gates[c.AfterGate].Distance);
                Assert.That(c.Kind, Is.InRange(0, Weights.Count - 1));
            }
        }

        [Test]
        public void Nearest_AndAhead_WrapRoundTheLoop()
        {
            var t = RaceTrack.Generate(Rules(), 11u, Vector3.zero, Vector3.left, Weights);
            var m = t.Samples.Count;
            Assert.AreEqual(17, t.Nearest(t.Samples[17]));
            Assert.AreEqual(17, t.Nearest(t.Samples[17], 20, 8), "local search finds it too");
            Assert.AreEqual(2, t.Ahead(1, m - 1), "just past the line is ahead of just before it");
            Assert.AreEqual(-2, t.Ahead(m - 1, 1));
        }

        [Test]
        public void PickWeighted_FollowsTheWeights()
        {
            Assert.AreEqual(0, RaceTrack.PickWeighted(Weights, 0f));
            Assert.AreEqual(0, RaceTrack.PickWeighted(Weights, 0.39f));
            Assert.AreEqual(1, RaceTrack.PickWeighted(Weights, 0.41f));
            Assert.AreEqual(2, RaceTrack.PickWeighted(Weights, 0.75f));
            Assert.AreEqual(3, RaceTrack.PickWeighted(Weights, 1f));
            Assert.AreEqual(0, RaceTrack.PickWeighted(new List<float>(), 0.5f));
        }

        static MysteryCube Cube()
        {
            var c = new MysteryCube { Name = "Test", Nat20 = new CubeOutcome { Name = "Jackpot", Effect = RaceEffect.Warp } };
            c.Outcomes.Add(new CubeOutcome { Min = 1, Name = "Low", Effect = RaceEffect.Stall });
            c.Outcomes.Add(new CubeOutcome { Min = 8, Name = "Mid", Effect = RaceEffect.Shield });
            c.Outcomes.Add(new CubeOutcome { Min = 15, Name = "High", Effect = RaceEffect.Boost });
            return c;
        }

        [Test]
        public void Dice_PicksByTotal()
        {
            var cube = Cube();
            var fumble = new CubeOutcome { Name = "Backfire", Effect = RaceEffect.Stall };
            Assert.AreEqual("Low", RaceDice.Pick(cube, fumble, 5, 5).Name);
            Assert.AreEqual("Mid", RaceDice.Pick(cube, fumble, 6, 8).Name, "a bonus can lift a roll into the next band");
            Assert.AreEqual("High", RaceDice.Pick(cube, fumble, 15, 19).Name);
            Assert.AreEqual("Low", RaceDice.Pick(cube, fumble, 2, 0).Name, "under every band: the lowest");
        }

        [Test]
        public void Dice_NaturalsOverrideTheTotal()
        {
            var cube = Cube();
            var fumble = new CubeOutcome { Name = "Backfire", Effect = RaceEffect.Stall };
            Assert.AreEqual("Backfire", RaceDice.Pick(cube, fumble, 1, 5).Name, "a natural 1 backfires whatever the bonus");
            Assert.AreEqual("Jackpot", RaceDice.Pick(cube, fumble, 20, 24).Name);
            cube.Nat20 = null;
            Assert.AreEqual("High", RaceDice.Pick(cube, fumble, 20, 20).Name, "no nat20: the top of the table");
            Assert.AreEqual("Low", RaceDice.Pick(cube, null, 1, 1).Name, "no fumble set: the table");
        }

        [Test]
        public void Dice_BonusIsLuckPlusCatchUp()
        {
            var rules = new RaceRoll { LuckMax = 2, CatchUpPerPlace = 1, CatchUpMax = 4 };
            Assert.AreEqual(0, RaceDice.Bonus(rules, 9u, false, 1), "rivals: no luck; leader: no catch-up");
            Assert.AreEqual(9 % 3, RaceDice.Bonus(rules, 9u, true, 1));
            Assert.AreEqual(2 + 3, RaceDice.Bonus(rules, 5u, true, 4));
            Assert.AreEqual(4, RaceDice.Bonus(rules, 0u, false, 11), "catch-up capped");
            Assert.AreEqual(0, RaceDice.Bonus(null, 5u, true, 6));
        }

        [Test]
        public void Ordinals()
        {
            Assert.AreEqual("1st", Race.Ordinal(1));
            Assert.AreEqual("2nd", Race.Ordinal(2));
            Assert.AreEqual("3rd", Race.Ordinal(3));
            Assert.AreEqual("11th", Race.Ordinal(11));
            Assert.AreEqual("22nd", Race.Ordinal(22));
        }
    }
}
