using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;

namespace LuckyNumbers.Cargo
{
    /// <summary>One relic in the hold: what, from where, worth what, and its own seed (shape and pattern).</summary>
    public sealed class Relic
    {
        public RelicCulture Culture;
        public RelicKind Kind;
        public string Material, Origin;
        public int Value;
        public uint Seed;
        /// <summary>"gilded copper Tibetan amulet".</summary>
        public string Name => $"{Material} {Culture.Name} {Kind.Name}";
    }

    /// <summary>
    /// The museum freighter's manifest, generated from the lucky number and content/cargo.txtpb: the same seed
    /// always gives the same ship, museum and relics (intro, the call and the crash site all agree).
    /// </summary>
    public sealed class Manifest
    {
        public string Ship, Museum;
        public readonly List<Relic> Relics = new();
        public int TotalValue;

        public static Manifest Generate(CargoConfig config, uint seed)
        {
            var m = new Manifest();
            if (config == null) return m;
            var rng = new Random((seed ^ 0xC0FFEE11u) | 1u);
            m.Ship = Pick(config.ShipNames, ref rng) ?? "MSV Curator";
            m.Museum = Pick(config.Museums, ref rng) ?? "Galactic Museum";
            var total = 0f;
            foreach (var c in config.Cultures) total += math.max(0f, c.Weight);
            var count = rng.NextInt(math.max(1, config.MinRelics), math.max(config.MinRelics, config.MaxRelics) + 1);
            for (var i = 0; i < count && total > 0f && config.Kinds.Count > 0; i++)
            {
                var roll = rng.NextFloat(total);
                var culture = config.Cultures[0];
                foreach (var c in config.Cultures) { roll -= math.max(0f, c.Weight); if (roll <= 0f) { culture = c; break; } }
                var kind = config.Kinds[rng.NextInt(config.Kinds.Count)];
                var relic = new Relic
                {
                    Culture = culture,
                    Kind = kind,
                    Material = Pick(culture.Materials, ref rng) ?? "",
                    Origin = Pick(culture.Origins, ref rng) ?? "",
                    Value = (int)math.round(rng.NextFloat(kind.ValueMin, math.max(kind.ValueMin, kind.ValueMax)) * math.max(0.1f, culture.ValueScale) / 10f) * 10,
                    Seed = rng.NextUInt() | 1u,
                };
                m.Relics.Add(relic);
                m.TotalValue += relic.Value;
            }
            return m;
        }

        /// <summary>"Tibetan, Roman and unknown alien amulets and vases" (cultures and kinds aboard, in manifest order).</summary>
        public string Summary()
        {
            var cultures = new List<string>();
            var kinds = new List<string>();
            foreach (var r in Relics)
            {
                if (!cultures.Contains(r.Culture.Name)) cultures.Add(r.Culture.Name);
                var plural = r.Kind.Name + "s";
                if (!kinds.Contains(plural)) kinds.Add(plural);
            }
            return $"{And(cultures)} {And(kinds)}";
        }

        /// <summary>Fill {ship} {museum} {planet} {cargo} {relics} {value} in a line of text.</summary>
        public string Fill(string text, string planet) => (text ?? "")
            .Replace("{ship}", Ship).Replace("{museum}", Museum).Replace("{planet}", planet)
            .Replace("{cargo}", Summary()).Replace("{relics}", Relics.Count.ToString()).Replace("{value}", TotalValue.ToString("N0"));

        static string And(List<string> words) => words.Count switch
        {
            0 => "",
            1 => words[0],
            _ => string.Join(", ", words.GetRange(0, words.Count - 1)) + " and " + words[^1],
        };

        static string Pick(IList<string> from, ref Random rng) => from == null || from.Count == 0 ? null : from[rng.NextInt(from.Count)];
    }
}
