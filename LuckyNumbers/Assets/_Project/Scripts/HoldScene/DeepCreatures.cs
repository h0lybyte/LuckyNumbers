using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Hold;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Creatures in the mines (HoldLife.creatures: combat.txtpb monster types), placed by HoldLifePlan in a region's
    /// chambers as it's shown and gone with it (RegionHidden). Each keeps to its own region's cells: it wanders a few
    /// steps at a time along walkable cells, notices you within `sight` when it can see you (a ray to your eyes), then
    /// chases along a cell-graph path (breadth-first on the grid, no NavMesh) and bites from `reach`.
    ///
    /// The fight is the crash site's (CombatPresenter), reused piece by piece rather than the presenter itself (that
    /// one is tied to the crash site: wreck, ground, jobs, encounters): MonsterView / MonsterRig / MonsterBrain for
    /// the body and its states, Marksman + Dice for the d20s, your weapon's bolts (the drawn gun through
    /// FirstPersonPresenter.ShotCheck and WeaponView's bolts; an unmodelled equipped weapon from the hip through
    /// Bolts), CombatRoll to the page, Stamina then HP off PlayerHealth / CharacterSheet. At 0 HP you're down: the
    /// rescue drone (combat.txtpb `rescue`) takes you to the station without your relics, a fee lighter.
    /// </summary>
    [Presenter]
    public sealed class DeepCreatures : IStartable, ITickable, ILateTickable, IDisposable
    {
        sealed class Creature
        {
            public MonsterType Type;
            public MonsterView View;
            public int3 Region;
            public HoldGrid Grid;          // its region's (valid until RegionHidden, which removes it first)
            public Vector3 Position, Size;
            public float Heading, Wait, LookAt, DeathFor;
            public bool Sees;
            public int Hp;
            public MonsterBrain Brain;
            public readonly List<int2> Path = new();   // local xz cells on its level, next first
            public int2 PathTo = new(-1, -1);
            public float RepathAt;
            public readonly Queue<int> Incoming = new();
        }

        readonly HoldWorld _world;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly PlayerInput _input;
        readonly PlayerHealth _health;
        readonly CharacterSheet _sheet;
        readonly IInventory _pack;
        readonly PlayerVoyage _voyage;
        readonly CombatState _state;
        readonly IPublisher<CombatRoll> _rolls;
        readonly IPublisher<PlayerDamaged> _damaged;
        readonly IPublisher<PlayerDied> _died;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly AssetScope _assets;
        readonly List<Creature> _creatures = new();
        IDisposable _warpSub;

        /// <summary>Dev (LN.send('DevWarpRequested', {to: 'creature'})): stand 3-6 cells from the nearest creature, in its sight.</summary>
        void WarpToCreature()
        {
            var me = _player.Body.transform.position;
            Creature best = null;
            foreach (var c in _creatures)
                if (!c.Brain.Dead && c.View != null && (best == null || (c.Position - me).sqrMagnitude < (best.Position - me).sqrMagnitude)) best = c;
            if (best == null) { Debug.Log("[HoldLife] dev warp creature: none loaded"); return; }
            var cell = _world.CellOf(best.Position + Vector3.up * 0.2f);
            for (var r = 3; r <= 6; r++)
            for (var dx = -r; dx <= r; dx++)
            for (var dz = -r; dz <= r; dz++)
            {
                if (math.max(math.abs(dx), math.abs(dz)) != r) continue;
                var to = cell + new int3(dx, 0, dz);
                if (!_world.TryCell(to, out var t) || t.Kind != CellKind.Mine || (t.Flags & CellFlags.Pillar) != 0) continue;
                var at = (Vector3)_world.CellFloor(to) + Vector3.up * 0.05f;
                if (Physics.Linecast(at + Vector3.up * 1.6f, best.Position + Vector3.up * 1f)) continue;
                var body = _player.Body;
                body.enabled = false;
                var face = best.Position - at;
                body.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg, 0f));
                body.enabled = true;
                _firstPerson.Look(Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg, 0f);
                Debug.Log($"[HoldLife] dev warp creature: {best.Type} at {cell}, standing at {to}");
                return;
            }
            Debug.Log("[HoldLife] dev warp creature: no clear cell near it");
        }
        readonly Dictionary<string, MonsterRig> _models = new();
        readonly List<int3> _pending = new();          // shown before the models were in
        readonly List<CachePlan> _unused = new();
        readonly List<CreaturePlan> _plans = new();
        readonly Marksman _marksman = new();
        readonly CancellationTokenSource _cts = new();
        readonly RaycastHit[] _hits = new RaycastHit[16];
        RaycastHit[] _aimHits = new RaycastHit[16];
        Bolts _bolts;
        Material _placeholder;
        Random _rng;
        bool _modelsReady, _down, _lockSeen, _firedOnce;
        float _activeFor, _nextShot, _downFade = -1f;

        static CombatConfig C => GameContent.Combat;
        static HoldLife Life => GameContent.Hold.Life;

        [Inject]
        public DeepCreatures(HoldWorld world, PlayerView player, FirstPersonPresenter firstPerson, PlanetView ui, PlayerInput input,
                             PlayerHealth health, CharacterSheet sheet, IInventory pack, PlayerVoyage voyage, CombatState state,
                             IPublisher<CombatRoll> rolls, IPublisher<PlayerDamaged> damaged, IPublisher<PlayerDied> died,
                             IPublisher<CharacterXpGained> xp, IPublisher<SceneChangeRequested> scenes, AssetScope assets,
                             ISubscriber<DevWarpRequested> warps)
        {
            _warpSub = warps.Subscribe(e => { if (e.To == "creature") WarpToCreature(); });
            _world = world;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _input = input;
            _health = health;
            _sheet = sheet;
            _pack = pack;
            _voyage = voyage;
            _state = state;
            _rolls = rolls;
            _damaged = damaged;
            _died = died;
            _xp = xp;
            _scenes = scenes;
            _assets = assets;
            // Now, not in Start: HoldPresenter's Start may show the first region before ours runs.
            _world.RegionShown += Shown;
            _world.RegionHidden += Hidden;
        }

        public void Start()
        {
            _state.Alive.Value = 0;
            _state.Killed.Value = 0;
            _state.Target.Value = "";
            _state.Downed.Value = false;
            _state.Armor.Value = PlayerArmor();
            _firstPerson.ShotCheck = HeldShot;
            _placeholder = Resources.Load<Material>("Placeholder");
            LoadModelsAsync(_cts.Token).Forget();
        }

        // ---- regions ---------------------------------------------------------------------------------------------

        void Shown(int3 region, HoldGrid grid)
        {
            if (Life == null || C == null) return;
            if (!_modelsReady) { _pending.Add(region); return; }
            Populate(region, grid);
        }

        void Hidden(int3 region)
        {
            _pending.Remove(region);
            for (var i = _creatures.Count - 1; i >= 0; i--)
            {
                if (!_creatures[i].Region.Equals(region)) continue;
                if (_creatures[i].View != null) UnityEngine.Object.Destroy(_creatures[i].View.gameObject);
                _creatures.RemoveAt(i);
            }
            _state.Alive.Value = AliveCount();
        }

        void Populate(int3 region, HoldGrid grid)
        {
            HoldLifePlan.Plan(_world.Seed, region, grid, Life, _unused, _plans);
            foreach (var plan in _plans)
            {
                var id = Life.Creatures[plan.Creature].Monster;
                MonsterType type = null;
                foreach (var t in C.Monsters) if (t.Id == id) type = t;
                if (type == null) { Debug.LogWarning($"[HoldLife] no monster '{id}' (combat.txtpb)"); continue; }
                Spawn(type, region, grid, plan.Cell);
            }
            if (_plans.Count > 0) Debug.Log($"[HoldLife] region {region}: {_plans.Count} creature(s)");
        }

        async UniTaskVoid LoadModelsAsync(CancellationToken ct)
        {
            var loads = new List<UniTask>();
            foreach (var creature in Life?.Creatures ?? new())
            foreach (var type in C?.Monsters ?? new())
            {
                if (type.Id != creature.Monster || string.IsNullOrEmpty(type.Model) || _models.ContainsKey(type.Model)) continue;
                _models[type.Model] = null;
                if (!AssetKeys.Monster.TryGetValue(type.Model, out var key)) continue;
                loads.Add(LoadModel(type.Model, key, ct));
            }
            try { await UniTask.WhenAll(loads); }
            catch (OperationCanceledException) { return; }
            _modelsReady = true;
            foreach (var region in _pending)
                if (_world.TryGrid(region, out var grid)) Populate(region, grid);
            _pending.Clear();
        }

        async UniTask LoadModel(string name, AssetKey<GameObject> key, CancellationToken ct)
        {
            try { _models[name] = (await _assets.LoadAsync(key, ct)).GetComponent<MonsterRig>(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Debug.LogWarning($"[HoldLife] {e.Message}; {name} stays a placeholder"); }
        }

        void Spawn(MonsterType type, int3 region, HoldGrid grid, int3 cell)
        {
            if (_bolts == null) Arm();
            var size = new Vector3(type.Size.X > 0f ? type.Size.X : 1f, type.Size.Y > 0f ? type.Size.Y : 1f, type.Size.Z > 0f ? type.Size.Z : 1f);
            _models.TryGetValue(type.Model ?? "", out var model);
            var view = model != null
                ? MonsterView.CreateModel(type.Id, model, size, type.Clips)
                : MonsterView.Create(type.Id, size, new Color(type.Color.R, type.Color.G, type.Color.B, 1f), _placeholder);
            var at = (Vector3)_world.CellFloor(cell);
            var c = new Creature
            {
                Type = type, View = view, Region = region, Grid = grid, Size = size, Position = at + Vector3.up * (size.y * 0.5f),
                Hp = math.max(1, type.Hp), Brain = MonsterBrain.Spawn(false), Heading = _rng.NextFloat(360f), Wait = _rng.NextFloat(1f, 4f),
            };
            view.Damaged += (v, source, point) => Hurt(c, source);
            Place(c);
            _creatures.Add(c);
            _state.Alive.Value = AliveCount();
        }

        void Arm()
        {
            _rng = new Random((_world.Seed ^ 0xD3E9C0u) | 1u);   // your lucky number rolls every fight down here too
            _bolts = new Bolts(_player.Body.transform);
        }

        // ---- ticking ---------------------------------------------------------------------------------------------

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f || C == null) return;
            _bolts?.Tick(dt);
            if (_down) return;
            var onFoot = _firstPerson.Active;
            _activeFor = onFoot ? _activeFor + dt : _activeFor;
            if (onFoot && _bolts != null) Shoot();
            var me = _player.Body.transform.position;
            for (var i = _creatures.Count - 1; i >= 0; i--) Think(_creatures[i], me, onFoot, dt);
        }

        /// <summary>After HoldPresenter's fade-in: while you're down, the fade to black is ours.</summary>
        public void LateTick()
        {
            if (_downFade >= 0f) _ui.Fade(_downFade);
        }

        int LevelOf(Creature c) => (int)math.floor((c.Position.y - c.Size.y * 0.5f + 0.1f) / _world.CellSize.y) - c.Grid.Origin.y;

        int2 LocalXZ(Vector3 p, HoldGrid g)
        {
            var w = _world.CellOf(p);
            return new int2(w.x - g.Origin.x, w.z - g.Origin.z);
        }

        bool PassableLocal(HoldGrid g, int level, int2 c) => HoldLifePlan.Passable(g.Get(new int3(c.x, level, c.y)));

        void Think(Creature c, Vector3 me, bool onFoot, float dt)
        {
            var eye = _player.Camera.transform.position;
            var to = new Vector3(me.x - c.Position.x, 0f, me.z - c.Position.z);
            var flat = to.magnitude;
            var dy = math.abs((me.y + 0.9f) - c.Position.y);
            var distance = flat + math.max(0f, dy - 1.4f) * 3f;   // another storey is a long way round
            var sight = Life.Sight > 0f ? Life.Sight : c.Type.Sight;
            var reach = math.max(0.8f, c.Type.Reach) + c.Size.z * 0.5f;

            // Line of sight, a few times a second (only when it matters: close enough to notice or bite).
            if (Time.time >= c.LookAt)
            {
                c.LookAt = Time.time + 0.2f;
                c.Sees = distance < sight * 3f && Sees(c, eye);
            }
            var grace = Life.GraceSeconds > 0f ? Life.GraceSeconds : MonsterBrain.NoticeAfter;
            var senses = new MonsterSenses
            {
                Dt = dt, Now = Time.time, OnFoot = onFoot, Sight = sight, Reach = reach, StandoffDistance = 0f, Busy = c.View.Busy,
                // Unseen, it can't notice you and can't bite through rock; chasing, it still follows the way you went.
                Distance = c.Brain.State == MonsterState.Chase ? (c.Sees ? distance : math.max(distance, reach + 0.1f))
                         : c.Sees ? distance : float.MaxValue,
                // The planet's grace: MonsterBrain waits NoticeAfter; ours is grace_seconds, mapped onto it.
                OnFootFor = _activeFor >= grace ? MonsterBrain.NoticeAfter + 1f : 0f,
            };
            var act = MonsterBrain.Tick(ref c.Brain, senses);
            var walked = 0f;
            switch (act)
            {
                case MonsterAct.Fade:
                    Fade(c);
                    return;
                case MonsterAct.Run:
                    walked = Walk(c, c.Position - to, c.Type.Speed * 1.6f, dt);
                    c.View.Move(walked, true);
                    Place(c);
                    return;
                case MonsterAct.Amble:
                case MonsterAct.OpenStandoff:
                    walked = Wander(c, dt);
                    break;
                default:
                    if (act == MonsterAct.Step) walked = Chase(c, me, flat, dt);
                    else
                    {
                        c.Heading = Mathf.MoveTowardsAngle(c.Heading, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 360f * dt);
                        if (act == MonsterAct.Bite) Bite(c);
                    }
                    break;
            }
            c.View.Move(walked, c.Brain.Hunting);
            Place(c);
        }

        bool Sees(Creature c, Vector3 eye)
        {
            var from = c.Position + Vector3.up * (c.Size.y * 0.35f);
            var dir = eye - from;
            var length = dir.magnitude;
            if (length < 0.01f) return true;
            var n = Physics.RaycastNonAlloc(from, dir / length, _hits, length, ~0, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < n; i++)
            {
                var t = _hits[i].collider.transform;
                if (t.IsChildOf(c.View.transform) || t.IsChildOf(_player.Body.transform)) continue;
                if (t.GetComponentInParent<MonsterView>() != null) continue;   // another creature doesn't hide you
                return false;
            }
            return true;
        }

        /// <summary>Idle: wait a moment, then a few random steps along walkable cells (no doubling back), at an amble.</summary>
        float Wander(Creature c, float dt)
        {
            if (c.Path.Count == 0)
            {
                c.Wait -= dt;
                if (c.Wait > 0f) return 0f;
                c.Wait = _rng.NextFloat(1.5f, 5f);
                var level = LevelOf(c);
                var at = LocalXZ(c.Position, c.Grid);
                var back = new int2(int.MinValue, 0);
                var steps = math.max(1, Life.WanderSteps > 0 ? Life.WanderSteps : 6);
                var dirs = new[] { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };
                for (var s = 0; s < steps; s++)
                {
                    var start = _rng.NextInt(4);
                    var moved = false;
                    for (var k = 0; k < 4 && !moved; k++)
                    {
                        var d = dirs[(start + k) % 4];
                        var next = at + d;
                        if (next.Equals(back) || !PassableLocal(c.Grid, level, next)) continue;
                        back = at;
                        at = next;
                        c.Path.Add(at);
                        moved = true;
                    }
                    if (!moved) break;
                }
                return 0f;
            }
            return FollowPath(c, c.Type.Speed * 0.3f, dt);
        }

        /// <summary>After you: straight at you when you're in view and close, else along a breadth-first path to your cell.</summary>
        float Chase(Creature c, Vector3 me, float flat, float dt)
        {
            var level = LevelOf(c);
            if (c.Sees && flat < _world.CellSize.x * 1.5f) { c.Path.Clear(); return Walk(c, me, c.Type.Speed, dt); }
            var target = LocalXZ(me, c.Grid);
            var myLevel = _world.CellOf(me + Vector3.up * 0.2f).y - c.Grid.Origin.y;
            var reachable = myLevel == level && PassableLocal(c.Grid, level, target);
            if (!reachable) { c.Path.Clear(); return Walk(c, me, c.Type.Speed, dt); }   // another level or region: as close as it gets
            if (!target.Equals(c.PathTo) || Time.time >= c.RepathAt || c.Path.Count == 0)
            {
                c.PathTo = target;
                c.RepathAt = Time.time + 0.6f;
                CellPaths.Find(c.Grid, level, LocalXZ(c.Position, c.Grid), target, Life.PathCells > 0 ? Life.PathCells : 600, c.Path);
                if (c.Path.Count > 0 && c.Path[0].Equals(LocalXZ(c.Position, c.Grid))) c.Path.RemoveAt(0);
            }
            if (c.Path.Count == 0) return Walk(c, me, c.Type.Speed, dt);
            return FollowPath(c, c.Type.Speed, dt);
        }

        float FollowPath(Creature c, float speed, float dt)
        {
            var g = c.Grid;
            var next = c.Path[0];
            var goal = (Vector3)_world.CellFloor(g.ToWorld(new int3(next.x, LevelOf(c), next.y)));
            goal.y = c.Position.y;
            if (new Vector2(goal.x - c.Position.x, goal.z - c.Position.z).magnitude < 0.25f)
            {
                c.Path.RemoveAt(0);
                if (c.Path.Count == 0) return 0f;
            }
            return Walk(c, goal, speed, dt);
        }

        /// <summary>Turn toward `goal` and step at `speed`, sliding along walls: it never leaves walkable cells of its region.</summary>
        float Walk(Creature c, Vector3 goal, float speed, float dt)
        {
            var to = new Vector3(goal.x - c.Position.x, 0f, goal.z - c.Position.z);
            if (to.sqrMagnitude < 1e-4f) return 0f;
            c.Heading = Mathf.MoveTowardsAngle(c.Heading, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 420f * dt);
            var step = to.normalized * math.min(speed * dt, to.magnitude);
            var before = c.Position;
            var level = LevelOf(c);
            var radius = math.min(0.45f, math.max(c.Size.x, c.Size.z) * 0.5f);
            foreach (var axis in new[] { new Vector3(step.x, 0f, 0f), new Vector3(0f, 0f, step.z) })
            {
                var p = c.Position + axis;
                if (Inside(c.Grid, level, p, radius)) c.Position = p;
            }
            return (c.Position - before).magnitude / dt;
        }

        /// <summary>`p` (with `radius` round it) is on passable cells: its own cell, and any neighbour it pokes into.</summary>
        bool Inside(HoldGrid g, int level, Vector3 p, float radius)
        {
            foreach (var o in new[] { Vector3.zero, new Vector3(radius, 0, 0), new Vector3(-radius, 0, 0), new Vector3(0, 0, radius), new Vector3(0, 0, -radius) })
                if (!PassableLocal(g, level, LocalXZ(p + o, g))) return false;
            return true;
        }

        void Place(Creature c)
        {
            var hop = c.Brain.Hunting && !c.View.Modelled ? math.abs(math.sin(Time.time * 9f + c.Size.x * 13f)) * c.Size.y * 0.12f : 0f;
            c.View.transform.SetPositionAndRotation(c.Position + Vector3.up * hop, Quaternion.Euler(0f, c.Heading, 0f));
        }

        void Fade(Creature c)
        {
            var t = c.Brain.Clock;
            var gone = 0.4f;
            if (c.View.Modelled)
            {
                const float sink = 1.2f;
                gone = c.DeathFor + sink;
                c.View.transform.position = c.Position - Vector3.up * (math.saturate((t - c.DeathFor) / sink) * c.Size.y);
            }
            else c.View.transform.localScale = c.Size * math.max(0.01f, 1f - t / 0.4f);
            if (t < gone) return;
            UnityEngine.Object.Destroy(c.View.gameObject);
            _creatures.Remove(c);
            _state.Alive.Value = AliveCount();
        }

        int AliveCount()
        {
            var n = 0;
            foreach (var x in _creatures) if (!x.Brain.Dead) n++;
            return n;
        }

        // ---- the fight (as CombatPresenter) ----------------------------------------------------------------------

        void Bite(Creature c)
        {
            c.Brain.Cooldown = math.max(0.3f, c.Type.Cooldown);
            c.View.Attack();
            var armor = PlayerArmor();
            var (d20, total, hit, crit, fumble) = Dice.Attack(c.Type.Attack, armor, ref _rng);
            var damage = hit ? Dice.Roll(c.Type.Damage, ref _rng, crit) : 0;
            _rolls.Publish(new CombatRoll(c.Type.Name, "You", d20, c.Type.Attack, total, armor, hit, crit, fumble, damage, c.Type.Damage, false, "bite",
                                          false, false, ""));
            Debug.Log($"[HoldLife] {c.Type.Name} bites: d20 {d20} + {c.Type.Attack} = {total} vs {armor}: {(hit ? $"hit, {damage}" : "miss")}");
            if (hit) TakeDamage(damage, c.Type.Name);
        }

        void TakeDamage(int damage, string source)
        {
            var toStamina = math.min(damage, _sheet.Stamina.Value);
            _sheet.Stamina.Value -= toStamina;
            var toHp = damage - toStamina;
            if (_health.Max.Value > 0) _health.Current.Value = math.max(0, _health.Current.Value - toHp);
            _damaged.Publish(new PlayerDamaged(damage, source));
            if (_health.Max.Value > 0 && _health.Current.Value <= 0) DownAsync(source, _cts.Token).Forget();
        }

        /// <summary>An equipped weapon with no model fires from the hip (as on the planet); a drawn gun fires itself.</summary>
        void Shoot()
        {
            var locked = PointerLock.Locked;
            _lockSeen |= locked;
            if (!_input.PointerPressed.Value || !(locked || !_lockSeen) || _firstPerson.Held != null || _firstPerson.Equipped != null || Time.time < _nextShot) return;
            var item = _pack.EquippedIn(EquipSlot.Weapon);
            if (item == null) return;
            var w = CombatWeapons.For(item.Id);
            _nextShot = Time.time + math.max(0.05f, w.Cooldown);
            var bolt = CombatWeapons.Bolt(w);
            var range = math.max(5f, bolt.Range);
            var eye = _player.Camera.transform;
            var from = eye.position + eye.right * 0.22f - eye.up * 0.18f + eye.forward * 0.4f;
            var target = eye.position + eye.forward * range;
            int count;
            while ((count = Physics.RaycastNonAlloc(eye.position, eye.forward, _aimHits, range, ~0, QueryTriggerInteraction.Ignore)) == _aimHits.Length)
                _aimHits = new RaycastHit[_aimHits.Length * 2];
            var best = float.MaxValue;
            RaycastHit aimed = default;
            for (var i = 0; i < count; i++)
            {
                var h = _aimHits[i];
                if (h.collider.transform.IsChildOf(_player.Body.transform)) continue;
                if (h.distance < best) { best = h.distance; target = h.point; aimed = h; }
            }
            var c = CreatureAt(aimed.collider);
            if (c != null && !ShootAt(c, item.Id, aimed))
            {
                var side = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), eye.forward) * eye.up;
                target = eye.position + (aimed.point + side * (aimed.collider.bounds.extents.magnitude + 0.35f) - eye.position).normalized * range;
            }
            if (!_firedOnce) { _firedOnce = true; Debug.Log($"[HoldLife] firing {w.Name} ({item.Id}) from the hip"); }
            _bolts.Fire(from, target, bolt, item.Id);
        }

        Creature CreatureAt(Collider col)
        {
            if (col == null) return null;
            var view = col.GetComponentInParent<MonsterView>();
            if (view == null) return null;
            foreach (var c in _creatures) if (c.View == view && !c.Brain.Dead) return c;
            return null;
        }

        /// <summary>FirstPersonPresenter.ShotCheck: a drawn gun's shot at a creature rolls as it's fired.</summary>
        bool HeldShot(RaycastHit aimed)
        {
            var c = CreatureAt(aimed.collider);
            var held = _firstPerson.Held;
            var source = held != null && held == _firstPerson.Equipped
                ? _pack.EquippedIn(EquipSlot.Weapon)?.Id ?? "default"
                : CombatWeapons.Held(held != null ? held.GetComponent<WeaponView>() : null).Source;
            return c == null || _down || ShootAt(c, source, aimed);
        }

        bool ShootAt(Creature c, string source, RaycastHit aimed)
        {
            if (_down) return false;
            if (_bolts == null) Arm();
            var w = CombatWeapons.For(source);
            var parts = new[] { ((w.Ability ?? "").ToUpperInvariant(), Mod(w.Ability)),
                                ("level", (int)math.floor(C.AttackPerLevel * math.max(1, _sheet.Level.Value))) };
            MonsterBrain.Fire(ref c.Brain, MonsterCue.ShotAt);
            var s = _marksman.Roll(parts, math.max(1, c.Type.Armor), w.Damage, aimed, _player.Camera.transform, _player.Body, ref _rng);
            _rolls.Publish(new CombatRoll("You", c.Type.Name, s.D20, s.Bonus, s.Total, s.Armor, s.Hit, s.Crit, s.Fumble, s.Damage, w.Damage, true,
                                          w.Name ?? source, s.Graze, s.Strong, s.Mods));
            Debug.Log($"[HoldLife] you shoot at {c.Type.Name} ({w.Name}): d20 {s.D20} + {s.Bonus} = {s.Total} vs {s.Armor}: {(s.Hit ? s.Damage.ToString() : "miss")}");
            if (s.Hit) c.Incoming.Enqueue(s.Damage);
            return s.Hit;
        }

        /// <summary>A bolt (or blade) reached it: your shot's rolled damage lands; a blade rolls on contact.</summary>
        void Hurt(Creature c, string source)
        {
            if (c.Brain.Dead || _down) return;
            if (source != "melee")
            {
                if (c.Incoming.Count > 0) Damage(c, c.Incoming.Dequeue());
                return;
            }
            var w = CombatWeapons.For(source);
            var bonus = Mod(w.Ability) + (int)math.floor(C.AttackPerLevel * math.max(1, _sheet.Level.Value));
            MonsterBrain.Fire(ref c.Brain, MonsterCue.ShotAt);
            var armor = math.max(1, c.Type.Armor);
            var (d20, total, hit, crit, fumble) = Dice.Attack(bonus, armor, ref _rng);
            var damage = hit ? Dice.Roll(w.Damage, ref _rng, crit) : 0;
            _rolls.Publish(new CombatRoll("You", c.Type.Name, d20, bonus, total, armor, hit, crit, fumble, damage, w.Damage, true, w.Name ?? source,
                                          false, false, ""));
            if (hit) Damage(c, damage);
        }

        void Damage(Creature c, int damage)
        {
            if (c.Brain.Dead) return;
            c.View.Flash();
            c.Hp -= damage;
            _state.Target.Value = c.Hp > 0 ? $"{c.Type.Name}|{c.Hp}|{math.max(1, c.Type.Hp)}" : "";
            if (c.Hp > 0) { c.View.Staggered(); return; }
            MonsterBrain.Fire(ref c.Brain, MonsterCue.Killed);
            c.DeathFor = c.View.Die() + 1.5f;
            var col = c.View.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            _state.Killed.Value++;
            _state.Alive.Value = AliveCount();
            if (c.Type.Xp > 0) _xp.Publish(new CharacterXpGained(c.Type.Xp, c.Type.Name));
            Debug.Log($"[HoldLife] {c.Type.Name} killed ({_state.Killed.Value} so far)");
        }

        int Mod(string ability)
        {
            var abilities = GameContent.Character.Abilities;
            for (var i = 0; i < abilities.Count && i < _sheet.Mods.Count; i++)
                if (abilities[i].Id == ability) return _sheet.Mods[i];
            return 0;
        }

        int PlayerArmor() => C == null ? 10 : C.PlayerArmor + Mod(C.DefenseAbility);

        /// <summary>Down: as on the planet, the rescue drone takes you to the station; your relics stay in the deep.</summary>
        async UniTaskVoid DownAsync(string cause, CancellationToken ct)
        {
            if (_down) return;
            _down = true;
            _state.Downed.Value = true;
            _firstPerson.Lock(ControlLock.Downed);
            _died.Publish(new PlayerDied(cause));
            Debug.Log($"[HoldLife] down ({cause}): rescue drone");
            var r = C.Rescue;
            DialogueLine.Play(new Dialogue { Speaker = "Suit", Text = r?.DownedText ?? "You're down.", DurationSeconds = 3f, CharsPerSecond = 40f },
                              _ui.Dialogue, ct).Forget();
            try
            {
                for (var t = 0f; t < 2.5f; t += Time.deltaTime) { _downFade = t / 2.5f; await UniTask.Yield(ct); }
            }
            catch (OperationCanceledException) { return; }
            _downFade = 1f;
            var lost = _pack.TakeRelics();
            var fee = math.min(math.max(0, _voyage.Credits.Value), r?.Fee ?? 0);
            _voyage.Credits.Value -= fee;
            _health.Current.Value = _health.Max.Value;
            _sheet.Stamina.Value = _sheet.StaminaMax.Value;
            _voyage.Rescued.Value = true;
            _voyage.Arrival.Value = StationArrival.Planet;
            Debug.Log($"[HoldLife] rescued: lost {lost.Count} relic(s), paid {fee} cr");
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        public void Dispose()
        {
            _warpSub?.Dispose();
            _world.RegionShown -= Shown;
            _world.RegionHidden -= Hidden;
            _cts.Cancel();
            _cts.Dispose();
            if (_firstPerson.ShotCheck == HeldShot) _firstPerson.ShotCheck = null;
            _bolts?.Dispose();
            foreach (var c in _creatures) if (c.View != null) UnityEngine.Object.Destroy(c.View.gameObject);
        }
    }

    /// <summary>Breadth-first paths over one level of a grid's passable cells (HoldLifePlan.Passable), 4-connected.</summary>
    public static class CellPaths
    {
        static readonly int2[] Four = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        static int[] _prev = Array.Empty<int>();
        static readonly Queue<int2> Queue = new();

        /// <summary>Cells from `from` to `to` inclusive into `path` (empty: none within `maxVisit` cells).</summary>
        public static bool Find(in HoldGrid g, int level, int2 from, int2 to, int maxVisit, List<int2> path)
        {
            path.Clear();
            int w = g.Size.x, d = g.Size.z;
            bool In(int2 c) => c.x >= 0 && c.y >= 0 && c.x < w && c.y < d;
            if (!In(from) || !In(to)) return false;
            if (_prev.Length < w * d) _prev = new int[w * d];
            for (var i = 0; i < w * d; i++) _prev[i] = -1;
            Queue.Clear();
            var start = from.x + w * from.y;
            _prev[start] = start;
            Queue.Enqueue(from);
            var visited = 0;
            var found = false;
            while (Queue.Count > 0 && visited++ < maxVisit)
            {
                var c = Queue.Dequeue();
                if (c.Equals(to)) { found = true; break; }
                foreach (var step in Four)
                {
                    var n = c + step;
                    if (!In(n)) continue;
                    var ni = n.x + w * n.y;
                    if (_prev[ni] >= 0 || !HoldLifePlan.Passable(g.Get(new int3(n.x, level, n.y)))) continue;
                    _prev[ni] = c.x + w * c.y;
                    Queue.Enqueue(n);
                }
            }
            if (!found) return false;
            for (var i = to.x + w * to.y; ; i = _prev[i])
            {
                path.Add(new int2(i % w, i / w));
                if (i == start) break;
            }
            path.Reverse();
            return true;
        }
    }
}
