using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A Defend job's numbers (content/combat.txtpb defend), apart from the presenter so they're testable: how many
    /// waves, how big each is, how hard the beacon is, how many may be on the ground at once, and who comes next.
    /// </summary>
    public static class DefendWaves
    {
        /// <summary>The job's waves (its goal, Jobs.Roll's target + target_per_danger), else waves + waves_per_danger x danger; at least 1.</summary>
        public static int Waves(DefendConfig d, int jobGoal, int danger) =>
            jobGoal > 0 ? jobGoal : math.max(1, d.Waves + (int)math.floor(d.WavesPerDanger * danger));

        /// <summary>Wave `wave`'s monsters (1-based): budget + budget_per_danger x danger, x budget_growth each wave after the first.</summary>
        public static int Budget(DefendConfig d, int wave, int danger)
        {
            var first = math.max(1f, d.Budget + d.BudgetPerDanger * danger);
            var growth = d.BudgetGrowth > 0f ? d.BudgetGrowth : 1f;
            return math.max(1, (int)math.round(first * math.pow(growth, math.max(0, wave - 1))));
        }

        /// <summary>The beacon's hp at this danger.</summary>
        public static int BeaconHp(DefendConfig d, int danger) => math.max(1, d.BeaconHp + d.BeaconHpPerDanger * danger);

        /// <summary>Horde monsters on the ground at once (the low-end tier's own cap); never under 1.</summary>
        public static int MaxAlive(DefendConfig d, bool lowEnd)
        {
            var cap = lowEnd && d.MaxAliveLowEnd > 0 ? d.MaxAliveLowEnd : d.MaxAlive;
            return math.max(1, cap > 0 ? cap : 60);
        }

        /// <summary>One mix entry's weight on wave `wave` at `danger` (0: it doesn't come).</summary>
        public static float Weight(DefendMix m, int wave, int danger) =>
            wave < math.max(1, m.WaveMin) || danger < m.DangerMin ? 0f : math.max(0f, m.Weight + m.WeightPerWave * (wave - 1));

        /// <summary>
        /// Who comes, by weight, for `roll` in [0, 1): the mix entry's monster id ("" if nothing can come on this wave).
        /// </summary>
        public static string Pick(IReadOnlyList<DefendMix> mix, int wave, int danger, float roll)
        {
            var total = 0f;
            foreach (var m in mix) total += Weight(m, wave, danger);
            if (total <= 0f) return "";
            var at = roll * total;
            string last = "";
            foreach (var m in mix)
            {
                var w = Weight(m, wave, danger);
                if (w <= 0f) continue;
                last = m.Monster;
                at -= w;
                if (at < 0f) return m.Monster;
            }
            return last;
        }
    }
}
