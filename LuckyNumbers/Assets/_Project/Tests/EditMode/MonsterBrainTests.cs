using LuckyNumbers.Planet;
using NUnit.Framework;

namespace LuckyNumbers.Tests
{
    /// <summary>
    /// MonsterBrain alone (no Unity objects): the transition table through Fire and Tick's cues, the melee acts, a
    /// thrower's band / wind-up / interrupts, and Threat (what CombatState.alive counts).
    /// </summary>
    public sealed class MonsterBrainTests
    {
        // Sight 12 (lost at 36), reach 1.5 (bites inside 1.275), standoff 6. A thrower keeps 5..12 m, winds up 0.5 s, waits 3 s.
        // dt 0.125: the clocks add up exactly in binary, so the timings below are exact. Distances stay off the brain's
        // computed edges (reach x 0.85, RangeMin x CastNearest ...): a float product there could round either way.
        const float Dt = 0.125f, Sight = 12f, Reach = 1.5f, Min = 5f, Max = 12f, CastTime = 0.5f, Wait = 3f;
        const float Bitten = 1.2f, Unbitten = 1.4f;   // either side of reach x 0.85 (1.275)

        /// <summary>You on foot well past the grace, `distance` away.</summary>
        static MonsterSenses Senses(float distance, bool onFoot = true, float onFootFor = 10f, float now = 0f, bool busy = false) => new()
        {
            Dt = Dt, Now = now, Distance = distance, OnFoot = onFoot, OnFootFor = onFootFor,
            Sight = Sight, Reach = Reach, StandoffDistance = 6f, Busy = busy,
        };

        /// <summary>The same, for a thrower; `sees`: a clear line on you.</summary>
        static MonsterSenses Thrower(float distance, bool sees = true, bool onFoot = true, bool busy = false)
        {
            var s = Senses(distance, onFoot, busy: busy);
            s.Ranged = true;
            s.LineOfSight = sees;
            s.RangeMin = Min;
            s.RangeMax = Max;
            s.CastTime = CastTime;
            s.CastCooldown = Wait;
            return s;
        }

        static MonsterBrain Chasing() => MonsterBrain.Spawn(true);

        // ---- transitions --------------------------------------------------------------------------------------

        [Test]
        public void Spawn_WanderOrChase()
        {
            Assert.AreEqual(MonsterState.Wander, MonsterBrain.Spawn(false).State);
            Assert.AreEqual(MonsterState.Chase, MonsterBrain.Spawn(true).State);
        }

        [Test]
        public void Wander_NoticesYouWithinSight_AfterTheGrace()
        {
            var b = MonsterBrain.Spawn(false);
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(5f, onFootFor: MonsterBrain.NoticeAfter - 1f)));
            Assert.AreEqual(MonsterState.Wander, b.State, "still in your grace");
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(Sight + 1f)));
            Assert.AreEqual(MonsterState.Wander, b.State, "out of sight");
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(5f, onFoot: false)));
            Assert.AreEqual(MonsterState.Wander, b.State, "in your ship");

            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Senses(5f)), "noticed: comes for you the same tick");
            Assert.AreEqual(MonsterState.Chase, b.State);
        }

        [Test]
        public void Chase_LosesYouPastThreeTimesSight()
        {
            var b = Chasing();
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Senses(Sight * 3f - 0.5f)));
            Assert.AreEqual(MonsterState.Chase, b.State);
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Senses(Sight * 3f + 0.5f)), "this tick's act still happens");
            Assert.AreEqual(MonsterState.Wander, b.State);
        }

        [Test]
        public void Flee_RunsThenVanishesIntoDying()
        {
            var b = MonsterBrain.Spawn(false);
            Assert.IsTrue(MonsterBrain.Fire(ref b, MonsterCue.Scared));
            Assert.AreEqual(MonsterState.Flee, b.State);
            var ticks = 0;
            while (b.State == MonsterState.Flee)
            {
                Assert.AreEqual(MonsterAct.Run, MonsterBrain.Tick(ref b, Senses(5f)), "runs every tick, the last one too");
                Assert.Less(++ticks, 1000);
            }
            Assert.AreEqual(MonsterState.Dying, b.State);
            Assert.Greater(ticks * Dt, MonsterBrain.FleeFor);
            Assert.LessOrEqual(ticks * Dt, MonsterBrain.FleeFor + Dt);
            Assert.AreEqual(0f, b.Clock, "Dying's clock starts from 0");
            Assert.AreEqual(MonsterAct.Fade, MonsterBrain.Tick(ref b, Senses(5f)));
            Assert.AreEqual(Dt, b.Clock);
        }

        [Test]
        public void Flee_ShotAtKeepsRunning_Harried()
        {
            var b = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref b, MonsterCue.Scared);
            Assert.IsFalse(MonsterBrain.Fire(ref b, MonsterCue.ShotAt));
            Assert.AreEqual(MonsterState.Flee, b.State);
            Assert.IsTrue(b.Hunting, "Harried: the chase hop the rest of the way");
        }

        [Test]
        public void Killed_IsFinal()
        {
            foreach (var from in new[] { MonsterState.Wander, MonsterState.Chase, MonsterState.Flee })
            {
                var b = new MonsterBrain { State = from };
                Assert.IsTrue(MonsterBrain.Fire(ref b, MonsterCue.Killed));
                Assert.IsTrue(b.Dead);
                foreach (MonsterCue cue in System.Enum.GetValues(typeof(MonsterCue)))
                {
                    Assert.IsFalse(MonsterBrain.Fire(ref b, cue, 99f), $"{cue} moved a dead one");
                    Assert.AreEqual(MonsterState.Dying, b.State);
                }
                Assert.AreEqual(MonsterAct.Fade, MonsterBrain.Tick(ref b, Senses(1f)));
            }
        }

        [Test]
        public void Calm_BlindUntilCalmUntil_ThenWanders()
        {
            var b = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref b, MonsterCue.Challenged);
            Assert.IsTrue(MonsterBrain.Fire(ref b, MonsterCue.Calmed, 5f));
            Assert.AreEqual(MonsterState.Calm, b.State);
            Assert.AreEqual(5f, b.CalmUntil);

            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(2f, now: 4.9f)), "blind to you, even close");
            Assert.AreEqual(MonsterState.Calm, b.State);

            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(Sight + 5f, now: 5f)));
            Assert.AreEqual(MonsterState.Wander, b.State);
            Assert.AreEqual(0f, b.CalmUntil, "cleared leaving Calm");
        }

        [Test]
        public void Calm_WearsOffIntoNoticingYouTheSameTick()
        {
            var b = new MonsterBrain();
            MonsterBrain.Fire(ref b, MonsterCue.Calmed, 5f);
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Senses(5f, now: 6f)));
            Assert.AreEqual(MonsterState.Chase, b.State);
        }

        [Test]
        public void Standoff_OpensOnFootWithinTheDistance()
        {
            var b = MonsterBrain.Spawn(false);
            Assert.IsTrue(MonsterBrain.Fire(ref b, MonsterCue.Challenged));
            Assert.AreEqual(MonsterState.Standoff, b.State);
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(3f, onFootFor: MonsterBrain.StandoffAfter * 0.5f)), "just stepped out");
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(7f)), "not close enough");
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(3f, onFoot: false)), "in your ship");
            Assert.AreEqual(MonsterAct.OpenStandoff, MonsterBrain.Tick(ref b, Senses(3f)));
            Assert.AreEqual(MonsterState.Standoff, b.State, "the card decides, not the tick");
        }

        [Test]
        public void Challenged_OnlyFromWander()
        {
            var b = Chasing();
            Assert.IsFalse(MonsterBrain.Fire(ref b, MonsterCue.Challenged));
            Assert.AreEqual(MonsterState.Chase, b.State);
        }

        [Test]
        public void StandoffOutcomes()
        {
            (MonsterCue cue, MonsterState to)[] outcomes =
            {
                (MonsterCue.LetPass, MonsterState.Ignored), (MonsterCue.Calmed, MonsterState.Calm), (MonsterCue.Scared, MonsterState.Flee),
                (MonsterCue.Ambushed, MonsterState.Chase), (MonsterCue.Fight, MonsterState.Chase), (MonsterCue.FightWon, MonsterState.Chase),
            };
            foreach (var (cue, to) in outcomes)
            {
                var b = MonsterBrain.Spawn(false);
                MonsterBrain.Fire(ref b, MonsterCue.Challenged);
                b.Cooldown = 5f;
                MonsterBrain.Fire(ref b, cue, 10f);
                Assert.AreEqual(to, b.State, cue.ToString());
                if (cue == MonsterCue.Ambushed) Assert.AreEqual(0f, b.Cooldown, "ambushed: bites at once");
            }
            var raised = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref raised, MonsterCue.Fight);
            Assert.AreEqual(MonsterBrain.RaiseWeapon, raised.Cooldown, "a moment to raise your weapon");
        }

        [Test]
        public void Ignored_ComesForYouOnlyWhenShotAt()
        {
            var b = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref b, MonsterCue.LetPass);
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(3f)));
            Assert.AreEqual(MonsterState.Ignored, b.State);
            Assert.IsTrue(MonsterBrain.Fire(ref b, MonsterCue.ShotAt));
            Assert.AreEqual(MonsterState.Chase, b.State);
        }

        [Test]
        public void Surprise_UsedOnce_AndDroppedLeavingChase()
        {
            var b = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref b, MonsterCue.Challenged);
            MonsterBrain.Fire(ref b, MonsterCue.FightWon);
            Assert.IsTrue(MonsterBrain.TakeSurprise(ref b));
            Assert.IsFalse(MonsterBrain.TakeSurprise(ref b), "used up");

            var lost = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref lost, MonsterCue.Challenged);
            MonsterBrain.Fire(ref lost, MonsterCue.FightWon);
            MonsterBrain.Tick(ref lost, Senses(Sight * 3f + 1f));
            Assert.AreEqual(MonsterState.Wander, lost.State);
            Assert.IsFalse(MonsterBrain.TakeSurprise(ref lost), "dropped when it lost you");

            var killed = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref killed, MonsterCue.FightWon);
            MonsterBrain.Fire(ref killed, MonsterCue.Killed);
            Assert.AreEqual(MonsterMark.None, killed.Marks & MonsterMark.Surprise);

            var refought = MonsterBrain.Spawn(false);
            MonsterBrain.Fire(ref refought, MonsterCue.FightWon);
            MonsterBrain.Fire(ref refought, MonsterCue.Fight);
            Assert.IsFalse(MonsterBrain.TakeSurprise(ref refought), "a lost roll clears it");
        }

        // ---- melee --------------------------------------------------------------------------------------------

        [Test]
        public void Melee_StepBiteFaceStagger()
        {
            var b = Chasing();
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Senses(Unbitten)));
            Assert.AreEqual(MonsterAct.Bite, MonsterBrain.Tick(ref b, Senses(Bitten)), "cooldown up, in reach");
            b.Cooldown = 1f;
            Assert.AreEqual(MonsterAct.Face, MonsterBrain.Tick(ref b, Senses(1f)), "in reach, cooling down");
            Assert.AreEqual(1f - Dt, b.Cooldown, "the cooldown counts down");
            Assert.AreEqual(MonsterAct.Stagger, MonsterBrain.Tick(ref b, Senses(1f, busy: true)));
            Assert.AreEqual(MonsterAct.Stagger, MonsterBrain.Tick(ref b, Senses(8f, busy: true)), "a stagger holds it, near or far");
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Senses(1f, onFoot: false)), "you're in your ship");
            Assert.AreEqual(MonsterState.Chase, b.State, "still after you");

            var due = Chasing();
            due.Cooldown = Dt * 0.5f;
            Assert.AreEqual(MonsterAct.Bite, MonsterBrain.Tick(ref due, Senses(1f)), "this tick's dt finishes the cooldown");
        }

        [Test]
        public void NotRanged_IgnoresTheThrowerFields()
        {
            // The thrower's numbers all set, a line on you, no cooldowns: without Ranged it's the plain melee chase.
            foreach (var distance in new[] { 0.5f, 1f, 2f, 4f, 6f, 10f, 14f, 30f })
            foreach (var busy in new[] { false, true })
            {
                var melee = Chasing();
                var plain = Chasing();
                var s = Thrower(distance, busy: busy);
                s.Ranged = false;
                Assert.IsFalse(melee.WantsSight(s));
                var act = MonsterBrain.Tick(ref melee, s);
                Assert.AreEqual(MonsterBrain.Tick(ref plain, Senses(distance, busy: busy)), act, $"{distance} m, busy {busy}");
                Assert.Less(act, MonsterAct.Retreat, "no thrower acts");
                Assert.IsFalse(melee.Winding);
            }
        }

        // ---- a thrower ----------------------------------------------------------------------------------------

        [Test]
        public void Ranged_KeepsItsBand()
        {
            var b = Chasing();
            b.CastCooldown = 100f;   // not ready: just the band
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Thrower(Max + 0.5f)), "past RangeMax: closes in");
            Assert.AreEqual(MonsterAct.Strafe, MonsterBrain.Tick(ref b, Thrower(Max)), "in the band: circles");
            Assert.AreEqual(MonsterAct.Strafe, MonsterBrain.Tick(ref b, Thrower(Min)));
            Assert.AreEqual(MonsterAct.Retreat, MonsterBrain.Tick(ref b, Thrower(Min - 0.5f)), "inside RangeMin: backs off");
            Assert.AreEqual(MonsterAct.Retreat, MonsterBrain.Tick(ref b, Thrower(Unbitten)));
            Assert.AreEqual(MonsterAct.Bite, MonsterBrain.Tick(ref b, Thrower(Bitten)), "in reach it still bites");
            b.Cooldown = 1f;   // the presenter's bite sets it
            Assert.AreEqual(MonsterAct.Retreat, MonsterBrain.Tick(ref b, Thrower(Bitten)), "cooling down from the bite: backs off");
            Assert.AreEqual(MonsterAct.Stagger, MonsterBrain.Tick(ref b, Thrower(8f, busy: true)));
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Thrower(8f, onFoot: false)));
        }

        [Test]
        public void Ranged_ReadyThrow()
        {
            // Ready, a line on you, not from too close (CastNearest x RangeMin) and not in reach: it casts.
            Assert.AreEqual(MonsterAct.Cast, TickReady(8f));
            Assert.AreEqual(MonsterAct.Cast, TickReady(Max));
            Assert.AreEqual(MonsterAct.Cast, TickReady(Min * MonsterBrain.CastNearest + 0.1f), "inside RangeMin but not too close: throws first");
            Assert.AreEqual(MonsterAct.Retreat, TickReady(Min * MonsterBrain.CastNearest - 0.1f), "too close: backs off first");
            Assert.AreEqual(MonsterAct.Bite, TickReady(Bitten), "in reach: bites, not throws");
            Assert.AreEqual(MonsterAct.Step, TickReady(Max + 0.1f));
            // Ready with no line on you: walks in for one, down to SeekNearest x RangeMin, then circles.
            Assert.AreEqual(MonsterAct.Step, TickReady(8f, sees: false));
            Assert.AreEqual(MonsterAct.Strafe, TickReady(Min * MonsterBrain.SeekNearest - 0.1f, sees: false));
            Assert.AreEqual(MonsterAct.Retreat, TickReady(Min - 0.5f, sees: false));
        }

        static MonsterAct TickReady(float distance, bool sees = true)
        {
            var b = Chasing();
            return MonsterBrain.Tick(ref b, Thrower(distance, sees));
        }

        [Test]
        public void Ranged_CastChannelThrow_ThenCooldown()
        {
            var b = Chasing();
            Assert.AreEqual(MonsterAct.Cast, MonsterBrain.Tick(ref b, Thrower(8f)));
            Assert.IsTrue(b.Winding);
            Assert.AreEqual(0f, b.CastClock);

            var channel = 0;
            MonsterAct act;
            while ((act = MonsterBrain.Tick(ref b, Thrower(8f, sees: false))) == MonsterAct.Channel) Assert.Less(++channel, 100);
            Assert.AreEqual(MonsterAct.Throw, act, "the wind-up runs to its throw, line of sight or not");
            Assert.AreEqual((int)(CastTime / Dt) - 1, channel, "Channel until CastTime, Throw on the tick it's reached");
            Assert.IsFalse(b.Winding);
            Assert.AreEqual(Wait, b.CastCooldown, "CastCooldown restarts on the throw");

            var waited = 0;
            while ((act = MonsterBrain.Tick(ref b, Thrower(8f))) != MonsterAct.Cast)
            {
                Assert.AreEqual(MonsterAct.Strafe, act, "circles you while it cools down");
                Assert.Less(++waited, 1000);
            }
            Assert.AreEqual((int)(Wait / Dt) - 1, waited, "casts again the tick the cooldown runs out");
        }

        [Test]
        public void Ranged_StaggerBreaksTheWindUp()
        {
            var b = WindingUp();
            Assert.AreEqual(MonsterAct.Stagger, MonsterBrain.Tick(ref b, Thrower(8f, busy: true)));
            AssertInterrupted(b);
            Assert.AreEqual(MonsterAct.Strafe, MonsterBrain.Tick(ref b, Thrower(8f)), "no throw straight after");
        }

        [Test]
        public void Ranged_LeavingChaseBreaksTheWindUp()
        {
            var lost = WindingUp();
            Assert.AreEqual(MonsterAct.Channel, MonsterBrain.Tick(ref lost, Thrower(Sight * 3f + 1f)), "this tick's act still happens");
            Assert.AreEqual(MonsterState.Wander, lost.State);
            AssertInterrupted(lost);

            var scared = WindingUp();
            MonsterBrain.Fire(ref scared, MonsterCue.Scared);
            AssertInterrupted(scared);

            var killed = WindingUp();
            MonsterBrain.Fire(ref killed, MonsterCue.Killed);
            Assert.IsFalse(killed.Winding);
        }

        [Test]
        public void Ranged_OffFootBreaksTheWindUp()
        {
            var b = WindingUp();
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref b, Thrower(8f, onFoot: false)));
            AssertInterrupted(b);
            Assert.AreEqual(MonsterState.Chase, b.State);
        }

        [Test]
        public void Ranged_InterruptKeepsALongerCooldown()
        {
            var b = WindingUp();
            b.CastCooldown = 5f;
            MonsterBrain.Fire(ref b, MonsterCue.LostTarget);
            Assert.AreEqual(5f, b.CastCooldown);
        }

        /// <summary>A thrower one Channel tick into a wind-up.</summary>
        static MonsterBrain WindingUp()
        {
            var b = Chasing();
            Assert.AreEqual(MonsterAct.Cast, MonsterBrain.Tick(ref b, Thrower(8f)));
            Assert.AreEqual(MonsterAct.Channel, MonsterBrain.Tick(ref b, Thrower(8f)));
            return b;
        }

        static void AssertInterrupted(in MonsterBrain b)
        {
            Assert.IsFalse(b.Winding, "the wind-up goes nowhere");
            Assert.GreaterOrEqual(b.CastCooldown, MonsterBrain.CastInterrupted - Dt, "and a moment before the next");
        }

        [Test]
        public void WantsSight_AgreesWithCast()
        {
            // Every combination the presenter can meet: WantsSight (asked before Tick, so it only casts the sight ray
            // when it matters) must be true exactly when Tick with a line on you would Cast.
            var states = new[] { MonsterState.Chase, MonsterState.Wander, MonsterState.Standoff, MonsterState.Calm, MonsterState.Flee, MonsterState.Dying };
            var cooldowns = new[] { -1f, 0f, Dt * 0.5f, Dt, Dt * 1.5f, 2f };
            var checkedCasts = 0;
            foreach (var state in states)
            foreach (var castCooldown in cooldowns)
            foreach (var winding in new[] { false, true })
            foreach (var busy in new[] { false, true })
            foreach (var onFoot in new[] { false, true })
            for (var distance = 0.25f; distance <= 20f; distance += 0.25f)
            {
                var b = new MonsterBrain { State = state, CastCooldown = castCooldown, CalmUntil = 100f };
                if (winding && state == MonsterState.Chase) b.Marks |= MonsterMark.Winding;
                var s = Thrower(distance, sees: true, onFoot: onFoot, busy: busy);
                s.OnFootFor = state == MonsterState.Wander ? 0f : 10f;   // a wanderer that notices you this tick is the next tick's thrower
                var wants = b.WantsSight(s);
                var act = MonsterBrain.Tick(ref b, s);
                Assert.AreEqual(act == MonsterAct.Cast, wants,
                                $"{state}, cast cooldown {castCooldown}, winding {winding}, busy {busy}, on foot {onFoot}, {distance} m: {act}");
                if (wants) checkedCasts++;
            }
            Assert.Greater(checkedCasts, 0);
        }

        [Test]
        public void WantsSight_FalseMeansNoCastWithoutSight()
        {
            for (var distance = 0.25f; distance <= 20f; distance += 0.25f)
            {
                var b = Chasing();
                Assert.AreNotEqual(MonsterAct.Cast, MonsterBrain.Tick(ref b, Thrower(distance, sees: false)), $"{distance} m");
                Assert.IsFalse(b.Winding);
            }
        }

        // ---- Threat (the monsters left) -----------------------------------------------------------------------

        [Test]
        public void Threat_LeavesTheMomentItFleesOrDies()
        {
            var b = MonsterBrain.Spawn(false);
            Assert.IsTrue(b.Threat);
            foreach (var cue in new[] { MonsterCue.Challenged, MonsterCue.LetPass, MonsterCue.Calmed, MonsterCue.ShotAt })
            {
                MonsterBrain.Fire(ref b, cue, 10f);
                Assert.IsTrue(b.Threat, $"{b.State} is still one left");
            }
            MonsterBrain.Fire(ref b, MonsterCue.Scared);
            Assert.IsFalse(b.Threat, "scared off: not one left, though it's still running");
            MonsterBrain.Fire(ref b, MonsterCue.ShotAt);
            Assert.IsFalse(b.Threat, "shot at while running: still running");
            while (!b.Dead) MonsterBrain.Tick(ref b, Senses(5f));
            Assert.IsFalse(b.Threat, "vanished");

            var killed = Chasing();
            MonsterBrain.Fire(ref killed, MonsterCue.Killed);
            Assert.IsFalse(killed.Threat);
        }

        // ---- a Defend job's horde -----------------------------------------------------------------------------

        static MonsterSenses Horde(float distance, bool onFoot = true, bool ranged = false)
        {
            var s = ranged ? Thrower(distance, onFoot: onFoot) : Senses(distance, onFoot);
            s.Horde = true;
            return s;
        }

        [Test]
        public void Horde_SpawnsChasingTheBeacon()
        {
            var b = MonsterBrain.Horde();
            Assert.AreEqual(MonsterState.Chase, b.State);
            Assert.IsTrue(b.OnBeacon);
            Assert.IsTrue(b.Threat);
            Assert.IsTrue(b.Hunting);
        }

        [Test]
        public void TargetBeacon_TheNearer_WithAMarginBeforeItSwitches()
        {
            var b = MonsterBrain.Horde();
            Assert.IsFalse(MonsterBrain.TargetBeacon(ref b, 10f, true, 30f), "you're far nearer: you");
            b = MonsterBrain.Horde();
            Assert.IsTrue(MonsterBrain.TargetBeacon(ref b, 10f, true, 11f), "you're nearer, but not by the margin: keeps the beacon");
            Assert.IsTrue(b.OnBeacon);
            Assert.IsFalse(MonsterBrain.TargetBeacon(ref b, 10f, true, 10f + MonsterBrain.SwitchMargin + 0.5f), "you're nearer by more than the margin: you");
            Assert.IsFalse(b.OnBeacon);
            Assert.IsFalse(MonsterBrain.TargetBeacon(ref b, 10f, true, 9f), "the beacon's a little nearer: keeps on at you");
            Assert.IsTrue(MonsterBrain.TargetBeacon(ref b, 10f, true, 10f - MonsterBrain.SwitchMargin - 0.5f), "the beacon's nearer by the margin: back to it");
        }

        [Test]
        public void TargetBeacon_YouInYourShip_OnlyTheBeacon()
        {
            var b = MonsterBrain.Horde();
            Assert.IsFalse(MonsterBrain.TargetBeacon(ref b, 1f, true, 30f));
            Assert.IsTrue(MonsterBrain.TargetBeacon(ref b, 1f, false, 30f), "you're not about: the beacon, however near you are");
            Assert.IsTrue(b.OnBeacon);
        }

        [Test]
        public void TargetBeacon_KeepsItsOtherMarks()
        {
            var b = MonsterBrain.Horde();
            b.Marks |= MonsterMark.Winding;
            MonsterBrain.TargetBeacon(ref b, 1f, true, 30f);
            Assert.IsTrue(b.Winding, "switching targets doesn't break a wind-up");
            MonsterBrain.TargetBeacon(ref b, 30f, true, 1f);
            Assert.IsTrue(b.Winding);
            Assert.IsTrue(b.OnBeacon);
        }

        [Test]
        public void Horde_KeepsComing_WithYouInYourShip()
        {
            var b = MonsterBrain.Horde();
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Horde(8f, onFoot: false)), "after the beacon: on foot or not");
            Assert.AreEqual(MonsterAct.Bite, MonsterBrain.Tick(ref b, Horde(Bitten, onFoot: false)));
            Assert.AreEqual(MonsterState.Chase, b.State);

            var plain = Chasing();
            Assert.AreEqual(MonsterAct.Amble, MonsterBrain.Tick(ref plain, Senses(8f, onFoot: false)), "not a horde one: it ambles");
        }

        [Test]
        public void Horde_NeverLosesItsTarget()
        {
            var b = MonsterBrain.Horde();
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Horde(Sight * 3f + 5f)));
            Assert.AreEqual(MonsterState.Chase, b.State, "a horde monster from far off: still coming");

            var plain = Chasing();
            MonsterBrain.Tick(ref plain, Senses(Sight * 3f + 5f));
            Assert.AreEqual(MonsterState.Wander, plain.State);
        }

        [Test]
        public void Horde_ThrowerKeepsItsBand_AndThrowsWithYouInYourShip()
        {
            var b = MonsterBrain.Horde();
            Assert.AreEqual(MonsterAct.Step, MonsterBrain.Tick(ref b, Horde(Max + 3f, onFoot: false, ranged: true)), "past the band: closes in");
            Assert.AreEqual(MonsterAct.Retreat, MonsterBrain.Tick(ref b, Horde(Min * 0.5f, onFoot: false, ranged: true)), "well inside it: backs off");
            var s = Horde((Min + Max) * 0.5f, onFoot: false, ranged: true);
            Assert.IsTrue(b.WantsSight(s), "a throw's ready at the beacon: it asks for the line");
            Assert.AreEqual(MonsterAct.Cast, MonsterBrain.Tick(ref b, s));
            var act = MonsterAct.Cast;
            for (var t = 0f; t <= CastTime + Dt; t += Dt)
            {
                act = MonsterBrain.Tick(ref b, s);
                if (act == MonsterAct.Throw) break;
            }
            Assert.AreEqual(MonsterAct.Throw, act, "the wind-up runs to its throw, at the beacon, with you in your ship");
        }

        [Test]
        public void Horde_FleesWhenScattered()
        {
            var b = MonsterBrain.Horde();
            MonsterBrain.Fire(ref b, MonsterCue.Scared);
            Assert.AreEqual(MonsterState.Flee, b.State);
            Assert.IsFalse(b.Threat, "scattered: not one of the horde left");
            Assert.AreEqual(MonsterAct.Run, MonsterBrain.Tick(ref b, Horde(5f)));
            while (!b.Dead) MonsterBrain.Tick(ref b, Horde(5f));
            Assert.IsTrue(b.Dead, "and gone");
        }
    }
}
