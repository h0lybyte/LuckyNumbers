using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The outfitter (content/upgrades.txtpb): the Docking Bay's console (STATION_ACTION_OUTFITTER) opens it
    /// (StationPresenter sets Outfitter.open, and closes it on Esc or walking away); this keeps its list and your
    /// ship's stats up to date for the page and sells you the next level of an upgrade (UpgradeBuyRequested) if you
    /// have the credits. A bigger tank is filled at once (you're docked).
    /// </summary>
    [Presenter]
    public sealed class OutfitterPresenter : IStartable, IDisposable
    {
        readonly Outfitter _state;
        readonly PlayerShip _ship;
        readonly PlayerVoyage _voyage;
        readonly ISubscriber<UpgradeBuyRequested> _buys;
        readonly ISubscriber<OutfitterCloseRequested> _closes;
        readonly IPublisher<UpgradeBought> _bought;
        readonly List<IDisposable> _subscriptions = new();

        [Inject]
        public OutfitterPresenter(Outfitter state, PlayerShip ship, PlayerVoyage voyage, ISubscriber<UpgradeBuyRequested> buys,
                                  ISubscriber<OutfitterCloseRequested> closes, IPublisher<UpgradeBought> bought)
        {
            _state = state;
            _ship = ship;
            _voyage = voyage;
            _buys = buys;
            _closes = closes;
            _bought = bought;
        }

        public void Start()
        {
            _state.Open.Value = false;
            var c = GameContent.Upgrades;
            _state.CommonTitle.Value = c?.CommonTitle ?? "Outfitter";
            _state.CommonBlurb.Value = c?.CommonBlurb ?? "";
            _state.ExoticTitle.Value = c?.ExoticTitle ?? "Exotics";
            _state.ExoticBlurb.Value = c?.ExoticBlurb ?? "";
            Refresh();
            _subscriptions.Add(_buys.Subscribe(e => Buy(e.Id)));
            _subscriptions.Add(_closes.Subscribe(_ => _state.Open.Value = false));
        }

        void Buy(string id)
        {
            if (!_state.Open.Value) return;
            var u = ShipUpgrades.Find(id);
            if (u == null) return;
            var cost = ShipUpgrades.NextCost(_ship, u);
            if (cost <= 0 || _voyage.Credits.Value < cost) return;   // maxed, or you can't afford it (the page greys it out)
            var level = ShipUpgrades.Level(_ship, id) + 1;
            _voyage.Credits.Value -= cost;
            ShipUpgrades.SetLevel(_ship, id, level);
            if (u.DiceMax > 0f && _ship.Dice.Value == 0) ShipUpgrades.Reroll(_ship);   // a first roll to show
            PlayerShipStats.Refuel(_ship);   // docked: a bigger tank is filled at once
            Refresh();
            _bought.Publish(new UpgradeBought(id, u.Name, level, cost));
            Debug.Log($"[Outfitter] bought {u.Name} level {level} for {cost} cr ({_voyage.Credits.Value} cr left)");
        }

        /// <summary>The page's list ("id|tier|name|tag|description|effect|level|levels|cost") and your ship now.</summary>
        void Refresh()
        {
            _state.Items.Clear();
            if (GameContent.Upgrades != null)
                foreach (var u in GameContent.Upgrades.Upgrades)
                {
                    var tier = u.Tier == UpgradeTier.Exotic ? "Exotic" : "Common";
                    var level = Mathf.Min(ShipUpgrades.Level(_ship, u.Id), u.Costs.Count);
                    _state.Items.Add(string.Join("|", u.Id, tier, Clean(u.Name), Clean(u.Tag), Clean(u.Description),
                                                 Clean(u.Effect), level, u.Costs.Count, ShipUpgrades.NextCost(_ship, u)));
                }
            var s = PlayerShipStats.Of(_ship);
            _state.Ship.Value = s.Name;
            _state.Speed.Value = s.Speed;
            _state.Agility.Value = s.Agility;
            _state.Fuel.Value = s.Fuel;
            _state.Efficiency.Value = s.Efficiency;
            _state.Toughness.Value = s.Toughness;
            _state.Firepower.Value = s.Firepower;
            _state.Shield.Value = s.Shield;
            _state.Armor.Value = s.Armor;
            _state.Payout.Value = ShipUpgrades.Payout(_ship);
            var guns = new List<string>();
            foreach (var id in s.Guns) guns.Add(PlayerShipStats.Gun(id)?.Name ?? id);
            _state.Guns.Value = string.Join(" + ", guns);
        }

        static string Clean(string s) => (s ?? "").Replace('|', '/');

        public void Dispose()
        {
            foreach (var d in _subscriptions) d.Dispose();
            _subscriptions.Clear();
            _state.Open.Value = false;
        }
    }
}
