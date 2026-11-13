using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// One thing on offer to someone on foot: E does it (Usable), or it's only said (a hint, E stays the hands').
    /// Key is the provider's own handle for it (a relic, the wreck, the ship...): the same object while it's the same
    /// thing, so the prompt is rebuilt only when what's on offer changes.
    /// </summary>
    public readonly struct Interaction
    {
        public readonly object Key;
        public readonly int Priority;     // InteractionPriority: lower wins, then nearer
        public readonly float Distance;   // metres, for ties
        public readonly bool Usable;

        public Interaction(object key, int priority, float distance, bool usable = true)
        {
            Key = key;
            Priority = priority;
            Distance = distance;
            Usable = usable;
        }
    }

    /// <summary>Which offer wins when several are in reach.</summary>
    public static class InteractionPriority
    {
        public const int Aboard = -10;  // riding something (a hoist): its controls before anything you pass
        public const int Pickup = 0;    // a relic or a dropped item at your feet
        public const int Job = 10;      // the job's own thing: flag the wreck, plant the beacon
        public const int Place = 20;    // a way somewhere: the hold's gate, your ship
        public const int Info = 100;    // said, not done (E stays the hands'): the wreck's flagged, R uses the ready item
    }

    /// <summary>
    /// Something on the planet E can be for. InteractionPresenter asks each one every frame you're on foot, shows the
    /// winner's prompt and hands it E; nothing else touches the on-foot prompt or IFirstPerson.InteractElsewhere.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>The best thing it offers someone standing at `feet` (on foot), or false: nothing here.</summary>
        bool Offer(Vector3 feet, out Interaction offer);

        /// <summary>The prompt for what it offered (only asked when the winner changes).</summary>
        string Prompt(object key);

        /// <summary>E, on what it offered this frame (only Usable offers).</summary>
        void Interact(object key);
    }

    /// <summary>
    /// Where the winning prompt goes, every frame (empty: nothing on offer). The planet's own hint line; the hold's
    /// shared prompt (HoldWorld.Prompt, which its climbing hint also writes).
    /// </summary>
    public interface IPromptLine
    {
        void Show(string prompt);
    }

    /// <summary>The planet's hint line: written only when the prompt changes (the landing's hints share it).</summary>
    public sealed class PlanetPromptLine : IPromptLine
    {
        readonly PlanetView _view;
        string _shown = "";

        [Inject]
        public PlanetPromptLine(PlanetView view) => _view = view;

        public void Show(string prompt)
        {
            if (prompt == _shown) return;
            _shown = prompt;
            _view.Hint(prompt, prompt == "" ? 0f : 1f);
        }
    }

    /// <summary>
    /// The on-foot prompt and what E does with it, for every IInteractable in the scene: the lowest priority in reach
    /// wins (nearest on a tie); its prompt goes in the hint, E goes to it, and the hands keep E otherwise (pebbles).
    /// </summary>
    [Presenter]
    public sealed class InteractionPresenter : IStartable, ITickable, IDisposable
    {
        readonly IReadOnlyList<IInteractable> _all;
        readonly IFirstPerson _firstPerson;
        readonly PlayerView _player;
        readonly IPromptLine _line;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _subscription;
        bool _press, _claimed, _shownUsable;
        IInteractable _shownBy;
        object _shownKey;
        string _prompt = "";

        [Inject]
        public InteractionPresenter(IReadOnlyList<IInteractable> all, IFirstPerson firstPerson, PlayerView player, IPromptLine line,
                                    ISubscriber<InputActionPerformed> actions)
        {
            _all = all;
            _firstPerson = firstPerson;
            _player = player;
            _line = line;
            _actions = actions;
        }

        public void Start()
        {
            _subscription = _actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _press = true;
            });
        }

        public void Tick()
        {
            IInteractable by = null;
            var best = default(Interaction);
            if (_firstPerson.Active)
            {
                var feet = _player.Body.transform.position;
                foreach (var it in _all)
                {
                    if (!it.Offer(feet, out var offer) || offer.Key == null) continue;
                    if (by == null || offer.Priority < best.Priority ||
                        (offer.Priority == best.Priority && offer.Distance < best.Distance)) { by = it; best = offer; }
                }
            }
            // E's for the winner, not the hands; let go when nothing usable is in reach.
            var usable = by != null && best.Usable;
            if (usable != _claimed)
            {
                _claimed = usable;
                _firstPerson.InteractElsewhere = usable;
            }
            if (_press && usable) by.Interact(best.Key);
            _press = false;
            // The prompt's asked for only when what's on offer changes (or turns usable: a seal once you've its key);
            // the line gets it every frame.
            if (!ReferenceEquals(by, _shownBy) || !ReferenceEquals(best.Key, _shownKey) || usable != _shownUsable)
            {
                _shownBy = by;
                _shownKey = best.Key;
                _shownUsable = usable;
                _prompt = by != null ? by.Prompt(best.Key) ?? "" : "";
            }
            _line.Show(_prompt);
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            if (_claimed) _firstPerson.InteractElsewhere = false;
        }
    }
}
