namespace LuckyNumbers.Planet
{
    /// <summary>What a monster is doing (MonsterBrain.State).</summary>
    public enum MonsterState : byte
    {
        /// <summary>Ambling round where it started; notices you within `sight`.</summary>
        Wander,
        /// <summary>
        /// After you: closes to `reach` and bites when its cooldown's up; loses you at sight x 3. A thrower
        /// (MonsterSenses.Ranged) keeps within its band instead, winding up and throwing when it can see you.
        /// </summary>
        Chase,
        /// <summary>An encounter: waits for you, and opens the card when you're within the standoff distance.</summary>
        Standoff,
        /// <summary>A standoff let you pass: ambles and never comes for you (unless shot at).</summary>
        Ignored,
        /// <summary>A standoff calmed it: ambles, blind to you until `CalmUntil`.</summary>
        Calm,
        /// <summary>Scared off: runs from you and is gone (Dying) after `FleeFor`.</summary>
        Flee,
        /// <summary>Killed or gone: falls / shrinks / sinks, then the presenter removes it. Final.</summary>
        Dying,
    }

    /// <summary>What changes a monster's state (MonsterBrain.Fire). Encounter outcomes are their own cues.</summary>
    public enum MonsterCue : byte
    {
        /// <summary>You came within `sight` (Wander only).</summary>
        Noticed,
        /// <summary>You got past sight x 3 (Chase only).</summary>
        LostTarget,
        /// <summary>A shot or a swing at it: it knows you're there (a running one keeps running).</summary>
        ShotAt,
        /// <summary>Picked for an encounter: it waits for you (Wander only).</summary>
        Challenged,
        /// <summary>A standoff's "ignored": lets you pass.</summary>
        LetPass,
        /// <summary>A standoff's "calm": blind to you until the given time.</summary>
        Calmed,
        /// <summary>Calm wore off (Calm only).</summary>
        CalmedDown,
        /// <summary>A standoff's "flees".</summary>
        Scared,
        /// <summary>A standoff's "ambushed": it goes for you at once (cooldown 0; the presenter bites).</summary>
        Ambushed,
        /// <summary>A standoff's "fight", the roll failed: a moment to raise your weapon.</summary>
        Fight,
        /// <summary>A standoff's "fight", the roll won: that moment, and your first shot gets the surprise bonus.</summary>
        FightWon,
        /// <summary>Ran far enough: gone (Flee only).</summary>
        Vanished,
        /// <summary>Out of HP.</summary>
        Killed,
    }

    /// <summary>
    /// A tick's action (MonsterBrain.Tick); the presenter carries it out (moving, the bite, the card, the fade).
    /// Stagger is the view's hit clip (MonsterView.Busy) passed in, not a state: the brain doesn't own that clock.
    /// </summary>
    public enum MonsterAct : byte
    {
        /// <summary>Dying: shrink / sink by `Clock`.</summary>
        Fade,
        /// <summary>Flee: runs from you.</summary>
        Run,
        /// <summary>Standoff: open its card (no move this tick).</summary>
        OpenStandoff,
        /// <summary>Not chasing (or chasing with you off foot): amble round home.</summary>
        Amble,
        /// <summary>Chase, staggered by a hit: turns, holds still.</summary>
        Stagger,
        /// <summary>Chase: turns and walks at you.</summary>
        Step,
        /// <summary>Chase, in reach, cooldown up: turns and bites.</summary>
        Bite,
        /// <summary>Chase, in reach, cooling down: turns.</summary>
        Face,
        /// <summary>A thrower (MonsterSenses.Ranged), chasing: inside `RangeMin`, backs off from you, facing you.</summary>
        Retreat,
        /// <summary>A thrower in its band, cast cooling down or no line of sight: circles you, facing you.</summary>
        Strafe,
        /// <summary>A thrower: starts its wind-up this tick (the presenter plays the cast clip). Holds still.</summary>
        Cast,
        /// <summary>A thrower mid wind-up: turns to you, holds still.</summary>
        Channel,
        /// <summary>A thrower's wind-up is done: the flask leaves its hand this tick (the presenter throws it).</summary>
        Throw,
    }

    /// <summary>A one-shot modifier riding along with any state.</summary>
    [System.Flags]
    public enum MonsterMark : byte
    {
        None = 0,
        /// <summary>Your next shot at it gets the surprise bonus (a won standoff fight); used up by that shot, or dropped when it leaves Chase.</summary>
        Surprise = 1 << 0,
        /// <summary>Shot at while running off: the placeholder's chase hop, the rest of the way.</summary>
        Harried = 1 << 1,
        /// <summary>A thrower winding up a throw (Chase only): `CastClock` counts up to its cast time, then it throws.</summary>
        Winding = 1 << 2,
        /// <summary>A horde monster (a Defend job's) after the beacon rather than you (MonsterBrain.TargetBeacon).</summary>
        OnBeacon = 1 << 3,
    }

    /// <summary>
    /// What a monster's brain sees this tick (all from the presenter; no Unity objects). "You" is its target: you, or
    /// for a horde monster after the beacon (MonsterMark.OnBeacon) the beacon, Distance then to the beacon's side.
    /// </summary>
    public struct MonsterSenses
    {
        public float Dt, Now;
        /// <summary>Flat distance to its target (m): you, or the beacon.</summary>
        public float Distance;
        public bool OnFoot;
        /// <summary>Seconds since you stepped out (the grace before anything comes for you).</summary>
        public float OnFootFor;
        public float Sight, Reach, StandoffDistance;
        /// <summary>Mid-stagger (MonsterView.Busy).</summary>
        public bool Busy;
        /// <summary>
        /// It throws (MonsterType.ranged): keeps between RangeMin and RangeMax (m) of you, winds up for CastTime and
        /// waits CastCooldown between throws (s). LineOfSight: nothing between it and you (only asked while a throw is
        /// ready; else it can be left false).
        /// </summary>
        public bool Ranged, LineOfSight;
        public float RangeMin, RangeMax, CastTime, CastCooldown;
        /// <summary>
        /// A Defend job's horde: it always has a target (the beacon when you're not about), so it never loses it nor
        /// stands about while you're in your ship.
        /// </summary>
        public bool Horde;
    }

    /// <summary>
    /// A monster's state machine (CombatPresenter): plain data plus static functions, blittable and with no managed
    /// references, so it's testable alone (and could go to Burst). Every change goes through Fire (cue -> Next ->
    /// Exit / Enter); Tick reads the senses and picks the tick's action. The dice and Unity side effects stay in the
    /// presenter.
    ///
    ///   Wander   --Noticed-->    Chase      --LostTarget--> Wander
    ///   Wander   --Challenged--> Standoff   (the card's outcome: LetPass Ignored, Calmed Calm, Scared Flee,
    ///                                         Ambushed / Fight / FightWon Chase)
    ///   Calm     --CalmedDown--> Wander     Flee --Vanished--> Dying
    ///   any live --ShotAt-->     Chase (Flee stays Flee)        any --Killed--> Dying (final)
    ///
    /// A thrower's Chase (Ranged) has its own acts, no new states: past RangeMax Step, inside RangeMin Retreat, else
    /// Strafe (Step while a throw is ready but it can't see you); with its cast cooled down and a line on you Cast (MonsterMark.Winding set, CastClock from 0), then
    /// Channel until CastTime, then Throw (CastCooldown restarts). A stagger, leaving Chase or you leaving your feet
    /// breaks a wind-up (CastInterrupted). In reach it still bites. Without Ranged nothing here changes.
    ///
    /// A Defend job's horde (MonsterSenses.Horde, spawned by <see cref="Horde"/>) chases whichever is nearer, you or the
    /// beacon (<see cref="TargetBeacon"/>, MonsterMark.OnBeacon): the presenter senses that one, so every act above is
    /// at it. It never loses its target and keeps on with you in your ship (the beacon's still there).
    /// </summary>
    public struct MonsterBrain
    {
        /// <summary>How long a scared monster runs before it's gone (s).</summary>
        public const float FleeFor = 3.5f;
        /// <summary>Your grace on foot before anything notices you / a standoff opens (s).</summary>
        public const float NoticeAfter = 4f, StandoffAfter = 1f;
        /// <summary>A won or lost standoff fight: at least this before its first bite (s).</summary>
        public const float RaiseWeapon = 0.8f;
        /// <summary>A wind-up broken by a hit (or by losing you): at least this before it tries again (s).</summary>
        public const float CastInterrupted = 1.2f;
        /// <summary>Closer than this x RangeMin it won't start a throw (it backs off first): the splash would be its own.</summary>
        public const float CastNearest = 0.6f;
        /// <summary>A throw ready with no line on you: it walks in for one, down to this x RangeMin.</summary>
        public const float SeekNearest = 1.15f;

        public MonsterState State;
        public MonsterMark Marks;
        /// <summary>Seconds in Flee / Dying.</summary>
        public float Clock;
        /// <summary>Calm: blind to you until this time (Time.time).</summary>
        public float CalmUntil;
        /// <summary>Seconds until it can bite again (counts down in every live state but Dying).</summary>
        public float Cooldown;
        /// <summary>A thrower: seconds until it can throw again (counts down like Cooldown).</summary>
        public float CastCooldown;
        /// <summary>A thrower mid wind-up (MonsterMark.Winding): seconds into it.</summary>
        public float CastClock;

        public readonly bool Winding => (Marks & MonsterMark.Winding) != 0;

        /// <summary>A horde monster closes on its target once the other is this much nearer (m): no dithering between two about as far.</summary>
        public const float SwitchMargin = 2f;

        public static MonsterBrain Spawn(bool chasing) => new() { State = chasing ? MonsterState.Chase : MonsterState.Wander };

        /// <summary>A horde monster: straight into Chase, after the beacon it turned up round.</summary>
        public static MonsterBrain Horde() => new() { State = MonsterState.Chase, Marks = MonsterMark.OnBeacon };

        /// <summary>A horde monster's going for the beacon (else you).</summary>
        public readonly bool OnBeacon => (Marks & MonsterMark.OnBeacon) != 0;

        /// <summary>
        /// A horde monster's target this tick: whichever of you (only while you're about: `you`, on foot) and the beacon
        /// is nearer, keeping the one it has until the other is SwitchMargin nearer. Distances flat, to your body and
        /// the beacon's side (m). Returns true for the beacon (MonsterMark.OnBeacon).
        /// </summary>
        public static bool TargetBeacon(ref MonsterBrain b, float toYou, bool you, float toBeacon)
        {
            var beacon = !you || (b.OnBeacon ? toYou >= toBeacon - SwitchMargin : toBeacon < toYou - SwitchMargin);
            b.Marks = beacon ? b.Marks | MonsterMark.OnBeacon : b.Marks & ~MonsterMark.OnBeacon;
            return beacon;
        }

        public readonly bool Dead => State == MonsterState.Dying;

        /// <summary>
        /// Still one of the monsters left (CombatState.alive): from the moment it's scared off (Flee) or dying it isn't,
        /// though it stays on the ground a while running or fading.
        /// </summary>
        public readonly bool Threat => State != MonsterState.Flee && State != MonsterState.Dying;

        /// <summary>After you, for the view (run clip, the placeholder's hop).</summary>
        public readonly bool Hunting => State == MonsterState.Chase || (Marks & MonsterMark.Harried) != 0;

        /// <summary>The surprise bonus, if it's due: true once, then it's used up.</summary>
        public static bool TakeSurprise(ref MonsterBrain b)
        {
            if ((b.Marks & MonsterMark.Surprise) == 0) return false;
            b.Marks &= ~MonsterMark.Surprise;
            return true;
        }

        /// <summary>The transition table: where `cue` takes a monster in state `from` (itself: no change).</summary>
        public static MonsterState Next(MonsterState from, MonsterCue cue)
        {
            if (from == MonsterState.Dying) return from;
            switch (cue)
            {
                case MonsterCue.Noticed: return from == MonsterState.Wander ? MonsterState.Chase : from;
                case MonsterCue.LostTarget: return from == MonsterState.Chase ? MonsterState.Wander : from;
                case MonsterCue.ShotAt: return from == MonsterState.Flee ? from : MonsterState.Chase;
                case MonsterCue.Challenged: return from == MonsterState.Wander ? MonsterState.Standoff : from;
                case MonsterCue.LetPass: return MonsterState.Ignored;
                case MonsterCue.Calmed: return MonsterState.Calm;
                case MonsterCue.CalmedDown: return from == MonsterState.Calm ? MonsterState.Wander : from;
                case MonsterCue.Scared: return MonsterState.Flee;
                case MonsterCue.Ambushed:
                case MonsterCue.Fight:
                case MonsterCue.FightWon: return MonsterState.Chase;
                case MonsterCue.Vanished: return from == MonsterState.Flee ? MonsterState.Dying : from;
                case MonsterCue.Killed: return MonsterState.Dying;
                default: return from;
            }
        }

        /// <summary>
        /// The one way a monster's state changes: the cue's own effect, then (if the state moves) Exit the old one and
        /// Enter the new. `calmUntil`: Calmed's end time. Returns whether the state moved.
        /// </summary>
        public static bool Fire(ref MonsterBrain b, MonsterCue cue, float calmUntil = 0f)
        {
            if (b.Dead) return false;
            switch (cue)
            {
                case MonsterCue.ShotAt:
                    if (b.State == MonsterState.Flee) b.Marks |= MonsterMark.Harried;
                    break;
                case MonsterCue.Ambushed:
                    b.Cooldown = 0f;
                    break;
                case MonsterCue.Fight:
                case MonsterCue.FightWon:
                    b.Marks = cue == MonsterCue.FightWon ? b.Marks | MonsterMark.Surprise : b.Marks & ~MonsterMark.Surprise;
                    if (b.Cooldown < RaiseWeapon) b.Cooldown = RaiseWeapon;
                    break;
                case MonsterCue.Calmed:
                    b.CalmUntil = calmUntil;
                    break;
            }
            var to = Next(b.State, cue);
            if (to == b.State) return false;
            Exit(ref b, b.State);
            b.State = to;
            Enter(ref b, to);
            return true;
        }

        static void Exit(ref MonsterBrain b, MonsterState from)
        {
            if (from == MonsterState.Calm) b.CalmUntil = 0f;
            if (from == MonsterState.Chase)
            {
                b.Marks &= ~MonsterMark.Surprise;   // lost you (or died): the fight you chose is over
                if (b.Winding) Interrupt(ref b);    // and a throw it was winding up goes nowhere
            }
        }

        /// <summary>A wind-up broken off (a hit, it lost you, it died): no throw, and a moment before the next.</summary>
        static void Interrupt(ref MonsterBrain b)
        {
            b.Marks &= ~MonsterMark.Winding;
            if (b.CastCooldown < CastInterrupted) b.CastCooldown = CastInterrupted;
        }

        static void Enter(ref MonsterBrain b, MonsterState to)
        {
            if (to == MonsterState.Flee || to == MonsterState.Dying) b.Clock = 0f;
        }

        /// <summary>A tick: timers, the transitions the senses call for, and what to do.</summary>
        public static MonsterAct Tick(ref MonsterBrain b, in MonsterSenses s)
        {
            if (b.State == MonsterState.Dying)
            {
                b.Clock += s.Dt;
                return MonsterAct.Fade;
            }
            b.Cooldown -= s.Dt;
            b.CastCooldown -= s.Dt;
            if (b.State == MonsterState.Flee)
            {
                b.Clock += s.Dt;
                if (b.Clock > FleeFor) Fire(ref b, MonsterCue.Vanished);   // this tick's run still happens
                return MonsterAct.Run;
            }
            if (b.State == MonsterState.Standoff && s.OnFoot && s.OnFootFor > StandoffAfter && s.Distance < s.StandoffDistance)
                return MonsterAct.OpenStandoff;
            if (b.State == MonsterState.Calm && s.Now >= b.CalmUntil) Fire(ref b, MonsterCue.CalmedDown);
            if (b.State == MonsterState.Wander && s.OnFoot && s.OnFootFor > NoticeAfter && s.Distance < s.Sight) Fire(ref b, MonsterCue.Noticed);
            if (b.State != MonsterState.Chase || !(s.OnFoot || s.Horde))
            {
                if (b.Winding) Interrupt(ref b);   // you got back in your ship mid-throw
                return MonsterAct.Amble;
            }
            var act = s.Ranged ? Ranged(ref b, s)
                    : s.Busy ? MonsterAct.Stagger
                    : s.Distance > s.Reach * 0.85f ? MonsterAct.Step
                    : b.Cooldown <= 0f ? MonsterAct.Bite
                    : MonsterAct.Face;
            if (!s.Horde && s.Distance > s.Sight * 3f) Fire(ref b, MonsterCue.LostTarget);   // this tick's act still happens
            return act;
        }

        /// <summary>
        /// A thrower's chase: a wind-up runs to its throw (a stagger breaks it); in reach it bites when it can; past
        /// RangeMax it closes in; a throw that's ready and has a line on you starts (not from too close); inside
        /// RangeMin it backs off; a throw that's ready without a line on you walks in for one; else it circles you
        /// while the cast cools down.
        /// </summary>
        static MonsterAct Ranged(ref MonsterBrain b, in MonsterSenses s)
        {
            if (s.Busy)
            {
                if (b.Winding) Interrupt(ref b);
                return MonsterAct.Stagger;
            }
            if (b.Winding)
            {
                b.CastClock += s.Dt;
                if (b.CastClock < s.CastTime) return MonsterAct.Channel;
                b.Marks &= ~MonsterMark.Winding;
                b.CastCooldown = s.CastCooldown;
                return MonsterAct.Throw;
            }
            var bite = s.Distance <= s.Reach * 0.85f;
            if (bite && b.Cooldown <= 0f) return MonsterAct.Bite;
            if (s.Distance > s.RangeMax) return MonsterAct.Step;
            var ready = b.CastCooldown <= 0f && !bite && s.Distance >= s.RangeMin * CastNearest;
            if (ready && s.LineOfSight)
            {
                b.Marks |= MonsterMark.Winding;
                b.CastClock = 0f;
                return MonsterAct.Cast;
            }
            if (s.Distance < s.RangeMin) return MonsterAct.Retreat;
            // Ready but something's in the way (a hill, another monster): it closes in for a clear line.
            return ready && s.Distance > s.RangeMin * SeekNearest ? MonsterAct.Step : MonsterAct.Strafe;
        }

        /// <summary>Whether a thrower would start a throw this tick if it could see you (so the presenter only casts the sight ray then).</summary>
        public readonly bool WantsSight(in MonsterSenses s) =>
            s.Ranged && State == MonsterState.Chase && (s.OnFoot || s.Horde) && !Winding && !s.Busy && CastCooldown - s.Dt <= 0f &&
            s.Distance <= s.RangeMax && s.Distance >= s.RangeMin * CastNearest && s.Distance > s.Reach * 0.85f;
    }
}
