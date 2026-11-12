using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>What a rogue's pilot is doing (the page's target card shows it).</summary>
    public enum RogueState : byte { Patrol, Approach, Attack, BreakOff, Flank, Evade, Retreat }

    /// <summary>What a rogue knows this frame (Dogfight fills it).</summary>
    public struct RogueSenses
    {
        public Vector3 Position, Forward, Home;
        /// <summary>You, your velocity and nose; where you'll be when its bolt gets there.</summary>
        public Vector3 Player, PlayerVelocity, PlayerForward, Aim;
        public float Distance, HullShare, Time;
        public bool PlayerAlive, ShieldDown, ShieldFull, Hit;
    }

    /// <summary>Attack runs at once (DogfightConfig.attackers): a rogue takes one to start a run and gives it back after.</summary>
    public sealed class AttackTokens
    {
        public int Max = 2, Used;
        public bool TryTake()
        {
            if (Used >= Max) return false;
            Used++;
            return true;
        }
        public void Give() => Used = math.max(0, Used - 1);
    }

    /// <summary>
    /// A rogue's pilot: a small state machine (content/dogfight.txtpb rogues: aggro_range, attack_seconds,
    /// aggression, evade_hull, retreat_hull). Each frame Think says where it wants to be (the goal; SpaceNav gets it
    /// there round the station), how fast (a share of its speed) and whether it may fire.
    ///
    /// - Patrol: circles where it came in, until you're within aggro_range or you shoot it.
    /// - Approach: closes on you; in range it starts a run if there's a token free, else flanks.
    /// - Attack: the run: at where you'll be (the lead), firing; past `pass` metres or after attack_seconds, breaks off.
    /// - BreakOff: overshoots out to one side, then (aggression) goes straight back in, or flanks.
    /// - Flank: swings round to your side and behind, so the next run comes from somewhere you're not looking.
    /// - Evade: hit with its shield down and its hull below evade_hull: jinks (a new heading every half second).
    /// - Retreat: below retreat_hull with its shield down: runs out, and comes back once the shield's up again.
    /// </summary>
    public sealed class RogueBrain
    {
        readonly RogueShip _t;
        readonly AttackTokens _tokens;
        float _until, _jinkAt;
        Vector3 _point;
        int _side;
        bool _token;

        public RogueState State { get; private set; } = RogueState.Patrol;

        /// <summary>Set: it patrols until you're this close (m), not its type's aggro_range (rogues lying in wait).</summary>
        public float AggroOverride;

        public RogueBrain(RogueShip type, AttackTokens tokens, ref Random rng)
        {
            _t = type;
            _tokens = tokens;
            _side = rng.NextBool() ? 1 : -1;
        }

        float Close => math.max(20f, _t.Close);
        float Aggro => AggroOverride > 0f ? AggroOverride : _t.AggroRange > 0f ? _t.AggroRange : 800f;

        /// <summary>Where to go, how fast (x its speed), and whether it may shoot (if it's lined up).</summary>
        public (Vector3 goal, float speed, bool fire) Think(in RogueSenses s, ref Random rng)
        {
            // Changes that can come from any state.
            if (!s.PlayerAlive)
            {
                if (State != RogueState.Patrol) Go(RogueState.Patrol, s, 0f);
            }
            else if (State == RogueState.Patrol)
            {
                if (s.Distance < Aggro || s.Hit) Go(RogueState.Approach, s, 0f);
            }
            else if (_t.RetreatHull > 0f && s.HullShare < _t.RetreatHull && s.ShieldDown && State != RogueState.Retreat)
                Go(RogueState.Retreat, s, 8f);
            else if (s.Hit && s.ShieldDown && s.HullShare < _t.EvadeHull &&
                     State is RogueState.Attack or RogueState.Approach or RogueState.Flank)
                Go(RogueState.Evade, s, rng.NextFloat(1.4f, 2.4f));

            switch (State)
            {
                case RogueState.Patrol:
                {
                    // A slow circle round where it came in.
                    var a = s.Time * 0.25f + _side;
                    return (s.Home + new Vector3(math.cos(a), 0.15f * math.sin(a * 0.7f), math.sin(a)) * (Close * 2f), 0.55f, false);
                }
                case RogueState.Approach:
                    if (s.Distance < math.max(Close * 2f, _t.FireRange))
                    {
                        if (TakeToken()) Go(RogueState.Attack, s, Run());
                        else Go(RogueState.Flank, s, rng.NextFloat(4f, 6f));
                    }
                    return (s.Player, 1.2f, false);

                case RogueState.Attack:
                    if (s.Distance < math.max(10f, _t.Pass) || s.Time > _until)
                    {
                        // Past you, then up and over (or down and under) and out to one side: a climbing break that
                        // RogueFlight flies as a bank and pull, so it comes round in an arc rather than flat.
                        var side = Vector3.Cross(s.Forward, Vector3.up);
                        if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
                        var vertical = rng.NextFloat() < 0.7f ? 1f : -1f;
                        _point = s.Position + s.Forward * (Close * 2f) + Vector3.up * (vertical * Close * 2.5f) +
                                 side.normalized * (_side * Close * 1.5f);
                        _side = -_side;   // the other way next time
                        Go(RogueState.BreakOff, s, rng.NextFloat(1.5f, 2.6f));
                        return (_point, 1.25f, false);
                    }
                    {
                        // Weave on the way in (harder to hit), steadying up as it closes to shoot.
                        var across = Vector3.Cross(s.Player - s.Position, Vector3.up);
                        if (across.sqrMagnitude < 1e-3f) across = Vector3.right;
                        var weave = math.saturate((s.Distance - Close * 2f) / (Close * 4f));
                        var aim = s.Aim + across.normalized * (math.sin(s.Time * 1.3f + _side * 2f) * Close * 0.8f * weave) +
                                  Vector3.up * (math.sin(s.Time * 0.9f + _side) * Close * 0.4f * weave);
                        return (aim, s.Distance > Close * 3f ? 1.25f : s.Distance < Close ? 0.75f : 1f, true);
                    }

                case RogueState.BreakOff:
                    if (s.Time > _until)
                    {
                        if (rng.NextFloat() < _t.Aggression && TakeToken()) Go(RogueState.Attack, s, Run());
                        else Go(RogueState.Flank, s, rng.NextFloat(4f, 6f));
                    }
                    return (_point, 1.25f, false);

                case RogueState.Flank:
                {
                    // Round to your side and behind you (it follows you as you turn).
                    var right = Vector3.Cross(Vector3.up, s.PlayerForward);
                    if (right.sqrMagnitude < 1e-3f) right = Vector3.right;
                    var spot = s.Player + right.normalized * (_side * Close * 2.2f) - s.PlayerForward * (Close * 1.4f);
                    if (Vector3.Distance(s.Position, spot) < Close * 1.2f || s.Time > _until)
                    {
                        if (TakeToken()) Go(RogueState.Attack, s, Run());
                        else
                        {
                            _side = -_side;   // wait its turn on the other side
                            Go(RogueState.Flank, s, rng.NextFloat(3f, 5f));
                        }
                    }
                    return (spot, 1.15f, false);
                }

                case RogueState.Evade:
                    if (s.Time >= _jinkAt)
                    {
                        // A new heading, away from your nose.
                        _jinkAt = s.Time + 0.5f;
                        var away = Vector3.ProjectOnPlane((Vector3)rng.NextFloat3Direction(), s.PlayerForward);
                        _point = s.Position + (away.normalized + (s.Position - s.Player).normalized * 0.5f) * (Close * 2f);
                    }
                    if (s.Time > _until) Go(RogueState.Flank, s, rng.NextFloat(3f, 5f));
                    return (_point, 1.3f, false);

                case RogueState.Retreat:
                    if (s.ShieldFull || s.Time > _until) Go(RogueState.Approach, s, 0f);
                    return (s.Player + (s.Position - s.Player).normalized * (Aggro * 0.9f), 1.3f, false);
            }
            return (s.Player, 1f, false);
        }

        float Run() => _t.AttackSeconds > 0f ? _t.AttackSeconds : 4f;

        bool TakeToken()
        {
            if (_token) return true;
            _token = _tokens.TryTake();
            return _token;
        }

        void Go(RogueState next, in RogueSenses s, float seconds)
        {
            // The run's token goes back as soon as it's over.
            if (State == RogueState.Attack && next != RogueState.Attack) Release();
            State = next;
            _until = s.Time + seconds;
            _jinkAt = 0f;
        }

        /// <summary>Its token back (the run's over, or it's dead).</summary>
        public void Release()
        {
            if (!_token) return;
            _tokens.Give();
            _token = false;
        }
    }
}
