using System;
using LuckyNumbers.Model;
using R3;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Your debt (DebtState, content/dogfight.txtpb debt), app-wide: credits can go below zero (a memory restore's
    /// rebuild, a rescue drone's fee). Below 0 you're in debt; past the collections limit the outfitter won't sell; past
    /// the game-over limit the creditors take your chip (the station ends the run: StationPresenter.GameOver). Both
    /// limits grow with your level. Kept up to date whenever your credits or level change.
    /// </summary>
    [Presenter]
    public sealed class DebtWatch : IStartable, IDisposable
    {
        public const int None = 0, InDebt = 1, Collections = 2, GameOver = 3;

        readonly DebtState _state;
        readonly PlayerVoyage _voyage;
        readonly CharacterSheet _sheet;
        IDisposable _credits, _level;

        [Inject]
        public DebtWatch(DebtState state, PlayerVoyage voyage, CharacterSheet sheet)
        {
            _state = state;
            _voyage = voyage;
            _sheet = sheet;
        }

        public void Start()
        {
            _credits = _voyage.Credits.Subscribe(_ => Update());
            _level = _sheet.Level.Subscribe(_ => Update());
        }

        void Update()
        {
            var d = GameContent.Dogfight?.Debt;
            if (d == null) return;
            var level = Math.Max(1, _sheet.Level.Value);
            var collections = -(d.Collections + d.PerLevel * (level - 1));
            var gameOver = -(d.GameOver + d.PerLevel * (level - 1));
            var credits = _voyage.Credits.Value;
            var tier = credits >= 0 ? None : credits > collections ? InDebt : credits > gameOver ? Collections : GameOver;
            _state.Collections.Value = collections;
            _state.GameOver.Value = gameOver;
            _state.GameOverTitle.Value = d.GameOverTitle ?? "";
            _state.GameOverText.Value = d.GameOverText ?? "";
            _state.Text.Value = tier switch
            {
                InDebt => d.DebtText ?? "",
                Collections => (d.CollectionsText ?? "").Replace("{limit}", collections.ToString("N0")),
                _ => "",
            };
            if (_state.Level.Value != tier) UnityEngine.Debug.Log($"[Debt] {credits} cr: tier {tier} (collections {collections}, game over {gameOver})");
            _state.Level.Value = tier;
        }

        /// <summary>Is the outfitter closed to you (collections or worse)?</summary>
        public static bool Locked(DebtState state) => state.Level.Value >= Collections;

        public void Dispose()
        {
            _credits?.Dispose();
            _level?.Dispose();
        }
    }
}
