using System.Collections.Generic;
using LuckyNumbers.Model;

namespace LuckyNumbers.Core
{
    /// <summary>The player's ship skin: their choice (PlayerShip) if set, else the default from content/player.txtpb.</summary>
    public static class PlayerShipSkin
    {
        public static string Current(PlayerShip ship)
        {
            if (string.IsNullOrEmpty(ship.Skin.Value)) ship.Skin.Value = GameContent.Player.ShipSkin;
            return ship.Skin.Value;
        }
    }

    /// <summary>
    /// A skin's stats (content/ships.txtpb), with unset multipliers as 1 and no guns as default_stats' guns; and the
    /// tank (PlayerShip.fuel / fuel_max) it flies with.
    /// </summary>
    public static class PlayerShipStats
    {
        static readonly Dictionary<string, ShipStats> Cache = new();

        public static ShipStats Of(string skin)
        {
            skin ??= "";
            if (Cache.TryGetValue(skin, out var cached)) return cached;
            var config = GameContent.Ships;
            var basis = config?.DefaultStats ?? new ShipStats();
            ShipStats found = null;
            if (config != null) foreach (var s in config.Ships) if (s.Skin == skin) found = s;
            var stats = new ShipStats
            {
                Skin = skin,
                Name = !string.IsNullOrEmpty(found?.Name) ? found.Name : !string.IsNullOrEmpty(basis.Name) ? basis.Name : skin,
                Role = !string.IsNullOrEmpty(found?.Role) ? found.Role : basis.Role ?? "",
                Description = found?.Description ?? basis.Description ?? "",
                Speed = Or(found?.Speed, basis.Speed),
                Agility = Or(found?.Agility, basis.Agility),
                Fuel = Or(found?.Fuel, basis.Fuel),
                Efficiency = Or(found?.Efficiency, basis.Efficiency),
                Toughness = Or(found?.Toughness, basis.Toughness),
                Firepower = Or(found?.Firepower, basis.Firepower),
            };
            stats.Guns.AddRange(found != null && found.Guns.Count > 0 ? found.Guns : basis.Guns);
            Cache[skin] = stats;
            return stats;
        }

        public static ShipStats Of(PlayerShip ship) => Of(PlayerShipSkin.Current(ship));

        public static ShipGun Gun(string id)
        {
            if (GameContent.Ships != null) foreach (var g in GameContent.Ships.Guns) if (g.Id == id) return g;
            return null;
        }

        /// <summary>The tank's size for this skin (units).</summary>
        public static float FuelMax(string skin) => (GameContent.Ships?.Fuel?.Capacity ?? 100f) * Of(skin).Fuel;

        /// <summary>Fill the tank (the station; a new skin): fuel_max for the skin, fuel to it.</summary>
        public static void Refuel(PlayerShip ship)
        {
            ship.FuelMax.Value = FuelMax(PlayerShipSkin.Current(ship));
            ship.Fuel.Value = ship.FuelMax.Value;
        }

        /// <summary>Fill it only if it's never been set (first flight, a dev warp straight to the planet).</summary>
        public static void EnsureFuel(PlayerShip ship)
        {
            if (ship.FuelMax.Value <= 0f) Refuel(ship);
        }

        static float Or(float? value, float basis) => value is > 0f ? value.Value : basis > 0f ? basis : 1f;
    }
}
