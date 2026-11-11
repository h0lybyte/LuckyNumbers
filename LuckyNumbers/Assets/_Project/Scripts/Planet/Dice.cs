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
