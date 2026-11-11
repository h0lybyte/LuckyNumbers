using System;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>Dice text ("NdM+K", "2d6", "1d8-1", "3") and d20 checks, on a seeded Random (your luck).</summary>
    public static class Dice
    {
        /// <summary>Roll `dice`; a crit doubles the dice (not the modifier). Bad text rolls 1d4.</summary>
        public static int Roll(string dice, ref Random rng, bool crit = false)
        {
            Parse(dice, out var count, out var sides, out var add);
            var total = add;
            for (var i = 0; i < count * (crit ? 2 : 1); i++) total += rng.NextInt(1, sides + 1);
            return math.max(1, total);
        }

        public static void Parse(string dice, out int count, out int sides, out int add)
        {
            count = 1; sides = 4; add = 0;
            var s = (dice ?? "").Replace(" ", "").ToLowerInvariant();
            if (s.Length == 0) return;
            var d = s.IndexOf('d');
            if (d < 0) { if (int.TryParse(s, out var flat)) { count = 0; add = flat; } return; }
            var rest = s.Substring(d + 1);
            var sign = rest.IndexOfAny(new[] { '+', '-' });
            var sidesText = sign >= 0 ? rest.Substring(0, sign) : rest;
            count = d == 0 ? 1 : int.TryParse(s.Substring(0, d), out var c) ? Math.Max(0, c) : 1;
            sides = int.TryParse(sidesText, out var m) ? Math.Max(1, m) : 4;
            if (sign >= 0 && int.TryParse(rest.Substring(sign), out var k)) add = k;
        }

        /// <summary>
        /// Damage for a shot's grade, each tier above the last: a graze halves a normal roll (at least 1); a hit rolls the
        /// dice; a strong hit rolls one more die but never less than the dice's best (1d6: 6..12); a crit is the dice's
        /// best plus another roll of them (1d6: 7..12, 1d6+1: 8..13).
        /// </summary>
        public static int Damage(string dice, ref Random rng, bool crit, bool strong, bool graze)
        {
            Parse(dice, out var count, out var sides, out var add);
            var best = math.max(1, count * sides + add);
            if (crit) return best + RollN(math.max(1, count), sides, ref rng);
            if (strong) return math.max(best, add + RollN(count + 1, sides, ref rng));
            var total = math.max(1, add + RollN(count, sides, ref rng));
            return graze ? math.max(1, total / 2) : total;
        }

        static int RollN(int n, int sides, ref Random rng)
        {
            var t = 0;
            for (var i = 0; i < n; i++) t += rng.NextInt(1, sides + 1);
            return t;
        }

        /// <summary>The dice's best roll (1d6+1: 7).</summary>
        public static int Max(string dice)
        {
            Parse(dice, out var count, out var sides, out var add);
            return math.max(1, count * sides + add);
        }

        /// <summary>A d20 attack: hit on total &gt;= armour; natural 20 always hits (crit), natural 1 always misses.</summary>
        public static (int d20, int total, bool hit, bool crit, bool fumble) Attack(int bonus, int armor, ref Random rng)
        {
            var d20 = rng.NextInt(1, 21);
            var total = d20 + bonus;
            var crit = d20 == 20;
            var fumble = d20 == 1;
            return (d20, total, crit || (!fumble && total >= armor), crit, fumble);
        }
    }
}
