using LuckyNumbers.Model;

namespace LuckyNumbers.Intro
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
}
