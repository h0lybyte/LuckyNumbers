using System.Collections.Generic;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using NUnit.Framework;
using Unity.Mathematics;

namespace LuckyNumbers.Tests
{
    /// <summary>
    /// A Defend job's horde apart from the scene: the wave numbers and who comes (DefendWaves), and the crowd's
    /// separation (MonsterSeparation: overlapping bodies pushed apart, far ones and fixed ones left be).
    /// </summary>
    public sealed class DefendTests
    {
        static DefendConfig Config() => new()
        {
            Waves = 3, WavesPerDanger = 0.5f, BeaconHp = 300, BeaconHpPerDanger = 60, Budget = 18, BudgetPerDanger = 4,
            BudgetGrowth = 1.3f, MaxAlive = 60, MaxAliveLowEnd = 25,
        };

        // ---- DefendWaves --------------------------------------------------------------------------------------

        [Test]
        public void Waves_TheJobsGoal_ElseTheConfigs()
        {
            var d = Config();
            Assert.AreEqual(5, DefendWaves.Waves(d, 5, 1), "a job says how many");
            Assert.AreEqual(4, DefendWaves.Waves(d, 0, 3), "3 + floor(0.5 x 3)");
            Assert.AreEqual(1, DefendWaves.Waves(new DefendConfig(), 0, 1), "never none");
        }

        [Test]
        public void Budget_GrowsEachWave()
        {
            var d = Config();
            Assert.AreEqual(30, DefendWaves.Budget(d, 1, 3), "18 + 4 x 3");
            Assert.AreEqual(39, DefendWaves.Budget(d, 2, 3), "x 1.3");
            Assert.AreEqual(51, DefendWaves.Budget(d, 3, 3));
            Assert.Less(DefendWaves.Budget(d, 4, 3), DefendWaves.Budget(d, 5, 3));
            Assert.AreEqual(1, DefendWaves.Budget(new DefendConfig(), 1, 1), "at least one");
        }

        [Test]
        public void BeaconHp_AndTheCaps()
        {
            var d = Config();
            Assert.AreEqual(480, DefendWaves.BeaconHp(d, 3));
            Assert.AreEqual(60, DefendWaves.MaxAlive(d, false));
            Assert.AreEqual(25, DefendWaves.MaxAlive(d, true), "the low-end tier's own");
            d.MaxAliveLowEnd = 0;
            Assert.AreEqual(60, DefendWaves.MaxAlive(d, true), "no low-end cap: the usual");
        }

        [Test]
        public void Pick_ByWeight_RespectingWaveAndDanger()
        {
            var mix = new List<DefendMix>
            {
                new() { Monster = "skeleton", Weight = 3f },
                new() { Monster = "arcanist", Weight = 1f, WaveMin = 2 },
                new() { Monster = "brute", Weight = 100f, DangerMin = 5 },
            };
            for (var r = 0f; r < 1f; r += 0.01f)
                Assert.AreEqual("skeleton", DefendWaves.Pick(mix, 1, 3, r), "wave 1: no arcanists yet, and no brutes below danger 5");
            Assert.AreEqual("skeleton", DefendWaves.Pick(mix, 2, 3, 0.74f));
            Assert.AreEqual("arcanist", DefendWaves.Pick(mix, 2, 3, 0.76f), "wave 2: a quarter arcanists");
            Assert.AreEqual("brute", DefendWaves.Pick(mix, 2, 5, 0.5f));
            Assert.AreEqual("", DefendWaves.Pick(new List<DefendMix>(), 1, 1, 0.5f));
        }

        [Test]
        public void Weight_GrowsPerWave()
        {
            var m = new DefendMix { Monster = "arcanist", Weight = 0.6f, WeightPerWave = 0.4f, WaveMin = 2 };
            Assert.AreEqual(0f, DefendWaves.Weight(m, 1, 1));
            Assert.AreEqual(1.0f, DefendWaves.Weight(m, 2, 1), 1e-5f);
            Assert.AreEqual(1.4f, DefendWaves.Weight(m, 3, 1), 1e-5f);
        }

        // ---- MonsterSeparation --------------------------------------------------------------------------------

        [Test]
        public void Separation_PushesOverlappingApart_HalfEach()
        {
            using var s = new MonsterSeparation();
            s.Begin(4);
            s.Add(new float2(0f, 0f), 0.5f);
            s.Add(new float2(0.6f, 0f), 0.5f);   // 0.4 m of overlap
            s.Add(new float2(10f, 10f), 0.5f);  // nobody near
            s.Run();
            Assert.AreEqual(-0.2f, s.Push(0).x, 1e-4f);
            Assert.AreEqual(0.2f, s.Push(1).x, 1e-4f);
            Assert.AreEqual(0f, s.Push(0).y, 1e-4f);
            Assert.AreEqual(float2.zero, s.Push(2));
        }

        [Test]
        public void Separation_FixedBodiesPushButStay()
        {
            using var s = new MonsterSeparation();
            s.Begin(2);
            s.Add(new float2(0f, 0f), 1f, true);   // the beacon
            s.Add(new float2(0f, 1.5f), 0.5f);    // just touching
            s.Run();
            Assert.AreEqual(float2.zero, s.Push(0), "fixed: never moves");
            Assert.AreEqual(0.0f, s.Push(1).x, 1e-4f);
            Assert.AreEqual(0.0f, s.Push(1).y, 1e-4f, "1.5 m apart, reach 1.5: just touching");
            s.Begin(2);
            s.Add(new float2(0f, 0f), 1f, true);
            s.Add(new float2(0f, 1.2f), 0.5f);
            s.Run();
            Assert.AreEqual(0.3f, s.Push(1).y, 1e-4f, "all of the overlap, not half");
        }

        [Test]
        public void Separation_AcrossCells_AndOnTopOfEachOther()
        {
            using var s = new MonsterSeparation();
            s.Begin(4);
            var edge = MonsterSeparation.Cell;   // either side of a cell edge
            s.Add(new float2(edge - 0.1f, 0f), 0.4f);
            s.Add(new float2(edge + 0.1f, 0f), 0.4f);
            s.Add(new float2(-5f, -5f), 0.4f);
            s.Add(new float2(-5f, -5f), 0.4f);   // exactly on top
            s.Run();
            Assert.Less(s.Push(0).x, 0f);
            Assert.Greater(s.Push(1).x, 0f);
            Assert.Greater(math.length(s.Push(2)), 0.3f, "right on top: pushed apart all the same");
            Assert.AreEqual(0f, math.length(s.Push(2) + s.Push(3)), 1e-4f, "opposite ways");
        }

        [Test]
        public void Separation_NotSolidIsLeftOut()
        {
            using var s = new MonsterSeparation();
            s.Begin(2);
            s.Add(new float2(0f, 0f), 0f);   // dying
            s.Add(new float2(0.1f, 0f), 0.5f);
            s.Run();
            Assert.AreEqual(float2.zero, s.Push(0));
            Assert.AreEqual(float2.zero, s.Push(1));
        }

        [Test]
        public void Separation_ACrowdEndsUpApart()
        {
            using var s = new MonsterSeparation();
            var rng = new Random(7);
            var at = new float2[60];
            for (var i = 0; i < at.Length; i++) at[i] = rng.NextFloat2(-2f, 2f);   // 60 in a 4 m square: a heap
            for (var step = 0; step < 200; step++)
            {
                s.Begin(at.Length);
                foreach (var p in at) s.Add(p, 0.4f);
                s.Run();
                for (var i = 0; i < at.Length; i++) at[i] += s.Push(i) * 0.5f;
            }
            var closest = float.MaxValue;
            for (var i = 0; i < at.Length; i++)
            for (var j = i + 1; j < at.Length; j++)
                closest = math.min(closest, math.distance(at[i], at[j]));
            Assert.Greater(closest, 0.7f, "spread out to (nearly) their 0.8 m");
        }
    }
}
