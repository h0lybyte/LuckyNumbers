using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>What a rogue's pilot is doing (the page's target card shows it).</summary>
    public enum RogueState : byte { Patrol, Approach, Attack, BreakOff, Flank, Evade, Retreat, Break }

    /// <summary>What a rogue knows this frame (Dogfight fills it).</summary>
    public struct RogueSenses
    {
        public Vector3 Position, Forward, Velocity, Home;
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
    /// there round the station), the velocity it wants (Steering: arrive, lead, orbit; RogueFlight flies it) and whether
    /// it may fire.
    ///
    /// - Patrol: orbits where it came in (no faster than its turn rate allows for that circle), until you're within
    ///   aggro_range or you shoot it.
    /// - Approach: closes on where you'll be (a short, capped lead); in range it starts a run if there's a token free,
    ///   else flanks.
    /// - Attack: the run: at where you'll be (the lead), firing; past `pass` metres or after attack_seconds, breaks off.
    /// - BreakOff: overshoots out to one side, then (aggression) goes straight back in, or flanks.
    /// - Flank: swings round to a slot at your side and behind, arriving there (braking in time and matching your
    ///   velocity, so it settles in the slot rather than overshooting), so the next run comes from somewhere you're
    ///   not looking.
    /// - Evade: hit with its shield down and its hull below evade_hull: jinks (a new heading every half second).
    /// - Retreat: below retreat_hull with its shield down: runs out, and comes back once the shield's up again.
    /// - Break (FreeSpace 2's "get away"): you've been on its tail, nose on it, for its reaction time: a hard break turn
    ///   across your line of fire and out, then flank.
    ///
    /// On top of any state: lined up in your sights (inside 8 degrees, 450 m) for its reaction time, it sidesteps
    /// across your line of fire for a moment (harder the better the pilot). An Approach that drags on (a turning fight
    /// it isn't winning) extends: straight out to gain room, then back in head on. `skill` sets the reaction time,
    /// the dodge and (in Dogfight) the aim.
    /// </summary>
    public sealed class RogueBrain
    {
        readonly RogueShip _t;
        readonly AttackTokens _tokens;
        float _until, _jinkAt;
        Vector3 _point;
        int _side;
        bool _token;
        float _tailSince = -1f, _sightSince = -1f, _dodgeUntil, _dodgeNext;
        Vector3 _dodge;

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
        /// <summary>Its pilot's skill, 0..1 (unset reads as 0.5).</summary>
        public float Skill => _t.Skill > 0f ? math.saturate(_t.Skill) : 0.5f;
        /// <summary>Seconds before it reacts to you on its tail or lining it up.</summary>
        float Reaction => math.lerp(0.9f, 0.2f, Skill);
        /// <summary>
        /// Where a run breaks off: `pass`, but never closer than it can turn away from (its turn radius, speed over turn
        /// rate, plus the ground it covers rolling into the bank, about 0.3 s, and room for its hull), so a run passes
        /// close rather than through you.
        /// </summary>
        float Pass => math.max(math.max(10f, _t.Pass),
                               Speed / math.radians(math.max(10f, _t.TurnRate)) * 1.2f + Speed * 0.3f + math.max(0f, _t.Length));
        /// <summary>How long it closes on you before it gives up on a turning fight and extends.</summary>
        const float ApproachSeconds = 10f;
        float Speed => math.max(1f, _t.Speed);
        /// <summary>What arrival brakes at: a little under what RogueFlight can do, for the lag in its turns.</summary>
        float Brake => RogueFlight.Accel(Speed * 1.3f) * 0.6f;

        /// <summary>Straight at a point, at `pace` x its speed (fly through it, no braking).</summary>
        (Vector3, Vector3, bool) At(in RogueSenses s, Vector3 point, float pace, bool fire)
        {
            var to = point - s.Position;
            var dir = to.sqrMagnitude > 1e-4f ? to.normalized : s.Forward;
            return (point, dir * (Speed * pace), fire);
        }
        float Aggro => AggroOverride > 0f ? AggroOverride : _t.AggroRange > 0f ? _t.AggroRange : 800f;

        /// <summary>
        /// Where it's headed (for routing round the station), the velocity it wants, and whether it may shoot (if it's
        /// lined up).
        /// </summary>
        public (Vector3 goal, Vector3 velocity, bool fire) Think(in RogueSenses s, ref Random rng)
        {
            var (goal, velocity, fire) = Decide(s, ref rng);
            if (!s.PlayerAlive || State is RogueState.Patrol or RogueState.Retreat) return (goal, velocity, fire);

            // In your sights: a sidestep across your line of fire, now and then, while it lasts.
            var fromYou = s.Position - s.Player;
            var inSights = s.Distance < 450f && Vector3.Angle(s.PlayerForward, fromYou) < 8f;
            _sightSince = inSights ? _sightSince < 0f ? s.Time : _sightSince : -1f;
            if (inSights && s.Time - _sightSince > Reaction && s.Time > _dodgeNext)
            {
                var across = Vector3.ProjectOnPlane((Vector3)rng.NextFloat3Direction(), s.PlayerForward);
                _dodge = across.sqrMagnitude > 1e-4f ? across.normalized : Vector3.up;
                _dodgeUntil = s.Time + rng.NextFloat(0.5f, 0.9f);
                _dodgeNext = _dodgeUntil + rng.NextFloat(0.4f, 1.2f);
            }
            if (s.Time < _dodgeUntil)
            {
                // Eased in and out over the sidestep, so it slides rather than snaps.
                var left = (_dodgeUntil - s.Time) / 0.7f;
                var ease = math.sin(math.saturate(left) * math.PI);
                velocity += _dodge * (Speed * math.lerp(0.35f, 0.8f, Skill) * ease);
            }
            return (goal, velocity, fire);
        }

        (Vector3 goal, Vector3 velocity, bool fire) Decide(in RogueSenses s, ref Random rng)
        {
            // You on its tail with your nose on it, close enough to matter.
            var fromYou = s.Position - s.Player;
            var onTail = s.PlayerAlive && s.Distance < 500f && Vector3.Dot(s.Forward, fromYou) > 0.5f * s.Distance &&
                         Vector3.Angle(s.PlayerForward, fromYou) < 20f;
            _tailSince = onTail ? _tailSince < 0f ? s.Time : _tailSince : -1f;

            // Changes that can come from any state.
            if (!s.PlayerAlive)
            {
                if (State != RogueState.Patrol) Go(RogueState.Patrol, s, 0f);
            }
            else if (State == RogueState.Patrol)
            {
                if (s.Distance < Aggro || s.Hit) Go(RogueState.Approach, s, ApproachSeconds);
            }
            else if (_t.RetreatHull > 0f && s.HullShare < _t.RetreatHull && s.ShieldDown && State != RogueState.Retreat)
                Go(RogueState.Retreat, s, 8f);
            else if (s.Hit && s.ShieldDown && s.HullShare < _t.EvadeHull &&
                     State is RogueState.Attack or RogueState.Approach or RogueState.Flank)
                Go(RogueState.Evade, s, rng.NextFloat(1.4f, 2.4f));
            else if (onTail && s.Time - _tailSince > Reaction &&
                     State is RogueState.Approach or RogueState.Flank or RogueState.BreakOff)
            {
                // Break: hard across your line of fire (the side it's already on), a little up or down, and out.
                var across = Vector3.ProjectOnPlane(fromYou, s.PlayerForward);
                var side = Vector3.Cross(s.PlayerForward, Vector3.up);
                if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
                var dir = across.sqrMagnitude > 1f ? across.normalized : side.normalized * _side;
                _point = s.Position + dir * (Close * 3f) + s.Forward * (Close * 1.5f) +
                         Vector3.up * (rng.NextFloat(-1f, 1f) * Close);
                _tailSince = -1f;
                Go(RogueState.Break, s, rng.NextFloat(1.2f, 2f));
            }

            switch (State)
            {
                case RogueState.Patrol:
                {
                    // A slow orbit round where it came in.
                    var v = Steering.Orbit(s.Position, s.Home, Vector3.zero, Vector3.up, Close * 2f, Speed * 0.55f,
                                           _t.TurnRate, 0.4f, _side);
                    return (s.Position + v * 1.5f, v, false);
                }
                case RogueState.Approach:
                    if (s.Time > _until)
                    {
                        // Dragging on (a turning fight): extend straight out for room, then come back in head on.
                        _point = s.Position + s.Forward * (Close * 4f);
                        Go(RogueState.BreakOff, s, rng.NextFloat(2.2f, 3f));
                        return At(s, _point, 1.3f, false);
                    }
                    if (s.Distance < math.max(Close * 2f, _t.FireRange))
                    {
                        if (TakeToken()) Go(RogueState.Attack, s, Run());
                        else Go(RogueState.Flank, s, rng.NextFloat(4f, 6f));
                    }
                    return At(s, Steering.Lead(s.Position, Speed * 1.2f, s.Player, s.PlayerVelocity, 1.5f), 1.2f, false);

                case RogueState.Attack:
                    if (s.Distance < Pass || s.Time > _until)
                    {
                        // Peel away on the side of you it's already on (never back across your nose): mostly out
                        // from your line, usually climbing (sometimes diving), then on past you. RogueFlight flies it
                        // as a bank and pull, so it comes round in an arc rather than flat.
                        var side = Vector3.Cross(s.Forward, Vector3.up);
                        if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
                        var vertical = rng.NextFloat() < 0.7f ? 1f : -1f;
                        var fallback = (Vector3.up * (vertical * 2.5f) + side.normalized * (_side * 1.5f)).normalized;
                        var offLine = Vector3.ProjectOnPlane(s.Position - s.Player, s.Forward);
                        var away = offLine.magnitude > 2f
                            ? (offLine.normalized * 2f + fallback).normalized   // its own side, bent the way it chose
                            : fallback;
                        _point = s.Position + away * (Close * 3f) + s.Forward * Close;
                        _side = -_side;   // the other way next time
                        Go(RogueState.BreakOff, s, rng.NextFloat(1.5f, 2.6f));
                        return At(s, _point, 1.25f, false);
                    }
                    {
                        // Weave on the way in (harder to hit), steadying up as it closes to shoot.
                        var across = Vector3.Cross(s.Player - s.Position, Vector3.up);
                        if (across.sqrMagnitude < 1e-3f) across = Vector3.right;
                        var weave = math.saturate((s.Distance - Close * 2f) / (Close * 4f));
                        var aim = s.Aim + across.normalized * (math.sin(s.Time * 1.3f + _side * 2f) * Close * 0.8f * weave) +
                                  Vector3.up * (math.sin(s.Time * 0.9f + _side) * Close * 0.4f * weave);
                        return At(s, aim, s.Distance > Close * 3f ? 1.25f : s.Distance < Close ? 0.75f : 1f, true);
                    }

                case RogueState.BreakOff:
                    if (s.Time > _until)
                    {
                        if (rng.NextFloat() < _t.Aggression && TakeToken()) Go(RogueState.Attack, s, Run());
                        else Go(RogueState.Flank, s, rng.NextFloat(4f, 6f));
                    }
                    return At(s, _point, 1.25f, false);

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
                    var v = Steering.Arrive(s.Position, spot, s.PlayerVelocity, Speed * 1.15f, Brake, Close * 0.5f);
                    return (spot, v, false);
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
                    return At(s, _point, 1.3f, false);

                case RogueState.Break:
                    if (s.Time > _until) Go(RogueState.Flank, s, rng.NextFloat(3f, 5f));
                    return At(s, _point, 1.3f, false);

                case RogueState.Retreat:
                    if (s.ShieldFull || s.Time > _until) Go(RogueState.Approach, s, ApproachSeconds);
                    return At(s, s.Player + (s.Position - s.Player).normalized * (Aggro * 0.9f), 1.3f, false);
            }
            return At(s, s.Player, 1f, false);
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
