using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Ship upgrades (content/upgrades.txtpb): what you own (PlayerShip.upgrades, "id|level"), buying the next level,
    /// and folding them into the ship's stats (PlayerShipStats.Of(ship)), what it's paid back and its solar cells.
    /// </summary>
    public static class ShipUpgrades
    {
        static UpgradeConfig Config => GameContent.Upgrades;

        public static ShipUpgrade Find(string id)
        {
            if (Config != null) foreach (var u in Config.Upgrades) if (u.Id == id) return u;
            return null;
        }

        /// <summary>The level you own of upgrade `id` (0: not bought).</summary>
        public static int Level(PlayerShip ship, string id)
        {
            foreach (var entry in ship.Upgrades)
            {
                var bar = entry.IndexOf('|');
                var key = bar < 0 ? entry : entry.Substring(0, bar);
                if (key != id) continue;
                return bar < 0 ? 1 : int.TryParse(entry.Substring(bar + 1), out var n) ? n : 1;
            }
            return 0;
        }

        /// <summary>Price of the next level (0: maxed, or no such upgrade).</summary>
        public static int NextCost(PlayerShip ship, ShipUpgrade u)
        {
            var level = Level(ship, u.Id);
            return level < u.Costs.Count ? Mathf.Max(1, u.Costs[level]) : 0;
        }

        public static void SetLevel(PlayerShip ship, string id, int level)
        {
            for (var i = ship.Upgrades.Count - 1; i >= 0; i--)
            {
                var e = ship.Upgrades[i];
                if (e == id || e.StartsWith(id + "|")) ship.Upgrades.RemoveAt(i);
            }
            if (level > 0) ship.Upgrades.Add($"{id}|{level}");
        }

        /// <summary>Every upgrade you own, with its level (unknown ids skipped).</summary>
        public static IEnumerable<(ShipUpgrade upgrade, int level)> Owned(PlayerShip ship)
        {
            if (Config == null) yield break;
            foreach (var u in Config.Upgrades)
            {
                var level = Mathf.Min(Level(ship, u.Id), u.Costs.Count);
                if (level > 0) yield return (u, level);
            }
        }

        /// <summary>The stock stats with your upgrades in: per-level multipliers, guns swapped, the dice drive's roll.</summary>
        public static ShipStats Apply(ShipStats stock, PlayerShip ship)
        {
            var s = new ShipStats
            {
                Skin = stock.Skin, Name = stock.Name, Role = stock.Role, Description = stock.Description,
                Speed = stock.Speed, Agility = stock.Agility, Fuel = stock.Fuel,
                Efficiency = stock.Efficiency, Toughness = stock.Toughness, Firepower = stock.Firepower,
                Shield = stock.Shield > 0f ? stock.Shield : 1f, Armor = stock.Armor > 0f ? stock.Armor : 1f,
            };
            s.Guns.AddRange(stock.Guns);
            foreach (var (u, level) in Owned(ship))
            {
                s.Speed *= Per(u.Speed, level);
                s.Agility *= Per(u.Agility, level);
                s.Fuel *= Per(u.Fuel, level);
                s.Efficiency *= Per(u.Efficiency, level);
                s.Toughness *= Per(u.Toughness, level);
                s.Firepower *= Per(u.Firepower, level);
                s.Shield *= Per(u.Shield, level);
                s.Armor *= Per(u.Armor, level);
                if (u.Guns.Count > 0)
                {
                    s.Guns.Clear();
                    s.Guns.AddRange(u.Guns);
                }
                if (u.DiceMax > 0f && ship.Dice.Value != 0)
                {
                    // Each stat its own roll from this launch's dice (PlayerShip.dice), between dice_min and dice_max.
                    var lo = Mathf.Max(0.05f, u.DiceMin);
                    var hi = Mathf.Max(lo, u.DiceMax);
                    var r = ship.Dice.Value;
                    s.Speed *= Roll(ref r, lo, hi);
                    s.Agility *= Roll(ref r, lo, hi);
                    s.Fuel *= Roll(ref r, lo, hi);
                    s.Efficiency *= Roll(ref r, lo, hi);
                    s.Toughness *= Roll(ref r, lo, hi);
                    s.Firepower *= Roll(ref r, lo, hi);
                }
            }
            return s;
        }

        /// <summary>What you've paid for the upgrades you own (every level bought), for the rebuild bill.</summary>
        public static int Spent(PlayerShip ship)
        {
            var total = 0;
            foreach (var (u, level) in Owned(ship))
                for (var i = 0; i < level && i < u.Costs.Count; i++) total += Mathf.Max(0, u.Costs[i]);
            return total;
        }

        /// <summary>What you're paid back at the station multiplies by this (1: no upgrades for it).</summary>
        public static float Payout(PlayerShip ship)
        {
            var m = 1f;
            foreach (var (u, level) in Owned(ship)) m *= Per(u.Payout, level);
            return m;
        }

        /// <summary>Landed, the solar cells top the tank up to at least this share (`basis`: ShipFuel.recharge_to).</summary>
        public static float RechargeTo(PlayerShip ship, float basis)
        {
            var to = basis;
            foreach (var (u, _) in Owned(ship)) to = Mathf.Max(to, u.RechargeTo);
            return Mathf.Clamp01(to);
        }

        /// <summary>A new roll for the dice drive (every launch). Never 0 (0 = not rolled).</summary>
        public static void Reroll(PlayerShip ship)
        {
            var r = (uint)Random.Range(1, int.MaxValue);
            ship.Dice.Value = r == 0 ? 1u : r;
        }

        public static bool HasDiceDrive(PlayerShip ship)
        {
            foreach (var (u, _) in Owned(ship)) if (u.DiceMax > 0f) return true;
            return false;
        }

        static float Per(float multiplier, int level) => multiplier > 0f ? Mathf.Pow(multiplier, level) : 1f;

        static float Roll(ref uint state, float lo, float hi)
        {
            // xorshift32: the same dice give the same ship all launch.
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return Mathf.Lerp(lo, hi, (state & 0xFFFFFF) / (float)0xFFFFFF);
        }
    }
}
