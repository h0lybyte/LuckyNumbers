using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Throwers (MonsterType.ranged): monsters that keep their distance and lob flasks. MonsterBrain decides (keep
    /// within the band, wind up, throw); this carries it out: backing off and circling you, the cast clip, a charge
    /// glowing in its hand, the launch (Ballistic: the low arc to your feet, leading you a little), the flask's flight
    /// (Flasks) and the splash's roll: within splash_radius of where it bursts, d20 + its attack (+ direct_bonus if it
    /// struck you) against your armour, damage as a bite's (Stamina, then HP), published as a "flask" CombatRoll.
    /// It flies slowly: step aside and it bursts clear of you. A horde thrower after the beacon (MonsterMark.OnBeacon)
    /// lobs at the beacon's foot instead, and a splash within reach of the beacon rolls against it (CombatPresenter.Defend);
    /// a splash can catch you both.
    ///
    /// Dev (the page's inspector): "monster:&lt;id&gt;:ranged" drops that monster as a thrower (its own ranged, or
    /// <see cref="DevRanged"/>), "monster:&lt;id&gt;:ranged:N" N of them in a ring round you; "monster:heal" fills your HP and Stamina.
    /// </summary>
    public sealed partial class CombatPresenter
    {
        Flasks _flasks;
        int _throwLogs;
        /// <summary>The flattest a flask is thrown (radians up): nearer targets get a slower lob, not a dart (Ballistic.Solve).</summary>
        const float LobAngle = 35f * Mathf.Deg2Rad;
        /// <summary>Chords of the arc a ready throw checks for a clear way (only while a throw's ready and it can see you).</summary>
        const int ArcChecks = 6;

        /// <summary>A dev thrower's attack when its type has none: the arcanist's plague flask (content/combat.txtpb).</summary>
        static readonly MonsterRanged DevRanged = new()
        {
            RangeMin = 7f, RangeMax = 15f, Cooldown = 3.2f, CastTime = 0.9f, Speed = 11f, Gravity = 7.5f,
            Attack = 3, Damage = "1d6", SplashRadius = 1.6f, DirectBonus = 2, Lead = 0.35f,
            Color = new Rgba(0.9f, 2.6f, 0.35f, 1f),
        };

        /// <summary>
        /// The cast's clip (MonsterClips.cast: the arcanist's channel, whose thrust is its release, cast_time in); a
        /// monster without one (a block, a dev thrower) plays its attack instead.
        /// </summary>
        static void PlayCast(Monster m)
        {
            if (!m.View.Cast()) m.View.Attack();
        }

        void EnsureFlasks()
        {
            if (_flasks != null) return;
            _flasks = new Flasks(16) { Direct = _player.Body.transform, Burst = OnFlaskBurst };
        }

        /// <summary>Makes `m` a thrower (null: not), its first throw a moment off and staggered from its neighbours'.</summary>
        static void Arm(Monster m, MonsterRanged ranged)
        {
            m.Ranged = ranged;
            if (ranged == null) return;
            var spread = math.frac(math.abs(m.Home.x * 0.731f + m.Home.z * 0.377f));   // no dice: spawning rolls stay as they were
            m.Brain.CastCooldown = math.max(0.5f, ranged.CastTime) + spread * math.max(0.5f, ranged.Cooldown) * 0.6f;
        }

        static float RangeMin(MonsterRanged r) => math.max(1f, r.RangeMin);
        static float RangeMax(MonsterRanged r) => math.max(RangeMin(r) + 2f, r.RangeMax);

        /// <summary>A thrower's part of the senses: its band and timings, and (only when a throw's ready) whether it can see you.</summary>
        void SenseRanged(Monster m, ref MonsterSenses s)
        {
            var r = m.Ranged;
            if (r == null) return;
            s.Ranged = true;
            s.RangeMin = RangeMin(r);
            s.RangeMax = RangeMax(r);
            s.CastTime = math.max(0.1f, r.CastTime);
            s.CastCooldown = math.max(0.5f, r.Cooldown);
            if (m.Brain.WantsSight(s)) s.LineOfSight = CanSee(m);
        }

        /// <summary>Where the flask leaves it: up at its right hand, in front of its body.</summary>
        static Vector3 Hand(Monster m)
        {
            var size = Size(m);
            return m.Position + Quaternion.Euler(0f, m.Heading, 0f) * new Vector3(size.x * 0.3f, size.y * 0.35f, size.z * 0.5f + 0.25f);
        }

        /// <summary>Nothing solid between its hand and your eyes, or the beacon's middle if that's its target (another monster in the way counts).</summary>
        bool CanSee(Monster m)
        {
            var from = Hand(m);
            var beacon = m.Brain.OnBeacon && _beacon != null;
            var to = (beacon ? BeaconMiddle : _player.Camera.transform.position) - from;
            var d = to.magnitude;
            if (d >= 0.01f && Physics.Raycast(from, to / d, out var hit, d, ~0, QueryTriggerInteraction.Ignore) && !IsTarget(m, hit.collider)) return false;
            return ClearArc(m);
        }

        /// <summary>`c` is (part of) what `m` is throwing at: your body, or the beacon.</summary>
        bool IsTarget(Monster m, Collider c) =>
            m.Brain.OnBeacon && _beacon != null ? c.transform.IsChildOf(_beacon) : c.transform.IsChildOf(_player.Body.transform);

        /// <summary>
        /// The throw it would make now (no lead) gets to you: the arc's chords (<see cref="ArcChecks"/> of them) hit
        /// nothing before you or the ground at your feet. A lob over a ship or a ridge it can see you past won't do:
        /// it walks in for a clear one (MonsterBrain: ready without a line, it steps closer).
        /// </summary>
        bool ClearArc(Monster m)
        {
            Aim(m, false, out var hand, out var velocity, out var gravity, out var time);
            float3 v = velocity, h = hand;
            var prev = hand;
            for (var i = 1; i <= ArcChecks; i++)
            {
                Vector3 next = Ballistic.At(h, v, gravity, time * i / ArcChecks);
                var seg = next - prev;
                var len = seg.magnitude;
                if (len > 1e-3f && Physics.Raycast(prev, seg / len, out var hit, len, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(m.View.transform)) { prev = next; continue; }   // its own body
                    var off = m.Brain.OnBeacon && _beacon != null ? FromBeacon(hit.point) : FromYou(hit.point);
                    return IsTarget(m, hit.collider) || off <= math.max(0.3f, m.Ranged.SplashRadius);
                }
                prev = next;
            }
            return true;
        }

        /// <summary>The throw at you: from its hand, the low lob to your feet (leading you by `lead` if asked); at the beacon, its foot.</summary>
        void Aim(Monster m, bool lead, out Vector3 hand, out Vector3 velocity, out float gravity, out float time)
        {
            var r = m.Ranged;
            hand = Hand(m);
            gravity = r.Gravity > 0f ? r.Gravity : 9.81f;
            var speed = math.max(1f, r.Speed);
            if (m.Brain.OnBeacon && _beacon != null)
            {
                // The side of the beacon facing it, a little up: it bursts against the pillar.
                var side = _beaconAt + Vector3.ProjectOnPlane(m.Position - _beaconAt, Vector3.up).normalized * (_beaconRadius * 0.8f);
                Ballistic.SolveLeading(hand, side + Vector3.up * 0.4f, float3.zero, 0f, speed, gravity, out var at, out time, LobAngle);
                velocity = at;
                return;
            }
            Ballistic.SolveLeading(hand, Feet() + Vector3.up * 0.3f, lead ? (float3)_player.Body.velocity : float3.zero,
                                   lead ? math.saturate(r.Lead) : 0f, speed, gravity, out var v, out time, LobAngle);
            velocity = v;
        }

        /// <summary>
        /// A thrower's act (MonsterBrain.Ranged); it's already turned to you. Returns the speed it walked (m/s).
        /// Backing off it also drifts sideways, so it doesn't just back into what's behind it.
        /// </summary>
        float Ranged(Monster m, MonsterAct act, Vector3 to, float dt)
        {
            var r = m.Ranged;
            var flat = to.sqrMagnitude > 1e-4f ? to.normalized : Vector3.forward;
            var side = Vector3.Cross(Vector3.up, flat) * StrafeSign(m);
            var speed = m.Type.Speed;
            if ((act == MonsterAct.Retreat || act == MonsterAct.Strafe) && Time.time < m.CastUntil) return 0f;   // still in its cast clip
            switch (act)
            {
                case MonsterAct.Retreat:
                    m.Position += (-flat * 0.75f + side * 0.35f) * (speed * dt);
                    return speed * 0.8f;
                case MonsterAct.Strafe:
                    m.Position += side * (speed * 0.45f * dt);
                    return speed * 0.45f;
                case MonsterAct.Cast:
                    PlayCast(m);
                    m.CastUntil = Time.time + math.max(0f, m.View.CastLength - 0.25f);   // no cast clip: no hold
                    _flasks?.Charge(Hand(m), 0.5f, 0f, Tint(r));
                    return 0f;
                case MonsterAct.Channel:
                    EnsureFlasks();
                    _flasks.Charge(Hand(m), 0.5f, m.Brain.CastClock / math.max(0.1f, r.CastTime), Tint(r));
                    return 0f;
                case MonsterAct.Throw:
                    Throw(m);
                    return 0f;
            }
            return 0f;
        }

        /// <summary>Which way it circles you: its own, turning about every few seconds.</summary>
        static float StrafeSign(Monster m) => ((int)math.floor(Time.time / 2.6f + math.abs(m.Home.x * 0.37f + m.Home.z * 0.21f)) & 1) == 0 ? 1f : -1f;

        static Color Tint(MonsterRanged r) => (r.Color.R + r.Color.G + r.Color.B) > 0f
            ? new Color(r.Color.R, r.Color.G, r.Color.B, 1f)
            : new Color(0.9f, 2.6f, 0.35f, 1f);

        /// <summary>The flask leaves its hand: the low arc to your feet, leading where you're walking by `lead`.</summary>
        void Throw(Monster m)
        {
            EnsureFlasks();
            var r = m.Ranged;
            Aim(m, true, out var hand, out var velocity, out var gravity, out var time);
            _flasks.Throw(hand, velocity, gravity, Tint(r), 0.2f, math.max(0.3f, r.SplashRadius), m.View.transform, m);
            if (_throwLogs++ < 6 && !m.Horde)
            {
                var short_ = Vector3.Distance(hand, Feet()) > Ballistic.Reach(math.max(1f, r.Speed), gravity);
                Debug.Log($"[Combat] {m.Type.Name} throws a flask: {Vector3.Distance(hand, Feet()):0.0} m, {time:0.00} s in the air" +
                          $"{(short_ ? " (out of reach: it falls short)" : "")}");
            }
        }

        /// <summary>The bottom of your body (CharacterController).</summary>
        Vector3 Feet()
        {
            var body = _player.Body;
            return body.transform.TransformPoint(body.center) - Vector3.up * (body.height * 0.5f);
        }

        /// <summary>How far `point` is from your body (its upright segment, feet to head, less its radius).</summary>
        float FromYou(Vector3 point)
        {
            var body = _player.Body;
            var feet = Feet();
            var y = math.clamp(point.y, feet.y + body.radius, feet.y + body.height - body.radius);
            return math.max(0f, Vector3.Distance(point, new Vector3(feet.x, y, feet.z)) - body.radius);
        }

        /// <summary>A flask burst (Flasks.Burst): within its splash of you it rolls d20 + attack (+ direct) against your armour.</summary>
        void OnFlaskBurst(Vector3 point, Collider what, bool direct, object tag)
        {
            if (_down || tag is not Monster m || m.Ranged == null) return;
            var r = m.Ranged;
            SplashBeacon(m, point, what);   // whoever it was thrown at: the beacon in its splash takes it too
            var off = FromYou(point);
            var radius = math.max(0.3f, r.SplashRadius);
            if (!direct && off > radius)
            {
                if (!m.Horde) Debug.Log($"[Combat] {m.Type.Name}'s flask bursts on {(what != null ? what.name : "?")} {off:0.0} m from you: clear (splash {radius:0.0} m)");
                return;
            }
            var bonus = r.Attack + (direct ? r.DirectBonus : 0);
            var armor = PlayerArmor();
            var (d20, total, hit, crit, fumble) = Dice.Attack(bonus, armor, ref _rng);
            var dice = string.IsNullOrEmpty(r.Damage) ? "1d4" : r.Damage;
            var damage = hit ? Dice.Roll(dice, ref _rng, crit) : 0;
            var mods = direct && r.DirectBonus != 0 ? $"attack +{r.Attack} · direct +{r.DirectBonus}" : $"attack +{r.Attack} · splash {off:0.0} m";
            _rolls.Publish(new CombatRoll(m.Type.Name, "You", d20, bonus, total, armor, hit, crit, fumble, damage, dice, false, "flask",
                                          false, false, mods));
            Debug.Log($"[Combat] {m.Type.Name}'s flask {(direct ? "strikes you" : $"splashes {off:0.0} m off")}: d20 {d20} + {bonus} = {total} vs {armor}: " +
                      $"{(hit ? $"{(crit ? "CRIT " : "")}hit, {damage}" : fumble ? "fumble" : "miss")}");
            if (hit) TakeDamage(damage, $"{m.Type.Name}'s flask");
        }

        /// <summary>Dev: "monster:heal": HP and Stamina full (a long test against throwers).</summary>
        void DevHeal()
        {
            _health.Current.Value = _health.Max.Value;
            _sheet.Stamina.Value = _sheet.StaminaMax.Value;
            Debug.Log($"[Combat] dev: healed ({_health.Current.Value} HP, {_sheet.Stamina.Value} Stamina)");
        }

        /// <summary>
        /// Dev: "monster:&lt;id&gt;:ranged[:N]": `type` as a thrower, 10 m ahead (N: a ring of them round you, mid-band).
        /// </summary>
        void DevRangedSpawn(MonsterType type, int n)
        {
            var ranged = type.Ranged ?? DevRanged;
            var body = _player.Body.transform;
            var flat = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
            if (n <= 1)
            {
                Spawn(type, body.position + flat * 10f, true);
                Arm(_monsters[^1], ranged);
                Debug.Log($"[Combat] dev: {type.Name} as a thrower 10 m ahead (band {RangeMin(ranged):0}-{RangeMax(ranged):0} m)");
                return;
            }
            var radius = (RangeMin(ranged) + RangeMax(ranged)) * 0.5f;
            for (var i = 0; i < n; i++)
            {
                Spawn(type, body.position + Quaternion.Euler(0f, 360f * i / n, 0f) * flat * radius, true);
                Arm(_monsters[^1], ranged);
            }
            Debug.Log($"[Combat] dev: {n} {type.Name} throwers in a ring {radius:0} m round you");
        }
    }
}
