using System.Text;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Your shots as d20 attacks, rolled the moment you fire (the station range, the planet's fights). Real-time play
    /// moves the odds and the dice decide:
    ///   aim     +2 dead centre .. -2 at the very edge of what the crosshair is on
    ///   stance  +1 standing still, -2 running or in the air
    ///   recoil  -1 per quick follow-up shot (down to -5), cooling 1.5 a second: spraying works, but worse
    /// Then how far the total beats the armour decides the damage: missed by 1-2 a graze (half the dice), beat it by
    /// 5+ a strong hit (one more die), a natural 20 a crit (double dice); a natural 1 always misses.
    /// One per shooter; the caller brings the rest of the bonus as labelled parts (ability modifier, level, surprise).
    /// </summary>
    public sealed class Marksman
    {
        public const int MaxRecoil = 5;
        const float CoolPerSecond = 1.5f;
        const float SteadySpeed = 0.4f, RunSpeed = 5.5f;   // m/s (walk 4.5, sprint 8)
        public const int GrazeBy = 2, StrongBy = 5;

        float _heat, _heatAt;

        public struct Shot
        {
            public int D20, Bonus, Total, Armor, Damage;
            public bool Hit, Crit, Fumble, Graze, Strong;
            public string Mods;
        }

        /// <summary>Roll a shot at `hit` (what the crosshair is on) from `eye`, by `body` (null: no stance).</summary>
        public Shot Roll((string label, int value)[] parts, int armor, string dice, RaycastHit hit, Transform eye,
                         CharacterController body, ref Random rng)
        {
            var mods = new StringBuilder();
            void Add(string label, int value)
            {
                if (value == 0) return;
                if (mods.Length > 0) mods.Append(" · ");
                mods.Append(label).Append(value > 0 ? " +" : " −").Append(Mathf.Abs(value));
            }
            var baseBonus = 0;
            foreach (var (label, value) in parts) { Add(label, value); baseBonus += value; }
            var aim = Aim(hit, eye);
            Add("aim", aim);
            var (stance, stanceLabel) = Stance(body);
            Add(stanceLabel, stance);
            var recoil = -Recoil();
            Add("recoil", recoil);

            var bonus = baseBonus + aim + stance + recoil;
            var s = new Shot { Bonus = bonus, Armor = Mathf.Max(1, armor), Mods = mods.ToString() };
            s.D20 = rng.NextInt(1, 21);
            s.Total = s.D20 + bonus;
            s.Crit = s.D20 == 20;
            s.Fumble = s.D20 == 1;
            var margin = s.Total - s.Armor;
            if (s.Fumble) s.Hit = false;
            else if (s.Crit || margin >= 0) { s.Hit = true; s.Strong = !s.Crit && margin >= StrongBy; }
            else if (margin >= -GrazeBy) { s.Hit = true; s.Graze = true; }
            s.Damage = s.Hit ? Dice.Damage(dice, ref rng, s.Crit, s.Strong, s.Graze) : 0;
            return s;
        }

        /// <summary>+2 at the centre of what's hit, down to -2 at its edge (across the line of sight).</summary>
        static int Aim(RaycastHit hit, Transform eye)
        {
            if (hit.collider == null || eye == null) return 0;
            var b = hit.collider.bounds;
            var off = hit.point - b.center;
            float Half(Vector3 u) => b.extents.x * Mathf.Abs(u.x) + b.extents.y * Mathf.Abs(u.y) + b.extents.z * Mathf.Abs(u.z);
            var right = eye.right;
            var up = eye.up;
            var dx = Vector3.Dot(off, right) / Mathf.Max(0.05f, Half(right));
            var dy = Vector3.Dot(off, up) / Mathf.Max(0.05f, Half(up));
            var t = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
            return Mathf.RoundToInt(2f - 4f * t);
        }

        static (int, string) Stance(CharacterController body)
        {
            if (body == null) return (0, "");
            if (!body.isGrounded) return (-2, "airborne");
            var v = body.velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            if (speed < SteadySpeed) return (1, "steady");
            if (speed > RunSpeed) return (-2, "running");
            return (0, "");
        }

        /// <summary>The penalty for this shot (from the ones just before), then this shot heats the gun.</summary>
        int Recoil()
        {
            var now = Time.time;
            _heat = Mathf.Max(0f, _heat - (now - _heatAt) * CoolPerSecond);
            _heatAt = now;
            var penalty = Mathf.Min(MaxRecoil, Mathf.FloorToInt(_heat));
            _heat = Mathf.Min(MaxRecoil + 1f, _heat + 1f);
            return penalty;
        }
    }
}
