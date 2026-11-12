using System;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The run's universe (UniverseState, proto universe.proto): the catalog's first (home) until UniverseSelected
    /// picks another (the title's switch, ?ln-universe=&lt;id&gt;, the dev menu, a loaded save). Scenes read the state:
    /// Space builds its systems from that universe's generator (Space/Multiverse), the crash site its ground and gate.
    /// </summary>
    [Presenter]
    public sealed class UniversePresenter : IStartable, IDisposable
    {
        readonly UniverseState _state;
        readonly ISubscriber<UniverseSelected> _selected;
        IDisposable _subscription;

        [Inject]
        public UniversePresenter(UniverseState state, ISubscriber<UniverseSelected> selected)
        {
            _state = state;
            _selected = selected;
        }

        public void Start()
        {
            Apply(Find(_state.Id.Value));
            _subscription = _selected.Subscribe(e =>
            {
                var list = GameContent.Universes?.Universes;
                if (list == null || list.Count == 0) return;
                Universe next;
                if (string.IsNullOrEmpty(e.Id) && e.Step == 0) next = list[0];   // the default (an old save)
                else if (!string.IsNullOrEmpty(e.Id))
                {
                    next = list.Find(u => u.Id == e.Id);
                    if (next == null)
                    {
                        Debug.LogWarning($"[Universe] no universe '{e.Id}' (content/universes.txtpb); staying in {_state.Id.Value}");
                        return;
                    }
                }
                else
                {
                    var at = Math.Max(0, list.FindIndex(u => u.Id == _state.Id.Value));
                    next = list[((at + e.Step) % list.Count + list.Count) % list.Count];
                }
                Apply(next);
            });
        }

        /// <summary>The universe with this id, else the catalog's first.</summary>
        static Universe Find(string id)
        {
            var list = GameContent.Universes?.Universes;
            if (list == null || list.Count == 0) return null;
            return (string.IsNullOrEmpty(id) ? null : list.Find(u => u.Id == id)) ?? list[0];
        }

        void Apply(Universe u)
        {
            if (u == null) return;
            var changed = _state.Id.Value != u.Id;
            _state.Name.Value = u.Name;
            _state.Holds.Value = u.Holds;
            _state.Id.Value = u.Id;   // last: readers of Id see the rest already set
            if (changed) Debug.Log($"[Universe] {u.Name} ({u.Id}){(u.Holds ? ", holds" : "")}");
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
