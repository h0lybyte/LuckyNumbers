using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Title
{
    /// <summary>
    /// The dice that create the universe. Faces chain onto the previous lucky number
    /// (LuckyNumber.Next), published as UniverseSeedChanged (buffered, so Space gets it whenever it starts).
    /// </summary>
    [Presenter]
    public sealed class DicePresenter : IStartable, IDisposable
    {
        readonly DiceView _view;
        readonly DiceState _state;
        readonly IBufferedPublisher<UniverseSeedChanged> _seeds;
        readonly List<int> _sides = new();
        readonly List<int> _faces = new();
        DiceRoller _config;
        Random _rng;
        bool _disposed;

        [Inject]
        public DicePresenter(DiceView view, DiceState state, IBufferedPublisher<UniverseSeedChanged> seeds)
        {
            _view = view;
            _state = state;
            _seeds = seeds;
        }

        public void Start()
        {
            _config = GameContent.Title.Dice;
            if (_config == null || _config.Dice.Count == 0) return;
            // A fresh source per launch. Environment.TickCount alone counts from page load on the web, so it
            // repeats; mix in the wall clock and a GUID.
            var entropy = (ulong)DateTime.UtcNow.Ticks ^ (ulong)(uint)Guid.NewGuid().GetHashCode() << 17 ^ (uint)Environment.TickCount;
            _rng = new Random((uint)(entropy ^ entropy >> 32) | 1u);

            _view.Bind();
            _view.Build(_config.Dice, _config.RollLabel, Roll);
            foreach (var die in _config.Dice)
            {
                _sides.Add(die.Sides);
                _faces.Add(Mathf.Clamp(die.InitialFace, 1, die.Sides));
            }
            if (_state.Seed.Value != 0 && _state.Faces.Count == _faces.Count)
            {
                // Back on the title (e.g. after the intro): keep the current universe, just show its dice.
                for (var i = 0; i < _faces.Count; i++) { _faces[i] = _state.Faces[i]; _view.ShowFace(i, _faces[i], 0f); }
                _view.SetLuckyNumber(_config.SeedFormat.Replace("{0}", _state.Seed.Value.ToString()));
                return;
            }
            // The starting universe: a fresh roll each launch (no animation), so no two runs open on the same
            // lucky number. Set title.dice.fixed_start in content to always open on the initial faces instead.
            if (_config.FixedStart) { Land(previous: 0u); return; }
            for (var i = 0; i < _faces.Count; i++) _faces[i] = _rng.NextInt(1, _sides[i] + 1);
            Land(previous: _rng.NextUInt());
        }

        void Roll()
        {
            if (_state.Rolling.Value) return;
            RollAsync().Forget();
        }

        async UniTaskVoid RollAsync()
        {
            _state.Rolling.Value = true;
            _view.SetRolling(true);
            var end = Time.time + Mathf.Max(0.1f, _config.RollSeconds);
            while (Time.time < end && !_disposed)
            {
                for (var i = 0; i < _faces.Count; i++)
                    _view.ShowFace(i, _rng.NextInt(1, _sides[i] + 1), _rng.NextFloat(-35f, 35f));
                await UniTask.Delay(70);
            }
            if (_disposed) return;
            for (var i = 0; i < _faces.Count; i++) _faces[i] = _rng.NextInt(1, _sides[i] + 1);
            Land(_state.Seed.Value);
            _state.Rolling.Value = false;
            _view.SetRolling(false);
        }

        void Land(uint previous)
        {
            var seed = LuckyNumber.Next(previous, _faces, _sides);
            _state.Faces.Clear();
            for (var i = 0; i < _faces.Count; i++)
            {
                _state.Faces.Add(_faces[i]);
                _view.ShowFace(i, _faces[i], 0f);
            }
            _state.Seed.Value = seed;
            _view.SetLuckyNumber(_config.SeedFormat.Replace("{0}", seed.ToString()));
            Debug.Log($"[Dice] {string.Join(" ", _faces)} -> lucky number {seed}");
            _seeds.Publish(new UniverseSeedChanged(seed, Role(DieRole.Star), Role(DieRole.Worlds), Role(DieRole.Luck), Role(DieRole.Moons)));
        }

        /// <summary>The face of the die with this role as 0..1 (1 = highest face), or -1 if no die has it.</summary>
        float Role(DieRole role)
        {
            for (var i = 0; i < _faces.Count; i++)
                if (_config.Dice[i].Role == role)
                    return _sides[i] > 1 ? (_faces[i] - 1) / (float)(_sides[i] - 1) : 1f;
            return -1f;
        }

        public void Dispose() => _disposed = true;
    }
}
