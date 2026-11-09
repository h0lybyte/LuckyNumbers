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
            _rng = new Random((uint)Environment.TickCount | 1u);

            _view.Bind();
            _view.Build(_config.Dice, _config.RollLabel, Roll);
            foreach (var die in _config.Dice)
            {
                _sides.Add(die.Sides);
                _faces.Add(Mathf.Clamp(die.InitialFace, 1, die.Sides));
            }
            Land(previous: 0u); // the starting universe: from the initial faces (6 6 6)
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
            _seeds.Publish(new UniverseSeedChanged(seed));
        }

        public void Dispose() => _disposed = true;
    }
}
