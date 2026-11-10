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
        /// Put item `itemId` in the first place it fits (any rotation), stacking if it can. False: no room, or it's
        /// unique and you already have it.
        /// </summary>
        bool TryAdd(string itemId);

        /// <summary>Is item `itemId` in the pack or worn (e.g. a unique relic you already picked up)?</summary>
        bool Has(string itemId);

        /// <summary>Take every relic out and say what they were (name, value): the museum buys them at the station.</summary>
        System.Collections.Generic.List<(string Name, int Value)> TakeRelics();

        /// <summary>Show `text` as the pack's message (e.g. "No room for ...").</summary>
        void Say(string text);

        /// <summary>
        /// Where dropped items go: the scene with a world to drop into (the crash site) sets itself, and clears it when
        /// it ends. Null: no world (Drop removes the item, a full-pack unequip is refused).
        /// </summary>
        IDropTarget DropTarget { get; set; }

        /// <summary>Pick a dropped item back up (it stacks if it can). False: no room, it stays where it lies.</summary>
        bool TryAdd(DroppedItem item);
    }

    /// <summary>Something that left the pack for the world: what it is and how many.</summary>
    public sealed class DroppedItem
    {
        public ItemDef Def;
        public string Name, Description;
        public int Value, Count;
    }

    /// <summary>A world items can be dropped into (lay `item` at the player's feet).</summary>
    public interface IDropTarget
    {
        /// <summary>Somewhere to drop it right now (on foot, not in the cockpit).</summary>
        bool CanDrop { get; }
        void Drop(DroppedItem item);
    }

    /// <summary>What equipped gear adds up to (CharacterPresenter folds it into the sheet).</summary>
    public interface IEquipment
    {
        int AbilityBonus(string ability);
        int SkillBonus(string skill);
        int StaminaBonus { get; }
        int HpBonus { get; }
        int RelicBonus { get; }
    }

    /// <summary>
    /// The pack (content/items.txtpb), app-wide: a grid items fit into by shape, like falling blocks. Your class's
    /// starting kit is packed while you set up your character (repacked when you change class) and kept once you
    /// leave the station; relics you pick up go in too (IInventory). The page's grid moves items
    /// (InventoryMoveRequested: cell + quarter turns, checked here), uses them (consumables heal or reveal; relics roll
    /// their d20 table: the die, + your luck die, + the sheet's relic bonus, naturals stand; R uses the ready one) and
    /// drops them. Bulk against your Strength gives the load and walking speed.
    /// Gear goes into its equipment slot (InventoryEquipRequested, or dragged onto the slot) and out of the grid; its
    /// bonuses reach the sheet through IEquipment (EquipmentChanged). Nothing is drawn: you're first-person arms.
    /// Unequipping puts it back where there's room (InventoryUnequipRequested, or dragged onto a cell). The kit's gear
    /// starts equipped. Everything goes to PlayerInventory for the page.
    /// </summary>
    [Presenter]
    public sealed class InventoryPresenter : IInventory, IEquipment, IStartable, ITickable, IDisposable
    {
        sealed class Item
        {
            public int Uid;
            public ItemDef Def;
            public string Name, Description;
            public int Value, X, Y, Rot;
            public EquipSlot Equipped;   // None: in the grid
            public int Count = 1;        // copies in this stack (ItemDef.stack)
            public bool[,] Cells;   // rotated footprint [row, column]
        }

        readonly PlayerInventory _state;
        readonly CharacterSheet _sheet;
        readonly PlayerHealth _health;
        readonly FlowState _flow;
        readonly ISubscriber<InventoryMoveRequested> _moves;
        readonly ISubscriber<InventoryUseRequested> _uses;
        readonly ISubscriber<InventoryDropRequested> _drops;
        readonly ISubscriber<InventoryEquipRequested> _equips;
        readonly ISubscriber<InventoryUnequipRequested> _unequips;
        readonly IPublisher<EquipmentChanged> _equipmentChanged;
        readonly IPublisher<ItemEffectRequested> _effects;
        readonly IPublisher<RelicRolled> _rolled;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly Dictionary<int, float> _readyAt = new();   // uid -> Time.time it can be used again
        int _ready, _rolls;
        float _luck = -1f;
        uint _seed = 1u;
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
                                  ISubscriber<InventoryDropRequested> drops,
                                  IPublisher<ItemEffectRequested> effects, ISubscriber<InventoryEquipRequested> equips,
                                  ISubscriber<InventoryUnequipRequested> unequips, IPublisher<EquipmentChanged> equipmentChanged,
                                  IPublisher<RelicRolled> rolled, ISubscriber<InputActionPerformed> actions,
                                  IBufferedSubscriber<UniverseSeedChanged> seeds)
        {
            _rolled = rolled;
            _actions = actions;
            _seeds = seeds;
            _equips = equips;
            _unequips = unequips;
            _equipmentChanged = equipmentChanged;
            _state = state;
            _sheet = sheet;
            _health = health;
            _flow = flow;
            _moves = moves;
            _uses = uses;
            _drops = drops;
            _effects = effects;
        }

        public void Start()
        {
            _state.Width.Value = Width;
            _state.Height.Value = Height;
            _subscriptions.Add(_moves.Subscribe(e => Move(e.Uid, e.X, e.Y, e.Rot)));
            _subscriptions.Add(_uses.Subscribe(e => Use(e.Uid)));
            _subscriptions.Add(_drops.Subscribe(e => Drop(e.Uid)));
            _subscriptions.Add(_equips.Subscribe(e => Equip(e.Uid)));
            _subscriptions.Add(_unequips.Subscribe(e => Unequip((EquipSlot)e.Slot)));
            // The title's roll: the lucky number seeds relic rolls, its luck die (0..1, -1: none) adds to them.
            _subscriptions.Add(_seeds.Subscribe(e => { _seed = e.Seed == 0 ? 1u : e.Seed; _luck = e.Luck; }));
            // R: the ready relic, at the crash site.
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.UseRelic && _ready != 0 && _flow.Scene.Value == GameScene.Planet) Use(_ready);
            }));
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
                foreach (var id in kit.Items) TryAdd(id, publish: false);
            }
            // The kit's gear starts on you.
            foreach (var item in _items.ToArray())
                if (item.Def.Slot != EquipSlot.None && SlotItem(item.Def.Slot) == null) item.Equipped = item.Def.Slot;
            // Then pack what's left tight from the top left (equipping left holes).
            var loose = _items.FindAll(i => i.Equipped == EquipSlot.None);
            foreach (var item in loose) item.Equipped = (EquipSlot)(-1);   // out of the grid while it's repacked
            foreach (var item in loose) if (!Place(item)) item.Equipped = EquipSlot.None;
            Publish();
            _equipmentChanged.Publish(new EquipmentChanged());
        }

        public void Tick()
        {
            // The ready relic's cooldown, for the page.
            var cooldown = _ready != 0 && _readyAt.TryGetValue(_ready, out var at) ? Mathf.Max(0f, at - Time.time) : 0f;
            if (Mathf.Abs(_state.Cooldown.Value - cooldown) > 0.05f || (cooldown == 0f && _state.Cooldown.Value != 0f))
                _state.Cooldown.Value = cooldown;
            // Strength sets the bulk limit; it moves while you shift points.
            // Dropping is possible only while the drop target says so (on foot), which changes as you land.
            var canDrop = _dropTarget != null && _dropTarget.CanDrop;
            if (_state.CanDrop.Value != canDrop) _state.CanDrop.Value = canDrop;
            var str = Strength();
            if (str != _strength)
            {
                _strength = str;
                Publish();
            }
        }

        // ---- IInventory ---------------------------------------------------------------------------------------

        public bool TryAdd(string itemId) => TryAdd(itemId, publish: true);

        public bool Has(string itemId) => _items.Exists(i => i.Def.Id == itemId);

        bool TryAdd(string itemId, bool publish)
        {
            var def = Config.Items.Find(i => i.Id == itemId);
            if (def == null)
            {
                Debug.LogWarning($"[Inventory] no item '{itemId}' in items.txtpb");
                return false;
            }
            var item = new Item { Uid = _nextUid++, Def = def, Name = def.Name, Description = Describe(def), Value = def.Value };
            return Add(item, publish);
        }

        public bool TryAdd(DroppedItem dropped)
        {
            if (dropped?.Def == null) return false;
            var item = new Item
            {
                Uid = _nextUid++, Def = dropped.Def, Name = dropped.Name, Description = dropped.Description,
                Value = dropped.Value, Count = Math.Max(1, dropped.Count),
            };
            return Add(item, publish: true);
        }

        IDropTarget _dropTarget;

        public IDropTarget DropTarget
        {
            get => _dropTarget;
            set
            {
                _dropTarget = value;
                _state.CanDrop.Value = value != null && value.CanDrop;
            }
        }

        static int StackLimit(ItemDef def) => def.Unique ? 1 : Math.Max(1, def.Stack);

        /// <summary>
        /// Into the pack: unique items refuse a second copy; stackable ones top up a stack of the same item first;
        /// the rest goes in the first place it fits (any rotation).
        /// </summary>
        bool Add(Item item, bool publish)
        {
            var def = item.Def;
            if (def.Unique && _items.Exists(i => i.Def.Id == def.Id))
            {
                if (publish) Say($"You already have {item.Name}");
                return false;
            }
            var limit = StackLimit(def);
            if (limit > 1)
            {
                foreach (var other in _items)
                {
                    if (other.Def != def || other.Equipped != EquipSlot.None || other.Count >= limit) continue;
                    var moved = Math.Min(item.Count, limit - other.Count);
                    other.Count += moved;
                    item.Count -= moved;
                    if (item.Count == 0)
                    {
                        if (publish) Publish();
                        return true;
                    }
                }
            }
            item.Count = Math.Min(item.Count, limit);
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
                    if (item.Def.Use != null && item.Def.Use.Rolls.Count > 0) _ready = item.Uid;   // just picked up: R uses it
                    if (publish) Publish();
                    return true;
                }
            }
            if (publish)
            {
                Publish();   // a partly topped-up stack still changed
                Say((Config.FullPrompt ?? "No room for {name}").Replace("{name}", item.Name));
            }
            return false;
        }

        public System.Collections.Generic.List<(string Name, int Value)> TakeRelics()
        {
            var taken = new System.Collections.Generic.List<(string, int)>();
            foreach (var i in _items) if (i.Def.Kind == ItemKind.Relic) taken.Add((i.Name, i.Value));
            if (_items.RemoveAll(i => i.Def.Kind == ItemKind.Relic) > 0) Publish();
            return taken;
        }

        /// <summary>Out of the pack (and its slot) into the world at your feet, the whole stack. False: no world.</summary>
        bool ToWorld(Item item)
        {
            if (DropTarget == null || !DropTarget.CanDrop) return false;
            _items.Remove(item);
            DropTarget.Drop(new DroppedItem { Def = item.Def, Name = item.Name, Description = item.Description, Value = item.Value, Count = item.Count });
            return true;
        }

        /// <summary>A relic's description: whose it was and when, then its lore; else the item's own text.</summary>
        static string Describe(ItemDef def)
        {
            var lore = def.Lore;
            if (lore == null || string.IsNullOrEmpty(lore.Text)) return def.Description;
            var who = string.IsNullOrEmpty(lore.Figure) ? "" : string.IsNullOrEmpty(lore.Era) ? $"{lore.Figure}. " : $"{lore.Figure}, {lore.Era}. ";
            return who + lore.Text;
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
            var wasEquipped = item.Equipped != EquipSlot.None;
            item.X = x;
            item.Y = y;
            item.Rot = rot;
            item.Cells = cells;
            item.Equipped = EquipSlot.None;   // dragged from its slot onto the grid: taken off
            Publish();
            if (wasEquipped) _equipmentChanged.Publish(new EquipmentChanged());
        }

        void Use(int uid)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null) return;
            if (item.Def.Use != null && item.Def.Use.Rolls.Count > 0)
            {
                Roll(item);
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
            foreach (var effect in item.Def.Effects) Apply(effect, item.Def.Amount, item.Def.Seconds);
            Say($"Used {item.Name}");
            if (item.Def.Consumable && --item.Count <= 0) _items.Remove(item);
            Publish();
        }

        /// <summary>
        /// A relic's d20: seeded by the lucky number (so a universe always rolls the same), plus up to luck_bonus from
        /// the luck die and the sheet's relic bonus; a natural 1 is always 1 and a natural 20 always 20. The first row
        /// whose range holds the total happens, then the relic rests for its cooldown. It becomes the ready one (R).
        /// </summary>
        void Roll(Item item)
        {
            var use = item.Def.Use;
            _ready = item.Uid;
            if (_readyAt.TryGetValue(item.Uid, out var at) && Time.time < at)
            {
                Say($"{item.Name} needs {Mathf.CeilToInt(at - Time.time)} s more");
                Publish();
                return;
            }
            var rng = new Unity.Mathematics.Random((_seed ^ (0xD20u * (uint)(++_rolls))) | 1u);
            var roll = rng.NextInt(1, 21);
            var luck = _luck >= 0f ? Mathf.RoundToInt(_luck * Mathf.Max(0, GameContent.Cargo.LuckBonus)) : 0;
            var bonus = luck + _sheet.RelicBonus.Value;
            var total = roll == 1 ? 1 : roll == 20 ? 20 : Mathf.Clamp(roll + bonus, 1, 20);
            RelicRoll row = null;
            foreach (var r in use.Rolls) if (total >= r.Min && total <= r.Max) { row = r; break; }
            row ??= use.Rolls[use.Rolls.Count - 1];
            foreach (var effect in row.Effects) Apply(effect, row.Amount, row.Seconds);
            _readyAt[item.Uid] = Time.time + use.CooldownSeconds;
            _rolled.Publish(new RelicRolled(item.Name, roll, total - roll, total, row.Title, row.Text));
            Debug.Log($"[Inventory] {item.Name}: d20 {roll} + {bonus} = {total}: {row.Title}");
            Say($"{item.Name}: {total} - {row.Title}");
            Publish();
        }

        /// <summary>
        /// One effect: heals here (HP first, the rest to Stamina; full heal both); the rest (reveal, slow) to whoever
        /// can do them (the crash site, ItemEffectRequested).
        /// </summary>
        void Apply(RelicEffect effect, float amountF, float seconds)
        {
            switch (effect)
            {
                case RelicEffect.Heal:
                {
                    var amount = Mathf.RoundToInt(amountF);
                    var toHp = Mathf.Min(amount, _health.Max.Value - _health.Current.Value);
                    _health.Current.Value += toHp;
                    _sheet.Stamina.Value = Mathf.Min(_sheet.StaminaMax.Value, _sheet.Stamina.Value + amount - toHp);
                    break;
                }
                case RelicEffect.FullHeal:
                    _health.Current.Value = _health.Max.Value;
                    _sheet.Stamina.Value = _sheet.StaminaMax.Value;
                    break;
                case RelicEffect.None:
                    break;
                default:
                    _effects.Publish(new ItemEffectRequested(effect, amountF, seconds));
                    break;
            }
        }

        Item SlotItem(EquipSlot slot) => slot == EquipSlot.None ? null : _items.Find(i => i.Equipped == slot);

        /// <summary>Into its slot; what was there goes back into the pack (or the swap doesn't happen).</summary>
        void Equip(int uid)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null || item.Def.Slot == EquipSlot.None || item.Equipped != EquipSlot.None) return;
            var current = SlotItem(item.Def.Slot);
            item.Equipped = item.Def.Slot;   // frees its cells first, so the swapped-out one can take them
            if (current != null && !Place(current))
            {
                // No room: the old one drops at your feet (or, with no world here, the swap doesn't happen).
                if (!ToWorld(current))
                {
                    item.Equipped = EquipSlot.None;
                    Say($"No room in your pack to take off {current.Name}");
                    Publish();
                    return;
                }
                Say($"Equipped {item.Name}; no room for {current.Name}, dropped at your feet");
            }
            else Say($"Equipped {item.Name}");
            Publish();
            _equipmentChanged.Publish(new EquipmentChanged());
        }

        void Unequip(EquipSlot slot)
        {
            var item = SlotItem(slot);
            if (item == null) return;
            if (!Place(item))
            {
                if (!ToWorld(item))
                {
                    Say($"No room in your pack for {item.Name}");
                    return;
                }
                Say($"No room in your pack: {item.Name} dropped at your feet");
            }
            else Say($"Took off {item.Name}");
            Publish();
            _equipmentChanged.Publish(new EquipmentChanged());
        }

        /// <summary>Back into the grid wherever it fits first (any rotation); false if it doesn't.</summary>
        bool Place(Item item)
        {
            var wasEquipped = item.Equipped;
            item.Equipped = EquipSlot.None;
            for (var rot = 0; rot < 4; rot++)
            {
                var cells = Rotate(Parse(item.Def.Shape), rot);
                for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    if (!Fits(cells, x, y, item)) continue;
                    item.X = x;
                    item.Y = y;
                    item.Rot = rot;
                    item.Cells = cells;
                    return true;
                }
            }
            item.Equipped = wasEquipped;
            return false;
        }

        // ---- IEquipment ---------------------------------------------------------------------------------------

        public int AbilityBonus(string ability)
        {
            var total = 0;
            foreach (var i in _items)
                if (i.Equipped != EquipSlot.None)
                    foreach (var b in i.Def.AbilityBonuses) if (b.Ability == ability) total += b.Amount;
            return total;
        }

        public int SkillBonus(string skill)
        {
            var total = 0;
            foreach (var i in _items)
                if (i.Equipped != EquipSlot.None)
                    foreach (var b in i.Def.SkillBonuses) if (b.Skill == skill) total += b.Amount;
            return total;
        }

        public int StaminaBonus => SumEquipped(d => d.StaminaBonus);
        public int HpBonus => SumEquipped(d => d.HpBonus);
        public int RelicBonus => SumEquipped(d => d.RelicBonus);
        float BulkBonus
        {
            get
            {
                var total = 0f;
                foreach (var i in _items) if (i.Equipped != EquipSlot.None) total += i.Def.BulkBonus;
                return total;
            }
        }

        int SumEquipped(Func<ItemDef, int> pick)
        {
            var total = 0;
            foreach (var i in _items) if (i.Equipped != EquipSlot.None) total += pick(i.Def);
            return total;
        }

        void Drop(int uid)
        {
            var item = _items.Find(i => i.Uid == uid);
            if (item == null) return;
            if (item.Def.Kind == ItemKind.Relic)
            {
                Say("The museum wants every relic back: you can't leave one behind");
                return;
            }
            var wasEquipped = item.Equipped != EquipSlot.None;
            if (ToWorld(item)) Say($"Dropped {item.Name}{(item.Count > 1 ? $" x{item.Count}" : "")} at your feet");
            else if (_flow.Scene.Value == GameScene.Title)
            {
                _items.Remove(item);   // setting up your character: there's no world yet, it just leaves the kit
                Say($"Removed {item.Name}");
            }
            else
            {
                Say(_dropTarget != null ? $"Land first: nowhere to drop {item.Name} from the cockpit" : $"Nowhere to drop {item.Name} here");
                return;
            }
            Publish();
            if (wasEquipped) _equipmentChanged.Publish(new EquipmentChanged());
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
                    if (other == ignore || other.Equipped != EquipSlot.None) continue;   // equipped: not in the grid
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
                bulk += i.Def.Bulk * i.Count;
                value += i.Value * i.Count;
                var c = i.Def.Color;
                var color = "#" + ColorUtility.ToHtmlStringRGB(new Color(c.R, c.G, c.B));
                var usable = i.Def.Effects.Count > 0 || (i.Def.Use != null && i.Def.Use.Rolls.Count > 0);
                var actions = (usable ? 1 : 0) | (i.Def.Kind != ItemKind.Relic ? 2 : 0) |
                              (i.Def.Slot != EquipSlot.None ? 4 : 0);
                rows.Add(string.Join("|", i.Uid, i.Def.Id, Clean(i.Name), (int)i.Def.Kind, i.X, i.Y, i.Rot, Shape(i.Cells),
                                     i.Def.Bulk.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), i.Value,
                                     color, Clean(i.Description), actions, (int)i.Def.Slot, (int)i.Equipped,
                                     Clean(BonusText(i.Def)), i.Count));
            }
            var str = Strength();
            var extra = BulkBonus;
            var limit = str * Config.BulkPerStr + extra;
            var max = str * Config.OverloadPerStr + extra;
            var load = bulk > max ? 2 : bulk > limit ? 1 : 0;
            _state.Bulk.Value = bulk;
            _state.BulkLimit.Value = limit;
            _state.BulkMax.Value = max;
            _state.Load.Value = load;
            _state.Speed.Value = load == 2 ? Config.OverloadedSpeed : load == 1 ? Config.EncumberedSpeed : 1f;
            _state.Value.Value = value;
            Set(_state.Items, rows);
            Set(_state.GearBonuses, GearTotals());
            // The ready relic: the last one picked up or used, else any you carry that rolls.
            if (_ready != 0 && !_items.Exists(i => i.Uid == _ready)) _ready = 0;
            if (_ready == 0)
            {
                var any = _items.Find(i => i.Def.Use != null && i.Def.Use.Rolls.Count > 0);
                if (any != null) _ready = any.Uid;
            }
            var ready = _items.Find(i => i.Uid == _ready);
            _state.Ready.Value = _ready;
            _state.ReadyName.Value = ready?.Name ?? "";
        }

        /// <summary>"Engineering +2, STR +1, Stamina +2"</summary>
        static string BonusText(ItemDef d)
        {
            var parts = new List<string>();
            foreach (var b in d.AbilityBonuses) parts.Add($"{AbilityAbbr(b.Ability)} {Signed(b.Amount)}");
            foreach (var b in d.SkillBonuses) parts.Add($"{SkillName(b.Skill)} {Signed(b.Amount)}");
            if (d.StaminaBonus != 0) parts.Add($"Stamina {Signed(d.StaminaBonus)}");
            if (d.HpBonus != 0) parts.Add($"HP {Signed(d.HpBonus)}");
            if (d.RelicBonus != 0) parts.Add($"Relic rolls {Signed(d.RelicBonus)}");
            if (d.BulkBonus != 0f) parts.Add($"Carry +{d.BulkBonus:0.#} bulk");
            return string.Join(", ", parts);
        }

        /// <summary>Everything equipped, added up: "label|+N" rows.</summary>
        List<string> GearTotals()
        {
            var rows = new List<string>();
            foreach (var a in GameContent.Character.Abilities)
            {
                var v = AbilityBonus(a.Id);
                if (v != 0) rows.Add($"{a.Abbr}|{Signed(v)}");
            }
            foreach (var sk in GameContent.Character.Skills)
            {
                var v = SkillBonus(sk.Id);
                if (v != 0) rows.Add($"{sk.Name}|{Signed(v)}");
            }
            if (StaminaBonus != 0) rows.Add($"Stamina|{Signed(StaminaBonus)}");
            if (HpBonus != 0) rows.Add($"HP|{Signed(HpBonus)}");
            if (RelicBonus != 0) rows.Add($"Relic rolls|{Signed(RelicBonus)}");
            if (BulkBonus != 0f) rows.Add($"Carry|+{BulkBonus:0.#} bulk");
            return rows;
        }

        static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();
        static string AbilityAbbr(string id) => GameContent.Character.Abilities.Find(a => a.Id == id)?.Abbr ?? id.ToUpperInvariant();
        static string SkillName(string id) => GameContent.Character.Skills.Find(s => s.Id == id)?.Name ?? id;

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
