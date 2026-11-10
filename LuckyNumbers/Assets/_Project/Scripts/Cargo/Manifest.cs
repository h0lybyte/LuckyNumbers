using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;

namespace LuckyNumbers.Cargo
{
    /// <summary>One relic in the hold: the item it is (a unique ItemDef with lore, a d20 table, a model) and its seed.</summary>
    public sealed class Relic
    {
        public ItemDef Def;
        public uint Seed;
        public string Name => Def.Name;
        public int Value => Def.Value;
        /// <summary>Whose it was ("Zhang Heng"), or "" for a relic without lore.</summary>
        public string Figure => Def.Lore?.Figure ?? "";
    }

    /// <summary>
    /// The museum freighter's manifest, from the lucky number and content/cargo.txtpb: the same seed always gives the
    /// same ship and museum (intro, the call and the crash site all agree). Its relics are cargo.txtpb `relics`, item
    /// ids in content/items.txtpb, always all of them (docs/items.md).
    /// </summary>
    public sealed class Manifest
    {
        public string Ship, Museum;
        public readonly List<Relic> Relics = new();
        public int TotalValue;

        public static Manifest Generate(CargoConfig config, uint seed) => Generate(config, GameContent.Items, seed);

        public static Manifest Generate(CargoConfig config, ItemConfig items, uint seed)
        {
            var m = new Manifest();
            if (config == null) return m;
            var rng = new Random((seed ^ 0xC0FFEE11u) | 1u);
            m.Ship = Pick(config.ShipNames, ref rng) ?? "MSV Curator";
            m.Museum = Pick(config.Museums, ref rng) ?? "Galactic Museum";
            foreach (var id in config.Relics)
            {
                var def = items?.Items.Find(i => i.Id == id);
                if (def == null)
                {
                    UnityEngine.Debug.LogWarning($"[Cargo] relic '{id}' isn't an item in items.txtpb");
                    continue;
                }
                m.Relics.Add(new Relic { Def = def, Seed = rng.NextUInt() | 1u });
                m.TotalValue += def.Value;
            }
            return m;
        }

        /// <summary>What's aboard, for the radio: each relic by whose it was ("Zhang Heng's Water Amulet").</summary>
        public string Summary()
        {
            var named = new List<string>();
            foreach (var r in Relics) named.Add(r.Figure != "" ? $"{r.Figure}'s {r.Name}" : r.Name);
            return named.Count > 0 ? And(named) : "nothing listed";
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
