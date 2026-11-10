using LuckyNumbers.Model;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Fills player state from content/player.txtpb on start-up (app-wide, once): full health at
    /// PlayerConfig.max_health. A save file would set these instead.
    /// </summary>
    [Presenter]
    public sealed class PlayerDefaults : IStartable
    {
        readonly PlayerHealth _health;

        [Inject]
        public PlayerDefaults(PlayerHealth health) => _health = health;

        public void Start()
        {
            if (_health.Max.Value > 0) return;
            _health.Max.Value = GameContent.Player.MaxHealth;
            _health.Current.Value = GameContent.Player.MaxHealth;
        }
    }
}
