using System;
using System.Collections.Generic;
using System.Text;
using LuckyNumbers.Model;
using MessagePipe;
using ObservableCollections;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>The player's pack, for the scenes that put things in it (relics at the crash site).</summary>
    public interface IInventory
    {
        /// <summary>
        /// Put item `itemId` in the first place it fits (any rotation). `name`, `description` and `value` override the
        /// item's own (a relic's); `relic` links it to RelicsPresenter's held list (-1: not a relic). False: no room.
        /// </summary>
        bool TryAdd(string itemId, string name, string description, int value, int relic);

        /// <summary>Take every relic out (the crash site starts over).</summary>
        void ClearRelics();

        /// <summary>Show `text` as the pack's message (e.g. "No room for ...").</summary>
        void Say(string text);
    }

    /// <summary>
    /// The pack (content/items.txtpb), app-wide: a grid items fit into by shape, like falling blocks. Your class's
    /// starting kit is packed while you set up your character (repacked when you change class) and kept once you
    /// leave the station; relics you pick up go in too (IInventory). The page's grid moves items
    /// (InventoryMoveRequested: cell + quarter turns, checked here), uses them (consumables heal or reveal; relics
    /// roll through RelicUseRequested) and drops them. Bulk against your Strength gives the load and walking speed.
    /// Everything goes to PlayerInventory for the page.
    /// </summary>
    [Presenter]
    public sealed class InventoryPresenter : IInventory, IStartable, ITickable, IDisposable
    {
        sealed class Item
        {
            public int Uid;
            public ItemDef Def;
            public string Name, Description;
            public int Value, X, Y, Rot, Relic = -1;
            public bool[,] Cells;   // rotated footprint [row, column]
        }

        readonly PlayerInventory _state;
        readonly CharacterSheet _sheet;
        readonly PlayerHealth _health;
        readonly FlowState _flow;
        readonly ISubscriber<InventoryMoveRequested> _moves;
        readonly ISubscriber<InventoryUseRequested> _uses;
        readonly ISubscriber<InventoryDropRequested> _drops;
        readonly IPublisher<RelicUseRequested> _relicUse;
        readonly IPublisher<ItemEffectRequested> _effects;
        readonly List<IDisposable> _subscriptions = new();
        readonly List<Item> _items = new();
        int _nextUid = 1, _strength = -1;
        string _kitFor;

        static ItemConfig Config => GameContent.Items;
        int Width => Math.Max(1, Config.GridWidth);
        int Height => Math.Max(1, Config.GridHeight);

        [Inject]
        public InventoryPresenter(PlayerInventory state, CharacterSheet sheet, PlayerHealth health, FlowState flow,
                                  ISubscriber<InventoryMoveRequested> moves, ISubscriber<InventoryUseRequested> uses,
                                  ISubscriber<InventoryDropRequested> drops, IPublisher<RelicUseRequested> relicUse,
                                  IPublisher<ItemEffectRequested> effects)
        {
            _state = state;
            _sheet = sheet;
            _health = health;
            _flow = flow;
            _moves = moves;
            _uses = uses;
            _drops = drops;
            _relicUse = relicUse;
            _effects = effects;
        }

        public void Start()
        {
            _state.Width.Value = Width;
            _state.Height.Value = Height;
            _subscriptions.Add(_moves.Subscribe(e => Move(e.Uid, e.X, e.Y, e.Rot)));
            _subscriptions.Add(_uses.Subscribe(e => Use(e.Uid)));
            _subscriptions.Add(_drops.Subscribe(e => Drop(e.Uid)));
            // The kit follows the class while the character is being set up; a new run packs it again.
            _subscriptions.Add(_sheet.ClassId.Subscribe(_ => Repack()));
            _subscriptions.Add(_sheet.Locked.Subscribe(locked =>
            {
                if (!locked) _kitFor = null;   // unlocked: a new run (or the first), so a fresh kit
                Repack();
            }));
            Publish();
        }

        void Repack()
        {
            var cls = _sheet.ClassId.Value;
            if (string.IsNullOrEmpty(cls) || _sheet.Locked.Value) return;
            if (_kitFor == cls && _items.Count > 0) return;
            _kitFor = cls;
            _items.Clear();
            foreach (var kit in Config.Kits)
            {
                if (kit.ClassId != cls) continue;
                foreach (var id in kit.Items) TryAdd(id, null, null, -1, -1, publish: false);
            }
            Publish();
        }

        public void Tick()
        {
            // Strength sets the bulk limit; it moves while you shift points.
            var str = Strength();
            if (str != _strength)
            {
                _strength = str;
                Publish();
            }
        }

        // ---- IInventory ---------------------------------------------------------------------------------------

        public bool TryAdd(string itemId, string name, string description, int value, int relic) =>
            TryAdd(itemId, name, description, value, relic, publish: true);

        bool TryAdd(string itemId, string name, string description, int value, int relic, bool publish)
        {
            var def = Config.Items.Find(i => i.Id == itemId);
            if (def == null)
            {
                Debug.LogWarning($"[Inventory] no item '{itemId}' in items.txtpb");
                return false;
            }
            var item = new Item
            {
                Uid = _nextUid++, Def = def, Name = string.IsNullOrEmpty(name) ? def.Name : name,
                Description = string.IsNullOrEmpty(description) ? def.Description : description,
                Value = value >= 0 ? value : def.Value, Relic = relic,
            };
            for (var rot = 0; rot < 4; rot++)
            {
                var cells = Rotate(Parse(def.Shape), rot);
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    if (!Fits(cells, x, y, null)) continue;
                    item.X = x;
                    item.Y = y;
                    item.Rot = rot;
                    item.Cells = cells;
                    _items.Add(item);
                    if (publish) Publish();
                    return true;
                }
            }
            if (publish) Say((Config.FullPrompt ?? "No room for {name}").Replace("{name}", item.Name));
            return false;
        }

        public void ClearRelics()
        {
            if (_items.RemoveAll(i => i.Relic >= 0) > 0) Publish();
        }

        public void Say(string text)
        {
            _state.Message.Value = text ?? "";
        }

        // ---- page requests ------------------------------------------------------------------------------------

        void Move(int uid, int x, int y, int rot)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null) return;
            rot = ((rot % 4) + 4) % 4;
            var cells = Rotate(Parse(item.Def.Shape), rot);
            if (!Fits(cells, x, y, item)) return;
            item.X = x;
            item.Y = y;
            item.Rot = rot;
            item.Cells = cells;
            Publish();
        }

        void Use(int uid)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null) return;
            if (item.Relic >= 0)
            {
                _relicUse.Publish(new RelicUseRequested(item.Relic));
                return;
            }
            if (item.Def.Effects.Count == 0)
            {
                Say($"{item.Name}: nothing to use it on yet");
                return;
            }
            foreach (var effect in item.Def.Effects)
            {
                if ((effect == RelicEffect.RevealNearest || effect == RelicEffect.RevealAll || effect == RelicEffect.Slow) &&
                    _flow.Scene.Value != GameScene.Planet)
                {
                    Say($"{item.Name}: nothing out here to find");
                    return;
                }
            }
            foreach (var effect in item.Def.Effects)
            {
                switch (effect)
                {
                    case RelicEffect.Heal:
                    {
                        // HP first; what's left over tops up Stamina (as relic heals do).
                        var amount = Mathf.RoundToInt(item.Def.Amount);
                        var toHp = Mathf.Min(amount, _health.Max.Value - _health.Current.Value);
                        _health.Current.Value += toHp;
                        _sheet.Stamina.Value = Mathf.Min(_sheet.StaminaMax.Value, _sheet.Stamina.Value + amount - toHp);
                        break;
                    }
                    case RelicEffect.FullHeal:
                        _health.Current.Value = _health.Max.Value;
                        _sheet.Stamina.Value = _sheet.StaminaMax.Value;
                        break;
                    default:
                        _effects.Publish(new ItemEffectRequested(effect, item.Def.Amount, item.Def.Seconds));
                        break;
                }
            }
            Say($"Used {item.Name}");
            if (item.Def.Consumable) _items.Remove(item);
            Publish();
        }

        void Drop(int uid)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null) return;
            if (item.Relic >= 0)
            {
                Say("The museum wants every relic back: you can't leave one behind");
                return;
            }
            _items.Remove(item);
            Say($"Dropped {item.Name}");
            Publish();
        }

        // ---- grid ---------------------------------------------------------------------------------------------

        bool Fits(bool[,] cells, int x, int y, Item ignore)
        {
            int h = cells.GetLength(0), w = cells.GetLength(1);
            if (x < 0 || y < 0 || x + w > Width || y + h > Height) return false;
            for (var r = 0; r < h; r++)
            for (var c = 0; c < w; c++)
            {
                if (!cells[r, c]) continue;
                foreach (var other in _items)
                {
                    if (other == ignore) continue;
                    int lr = y + r - other.Y, lc = x + c - other.X;
                    if (lr >= 0 && lc >= 0 && lr < other.Cells.GetLength(0) && lc < other.Cells.GetLength(1) && other.Cells[lr, lc])
                        return false;
                }
            }
            return true;
        }

        /// <summary>"##/#." -> cells [row, column]; empty: one cell.</summary>
        static bool[,] Parse(string shape)
        {
            var rows = string.IsNullOrEmpty(shape) ? new[] { "#" } : shape.Split('/');
            var w = 1;
            foreach (var r in rows) w = Math.Max(w, r.Length);
            var cells = new bool[rows.Length, w];
            for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                cells[r, c] = rows[r][c] == '#';
            return cells;
        }

        /// <summary>`turns` quarter turns clockwise.</summary>
        static bool[,] Rotate(bool[,] cells, int turns)
        {
            for (var t = 0; t < turns; t++)
            {
                int h = cells.GetLength(0), w = cells.GetLength(1);
                var turned = new bool[w, h];
                for (var r = 0; r < h; r++)
                for (var c = 0; c < w; c++)
                    turned[c, h - 1 - r] = cells[r, c];
                cells = turned;
            }
            return cells;
        }

        static string Shape(bool[,] cells)
        {
            var sb = new StringBuilder();
            for (var r = 0; r < cells.GetLength(0); r++)
            {
                if (r > 0) sb.Append('/');
                for (var c = 0; c < cells.GetLength(1); c++) sb.Append(cells[r, c] ? '#' : '.');
            }
            return sb.ToString();
        }

        // ---- state --------------------------------------------------------------------------------------------

        int Strength()
        {
            var abilities = GameContent.Character.Abilities;
            for (var i = 0; i < abilities.Count && i < _sheet.Scores.Count; i++)
                if (abilities[i].Id == "str") return _sheet.Scores[i];
            return 10;
        }

        void Publish()
        {
            var rows = new List<string>(_items.Count);
            float bulk = 0f;
            var value = 0;
            foreach (var i in _items)
            {
                bulk += i.Def.Bulk;
                value += i.Value;
                var c = i.Def.Color;
                var color = "#" + ColorUtility.ToHtmlStringRGB(new Color(c.R, c.G, c.B));
                var actions = (i.Relic >= 0 || i.Def.Effects.Count > 0 ? 1 : 0) | (i.Relic < 0 ? 2 : 0);
                rows.Add(string.Join("|", i.Uid, i.Def.Id, Clean(i.Name), (int)i.Def.Kind, i.X, i.Y, i.Rot, Shape(i.Cells),
                                     i.Def.Bulk.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), i.Value,
                                     color, Clean(i.Description), actions));
            }
            var str = Strength();
            var limit = str * Config.BulkPerStr;
            var max = str * Config.OverloadPerStr;
            var load = bulk > max ? 2 : bulk > limit ? 1 : 0;
            _state.Bulk.Value = bulk;
            _state.BulkLimit.Value = limit;
            _state.BulkMax.Value = max;
            _state.Load.Value = load;
            _state.Speed.Value = load == 2 ? Config.OverloadedSpeed : load == 1 ? Config.EncumberedSpeed : 1f;
            _state.Value.Value = value;
            Set(_state.Items, rows);
        }

        static string Clean(string s) => (s ?? "").Replace('|', '/');

        static void Set(ObservableList<string> list, List<string> values)
        {
            var same = list.Count == values.Count;
            for (var i = 0; same && i < values.Count; i++) same = list[i] == values[i];
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
