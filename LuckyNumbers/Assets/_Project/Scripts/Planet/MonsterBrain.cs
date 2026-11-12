namespace LuckyNumbers.Planet
{
    /// <summary>What a monster is doing (MonsterBrain.State).</summary>
    public enum MonsterState : byte
    {
        /// <summary>Ambling round where it started; notices you within `sight`.</summary>
        Wander,
        /// <summary>After you: closes to `reach` and bites when its cooldown's up; loses you at sight x 3.</summary>
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
    }

    /// <summary>A one-shot modifier riding along with any state.</summary>
    [System.Flags]
    public enum MonsterMark : byte
    {
        None = 0,
        /// <summary>Your next shot at it gets the surprise bonus (a won standoff fight); used up by that shot.</summary>
        Surprise = 1 << 0,
        /// <summary>Shot at while running off: the placeholder's chase hop, the rest of the way.</summary>
        Harried = 1 << 1,
    }

    /// <summary>What a monster's brain sees this tick (all from the presenter; no Unity objects).</summary>
    public struct MonsterSenses
    {
        public float Dt, Now;
        /// <summary>Flat distance to you (m).</summary>
        public float Distance;
        public bool OnFoot;
        /// <summary>Seconds since you stepped out (the grace before anything comes for you).</summary>
        public float OnFootFor;
        public float Sight, Reach, StandoffDistance;
        /// <summary>Mid-stagger (MonsterView.Busy).</summary>
        public bool Busy;
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
    /// </summary>
    public struct MonsterBrain
    {
        /// <summary>How long a scared monster runs before it's gone (s).</summary>
        public const float FleeFor = 3.5f;
        /// <summary>Your grace on foot before anything notices you / a standoff opens (s).</summary>
        public const float NoticeAfter = 4f, StandoffAfter = 1f;
        /// <summary>A won or lost standoff fight: at least this before its first bite (s).</summary>
        public const float RaiseWeapon = 0.8f;

        public MonsterState State;
        public MonsterMark Marks;
        /// <summary>Seconds in Flee / Dying.</summary>
        public float Clock;
        /// <summary>Calm: blind to you until this time (Time.time).</summary>
        public float CalmUntil;
        /// <summary>Seconds until it can bite again (counts down in every live state but Dying).</summary>
        public float Cooldown;

        public static MonsterBrain Spawn(bool chasing) => new() { State = chasing ? MonsterState.Chase : MonsterState.Wander };

        public readonly bool Dead => State == MonsterState.Dying;

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
            if (b.State != MonsterState.Chase || !s.OnFoot) return MonsterAct.Amble;
            var act = s.Busy ? MonsterAct.Stagger
                    : s.Distance > s.Reach * 0.85f ? MonsterAct.Step
                    : b.Cooldown <= 0f ? MonsterAct.Bite
                    : MonsterAct.Face;
            if (s.Distance > s.Sight * 3f) Fire(ref b, MonsterCue.LostTarget);   // this tick's act still happens
            return act;
        }
    }
}
