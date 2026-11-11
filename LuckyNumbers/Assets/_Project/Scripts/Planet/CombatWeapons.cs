using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>content/combat.txtpb's weapons: by source (an item id, "ship", "melee") or by a held gun's model.</summary>
    public static class CombatWeapons
    {
        static readonly CombatWeapon Fallback = new()
        {
            Source = "default", Name = "Gun", Ability = "dex", Damage = "1d6", Cooldown = 0.5f, Speed = 80f, Range = 60f,
        };

        /// <summary>The entry for `source`, else "default".</summary>
        public static CombatWeapon For(string source)
        {
            CombatWeapon fallback = null;
            var weapons = GameContent.Combat?.Weapons;
            if (weapons != null)
                foreach (var w in weapons)
                {
                    if (w.Source == source) return w;
                    if (w.Source == "default") fallback = w;
                }
            return fallback ?? Fallback;
        }

        /// <summary>The entry a held gun shoots as (its model listed in `models`), else "default".</summary>
        public static CombatWeapon ForModel(string model)
        {
            var weapons = GameContent.Combat?.Weapons;
            if (weapons != null && !string.IsNullOrEmpty(model))
                foreach (var w in weapons)
                    if (w.Models.Contains(model)) return w;
            return For("default");
        }

        /// <summary>A held gun's entry (WeaponView roots are named "Weapon &lt;model&gt;").</summary>
        public static CombatWeapon Held(Component gun) => ForModel(Model(gun));

        public static string Model(Component gun)
        {
            if (gun == null) return null;
            var n = gun.gameObject.name;
            return n.StartsWith("Weapon ") ? n.Substring("Weapon ".Length) : n;
        }
    }
}
