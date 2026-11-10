using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using MessagePipe;
using ObservableCollections;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The player's character sheet (content/character.txtpb), app-wide. Abilities roll from the lucky number
    /// (4d6 drop the lowest, seeded, so a universe always gives the same character); until you leave the station
    /// the page's sheet can move points between them (CharacterAbilityAdjustRequested: a point taken off goes to the
    /// pool, up to shift_budget; the pool also starts with free_points) and pick a class and origin. Scores, modifiers,
    /// Stamina, skills and the relic bonus go to CharacterSheet; HP goes to PlayerHealth. Back on the title it's a new
    /// run: unlocked, level 1, rolled again from the next lucky number.
    /// </summary>
    [Presenter]
    public sealed class CharacterPresenter : IStartable, IDisposable
    {
        readonly CharacterSheet _sheet;
        readonly PlayerHealth _health;
        readonly FlowState _flow;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly ISubscriber<CharacterAbilityAdjustRequested> _adjust;
        readonly ISubscriber<CharacterClassRequested> _classes;
        readonly ISubscriber<CharacterOriginRequested> _origins;
        readonly ISubscriber<CharacterResetRequested> _resets;
        readonly ISubscriber<CharacterXpGained> _xp;
        readonly List<IDisposable> _subscriptions = new();

        int[] _rolled = Array.Empty<int>(), _moved = Array.Empty<int>();
        string _classId, _originId;
        int _level = 1, _xpTotal, _staminaMax, _hpMax;
        uint _seed;
        bool _locked;

        static CharacterConfig Config => GameContent.Character;

        [Inject]
        public CharacterPresenter(CharacterSheet sheet, PlayerHealth health, FlowState flow,
                                  IBufferedSubscriber<UniverseSeedChanged> seeds,
                                  ISubscriber<CharacterAbilityAdjustRequested> adjust,
                                  ISubscriber<CharacterClassRequested> classes,
                                  ISubscriber<CharacterOriginRequested> origins,
                                  ISubscriber<CharacterResetRequested> resets,
                                  ISubscriber<CharacterXpGained> xp)
        {
            _sheet = sheet;
            _health = health;
            _flow = flow;
            _seeds = seeds;
            _adjust = adjust;
            _classes = classes;
            _origins = origins;
            _resets = resets;
            _xp = xp;
        }

        public void Start()
        {
            var n = Config.Abilities.Count;
            if (n == 0)
            {
                // No sheet in content: plain health from player.txtpb.
                _health.Max.Value = _health.Current.Value = GameContent.Player.MaxHealth;
                return;
            }
            _rolled = new int[n];
            _moved = new int[n];
            _classId = FindClass(Config.DefaultClass)?.Id ?? Config.Classes[0].Id;
            _originId = FindOrigin(Config.DefaultOrigin)?.Id ?? Config.Origins[0].Id;
            _sheet.Callsign.Value = Config.DefaultCallsign;
            Roll(1u);   // until the title's dice land (or when a scene runs on its own in the editor)

            _subscriptions.Add(_seeds.Subscribe(e =>
            {
                if (_locked) return;
                Roll(e.Seed);
                Publish();
            }));
            _subscriptions.Add(_adjust.Subscribe(e => Adjust(e.Index, e.Delta)));
            _subscriptions.Add(_classes.Subscribe(e =>
            {
                if (_locked || FindClass(e.Id) == null) return;
                _classId = e.Id;
                Publish();
            }));
            _subscriptions.Add(_origins.Subscribe(e =>
            {
                if (_locked || FindOrigin(e.Id) == null) return;
                _originId = e.Id;
                Publish();
            }));
            _subscriptions.Add(_resets.Subscribe(_ =>
            {
                if (_locked) return;
                Array.Clear(_moved, 0, _moved.Length);
                Publish();
            }));
            _subscriptions.Add(_xp.Subscribe(e => Gain(e.Amount)));
            _subscriptions.Add(_flow.Scene.Subscribe(OnScene));
            Publish();
        }

        void OnScene(GameScene scene)
        {
            if (scene == GameScene.Title && _locked)
            {
                // A new run.
                _locked = false;
                _level = 1;
                _xpTotal = 0;
                Array.Clear(_moved, 0, _moved.Length);
            }
            // The sheet is set once you leave the station.
            else if (scene == GameScene.Intro || scene == GameScene.Planet) _locked = true;
            Publish();
        }

        /// <summary>Each ability: `count` d`sides`, the lowest `drop_lowest` dropped, from the lucky number.</summary>
        void Roll(uint seed)
        {
            _seed = seed;
            var rule = Config.AbilityRoll;
            int count = Math.Max(1, rule?.Count ?? 4), sides = Math.Max(2, rule?.Sides ?? 6), drop = Math.Clamp(rule?.DropLowest ?? 1, 0, count - 1);
            var state = Hash(seed ^ 0x5EE7C4A2u);
            var dice = new int[count];
            for (var i = 0; i < _rolled.Length; i++)
            {
                for (var d = 0; d < count; d++)
                {
                    state = Hash(state + 0x9E3779B9u);
                    dice[d] = 1 + (int)(state % (uint)sides);
                }
                Array.Sort(dice);
                var sum = 0;
                for (var d = drop; d < count; d++) sum += dice[d];
                _rolled[i] = sum;
            }
            Array.Clear(_moved, 0, _moved.Length);
        }

        static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        /// <summary>+1 takes a point from the pool; -1 gives one back (or, past what was added, takes one off).</summary>
        void Adjust(int index, int delta)
        {
            if (_locked || index < 0 || index >= _moved.Length || delta == 0) return;
            var score = _rolled[index] + _moved[index];
            if (delta > 0)
            {
                if (Points() <= 0 || score >= Config.ScoreMax) return;
                _moved[index]++;
            }
            else
            {
                if (score <= Config.ScoreMin) return;
                if (_moved[index] <= 0 && CanLower() <= 0) return;
                _moved[index]--;
            }
            Publish();
        }

        int Points()
        {
            var points = Config.FreePoints;
            foreach (var m in _moved) points -= m;
            return points;
        }

        int CanLower()
        {
            var lowered = 0;
            foreach (var m in _moved) if (m < 0) lowered -= m;
            return Math.Max(0, Config.ShiftBudget - lowered);
        }

        void Gain(int amount)
        {
            if (amount <= 0 || _rolled.Length == 0) return;
            _xpTotal += amount;
            while (_level - 1 < Config.LevelXp.Count && _xpTotal >= Config.LevelXp[_level - 1])
            {
                _level++;
                Debug.Log($"[Character] level {_level}");
            }
            Publish();
        }

        /// <summary>Everything derived (scores, modifiers, Stamina, HP, skills) to CharacterSheet and PlayerHealth.</summary>
        void Publish()
        {
            if (_rolled.Length == 0) return;
            var cls = FindClass(_classId);
            var origin = FindOrigin(_originId);
            var n = _rolled.Length;
            var scores = new int[n];
            var mods = new int[n];
            for (var i = 0; i < n; i++)
            {
                scores[i] = _rolled[i] + _moved[i] + Bonus(origin, Config.Abilities[i].Id);
                mods[i] = Modifier(scores[i]);
            }

            var staminaMax = Math.Max(1, (cls.StaminaPerLevel + Mod(mods, Config.StaminaAbility)) * _level);
            var hpMax = Math.Max(1, origin.Hp + cls.HpPerLevel * _level);
            if (!_locked || _staminaMax == 0)
            {
                // Building the character (or the first time): always full.
                _sheet.Stamina.Value = staminaMax;
                _health.Current.Value = hpMax;
            }
            else
            {
                // Levelling up adds what the maximum grew by.
                _sheet.Stamina.Value = Math.Clamp(_sheet.Stamina.Value + Math.Max(0, staminaMax - _staminaMax), 0, staminaMax);
                _health.Current.Value = Math.Clamp(_health.Current.Value + Math.Max(0, hpMax - _hpMax), 0, hpMax);
            }
            _staminaMax = staminaMax;
            _hpMax = hpMax;
            _sheet.StaminaMax.Value = staminaMax;
            _health.Max.Value = hpMax;

            var skills = new List<string>(Config.Skills.Count);
            foreach (var s in Config.Skills)
            {
                var ai = AbilityIndex(s.Ability);
                var trained = cls.TrainedSkills.Contains(s.Id);
                var bonus = (ai >= 0 ? mods[ai] : 0) + (trained ? Config.TrainedBonus + _level : 0);
                var abbr = ai >= 0 ? Config.Abilities[ai].Abbr : "";
                skills.Add($"{s.Id}|{s.Name}|{abbr}|{bonus}|{(trained ? 1 : 0)}");
            }

            _sheet.ClassId.Value = cls.Id;
            _sheet.OriginId.Value = origin.Id;
            _sheet.Level.Value = _level;
            _sheet.Xp.Value = _xpTotal;
            _sheet.XpNext.Value = _level - 1 < Config.LevelXp.Count ? Config.LevelXp[_level - 1] : 0;
            _sheet.Points.Value = Points();
            _sheet.CanLower.Value = CanLower();
            _sheet.Locked.Value = _locked;
            _sheet.RelicBonus.Value = Mod(mods, Config.RelicAbility);
            _sheet.RelicAbility.Value = Abbr(Config.RelicAbility);
            _sheet.XpLevelStart.Value = _level >= 2 && _level - 2 < Config.LevelXp.Count ? Config.LevelXp[_level - 2] : 0;
            _sheet.Seed.Value = _seed;
            Set(_sheet.Rolled, _rolled);
            Set(_sheet.Moved, _moved);
            Set(_sheet.Scores, scores);
            Set(_sheet.Mods, mods);
            Set(_sheet.Skills, skills);

            var adjust = new int[n];
            var points = Points();
            var canLower = CanLower();
            for (var i = 0; i < n; i++)
            {
                if (_locked) continue;
                var score = _rolled[i] + _moved[i];
                if (points > 0 && score < Config.ScoreMax) adjust[i] |= 1;
                if (score > Config.ScoreMin && (_moved[i] > 0 || canLower > 0)) adjust[i] |= 2;
            }
            Set(_sheet.Adjust, adjust);
            Set(_sheet.Abilities, Config.Abilities.ConvertAll(a => $"{a.Id}|{a.Name}|{a.Abbr}|{a.Blurb}"));
            Set(_sheet.Classes, Config.Classes.ConvertAll(c => $"{c.Id}|{c.Name}|{c.Blurb}|{Abbr(c.KeyAbility)}"));
            Set(_sheet.Origins, Config.Origins.ConvertAll(o =>
                $"{o.Id}|{o.Name}|{o.Blurb}|{string.Join(", ", o.Bonuses.ConvertAll(b => $"{Abbr(b.Ability)} {(b.Amount >= 0 ? "+" : "")}{b.Amount}"))}"));
        }

        static string Abbr(string ability)
        {
            var i = AbilityIndex(ability);
            return i >= 0 ? Config.Abilities[i].Abbr : (ability ?? "").ToUpperInvariant();
        }

        /// <summary>d20 modifier: 10-11 is +0, each 2 points either way is 1.</summary>
        public static int Modifier(int score) => Mathf.FloorToInt((score - 10) / 2f);

        int Mod(int[] mods, string ability)
        {
            var i = AbilityIndex(ability);
            return i >= 0 ? mods[i] : 0;
        }

        static int AbilityIndex(string id)
        {
            for (var i = 0; i < Config.Abilities.Count; i++) if (Config.Abilities[i].Id == id) return i;
            return -1;
        }

        static int Bonus(CharacterOrigin origin, string ability)
        {
            var total = 0;
            foreach (var b in origin.Bonuses) if (b.Ability == ability) total += b.Amount;
            return total;
        }

        static CharacterClass FindClass(string id) => Config.Classes.Find(c => c.Id == id);
        static CharacterOrigin FindOrigin(string id) => Config.Origins.Find(o => o.Id == id);

        static void Set<T>(ObservableList<T> list, IReadOnlyList<T> values)
        {
            var same = list.Count == values.Count;
            for (var i = 0; same && i < values.Count; i++) same = EqualityComparer<T>.Default.Equals(list[i], values[i]);
            if (same) return;
            list.Clear();
            foreach (var v in values) list.Add(v);
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }
}
