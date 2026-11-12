using System;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Getting up shafts. v1: in a shaft's bottom cell (ShaftUp), E puts you beside the shaft on the floor above.
    /// (To be replaced by real climbing: ladders in the mesher, hands on rungs.)
    /// </summary>
    [Presenter]
    public sealed class HoldClimbPresenter : IStartable, ITickable, IDisposable
    {
        const string ClimbHint = "E  climb up the shaft";

        readonly HoldWorld _world;
        readonly HoldPresenter _hold;
        readonly PlayerView _player;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _sub;
        bool _interact;

        [Inject]
        public HoldClimbPresenter(HoldWorld world, HoldPresenter hold, PlayerView player, ISubscriber<InputActionPerformed> actions)
        {
            _world = world;
            _hold = hold;
            _player = player;
            _actions = actions;
        }

        public void Start() => _sub = _actions.Subscribe(e => { if (e.Action == InputAction.Interact) _interact = true; });

        public void Tick()
        {
            var press = _interact;
            _interact = false;
            var cell = _world.CellOf((float3)_player.Body.transform.position + new float3(0f, 0.2f, 0f));
            if (!_world.TryCell(cell, out var c) || c.Kind != CellKind.ShaftUp) return;
            _world.Prompt = ClimbHint;
            if (!press) return;
            var above = cell + new int3(0, 1, 0);
            foreach (var d in new[] { new int3(1, 0, 0), new int3(-1, 0, 0), new int3(0, 0, 1), new int3(0, 0, -1) })
            {
                var to = above + d;
                if (!_world.TryCell(to, out var t) || !t.Walkable || t.Kind == CellKind.ShaftDown || (t.Flags & CellFlags.Pillar) != 0) continue;
                _hold.Teleport(_world.CellFloor(to) + new float3(0f, 0.05f, 0f), math.degrees(math.atan2(d.x, d.z)));
                return;
            }
        }

        public void Dispose() => _sub?.Dispose();
    }
}
