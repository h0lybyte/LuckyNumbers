using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Monsters on a job's site and the fights with them (content/combat.txtpb). Once you're on foot, a few placeholder
    /// blocks (count + count_per_danger x the job's danger; a Clear job's goal at least) wait round the wreck; they
    /// notice you within `sight`, chase you and bite from `reach`.
    ///
    /// Every hit is a d20: you roll d20 + your weapon ability's modifier + level bonus against a monster's armour,
    /// it rolls d20 + its attack against yours (player_armor + defense_ability). A natural 20 hits and doubles the dice;
    /// a natural 1 misses. Rolls come from your site's seed: your lucky number decides how the fight goes. Each roll
    /// goes to the page (CombatRoll). Your attacks: click with an equipped weapon and empty hands fires bolts toward the
    /// crosshair; the ship's lasers ("ship") and a held blade ("melee") hit through IDamageable too.
    ///
    /// Damage takes Stamina, then HP. At 0 HP you're down: a rescue drone takes you back to the station, without the
    /// relics you carried and `rescue.fee` credits lighter (PlayerVoyage.rescued; the job isn't done). Kills give XP
    /// and count for a Clear job.
    /// </summary>
    [Presenter]
    public sealed partial class CombatPresenter : IStartable, ITickable, IDisposable
    {
        sealed class Monster
        {
            public MonsterType Type;
            public MonsterView View;
            public Vector3 Position, Home;
            public float Heading, Cooldown, Dying = -1f, Wander;
            public int Hp;
            public bool Aggro;
            // Encounters (CombatPresenter.Encounters): a standoff waiting for you; let you pass; running off; calm
            // until; your first shot gets the surprise bonus.
            public bool Standoff, Ignored, Surprise;
            public float Fleeing = -1f, CalmUntil;
        }

        readonly ICrashSite _site;
        readonly PlanetView _view;
        readonly PlayerView _player;
        readonly GroundView _ground;
        readonly IFirstPerson _firstPerson;
        readonly PlayerInput _input;
        readonly PlayerHealth _health;
        readonly CharacterSheet _sheet;
        readonly IInventory _pack;
        readonly PlayerJob _job;
        readonly PlayerVoyage _voyage;
        readonly CombatState _state;
        readonly IPublisher<CombatRoll> _rolls;
        readonly IPublisher<PlayerDamaged> _damaged;
        readonly IPublisher<PlayerDied> _died;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly List<Monster> _monsters = new();
        readonly List<IDisposable> _subscriptions = new();
        readonly CancellationTokenSource _cts = new();
        Bolts _bolts;
        Material _bodyMaterial;   // monsters: the relics' lit material (it's in the build), tinted per monster
        float _onFootFor;         // a moment's grace after you step out before anything comes for you
        bool _firedOnce;
        Monster _devAim;          // dev "monster": your view follows it a moment, so a test can just hold fire
        float _devAimUntil;
        Random _rng;
        bool _spawned, _down, _lockSeen;
        float _nextShot;

        static CombatConfig C => GameContent.Combat;

        [Inject]
        public CombatPresenter(ICrashSite site, PlanetView view, PlayerView player, GroundView ground, IFirstPerson firstPerson,
                               PlayerInput input, PlayerHealth health, CharacterSheet sheet, IInventory pack, PlayerJob job,
                               PlayerVoyage voyage, CombatState state, IPublisher<CombatRoll> rolls,
                               IPublisher<PlayerDamaged> damaged, IPublisher<PlayerDied> died, IPublisher<CharacterXpGained> xp,
                               IPublisher<SceneChangeRequested> scenes, ISubscriber<DevWarpRequested> warps,
                               EncounterState encounter, ISubscriber<EncounterChoice> choices)
        {
            _encounter = encounter;
            _choices = choices;
            _site = site;
            _view = view;
            _player = player;
            _ground = ground;
            _firstPerson = firstPerson;
            _input = input;
            _health = health;
            _sheet = sheet;
            _pack = pack;
            _job = job;
            _voyage = voyage;
            _state = state;
            _rolls = rolls;
            _damaged = damaged;
            _died = died;
            _xp = xp;
            _scenes = scenes;
            _warps = warps;
        }

        public void Start()
        {
            _state.Alive.Value = 0;
            _state.Killed.Value = 0;
            _state.Target.Value = "";
            _state.Downed.Value = false;
            _state.Armor.Value = PlayerArmor();
            StartEncounters();
            // Dev (the page's inspector): "monster" drops one a few metres ahead of you.
            _subscriptions.Add(_warps.Subscribe(e =>
            {
                if (e.To == "standoff" || e.To == "find") { DevEncounter(e.To); return; }
                if (e.To != "monster" || C == null || C.Monsters.Count == 0) return;
                // 10 m ahead of you, and your view turned onto it (so a test can just hold fire).
                var eye = _player.Camera.transform;
                var flat = Vector3.ProjectOnPlane(_player.Body.transform.forward, Vector3.up).normalized;
                EnsureBolts();
                Spawn(Pick(5), _player.Body.transform.position + flat * 10f, false);
                var m = _monsters[^1];
                _devAim = m;
                _devAimUntil = Time.time + 3f;
                Debug.Log($"[Combat] dev: {m.Type.Name} {Vector3.Distance(m.Position, eye.position):0.0} m ahead (your view follows it for 3 s)");
            }));
        }

        public void Tick()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f || C == null) return;
            if (!_spawned && _site.Built && _firstPerson.Active) SpawnSite();
            _bolts?.Tick(dt);
            if (_down) return;
            if (TickEncounters()) return;   // a card is open: time stands still
            var me = _player.Body.transform.position;
            var onFoot = _firstPerson.Active;
            _onFootFor = onFoot ? _onFootFor + dt : 0f;
            if (_devAim != null && Time.time < _devAimUntil && _devAim.Dying < 0f)
            {
                var to = _devAim.Position - _player.Camera.transform.position;
                _firstPerson.Look(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, -Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg);
            }
            if (onFoot) Shoot();
            for (var i = _monsters.Count - 1; i >= 0; i--) Think(_monsters[i], me, onFoot, dt);
        }

        // ---- Spawning -------------------------------------------------------------------------------------------

        void SpawnSite()
        {
            _spawned = true;
            EnsureBolts();
            var danger = _job.Active.Value ? math.max(1, _job.Danger.Value) : 1;
            var count = C.Count + C.CountPerDanger * danger;
            if (_job.Active.Value && _job.Kind.Value == JobKind.Clear) count = math.max(count, _job.Goal.Value);
            var wreck = _view.Wreck != null ? _view.Wreck.Pivot.position : Vector3.zero;
            for (var i = 0; i < count; i++)
            {
                var angle = _rng.NextFloat(360f);
                var at = wreck + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * _rng.NextFloat(C.SpawnMin, math.max(C.SpawnMin + 1f, C.SpawnMax));
                Spawn(Pick(danger), at, false);
            }
            Debug.Log($"[Combat] {count} monster(s) round the wreck (danger {danger}); your armour {PlayerArmor()}");
            SpawnEncounters(danger, wreck);
        }

        void EnsureBolts()
        {
            if (_bolts != null) return;
            _rng = new Random((_site.Seed ^ 0xC0BA7u) | 1u);   // your lucky number rolls every fight
            var beam = _view.WreckBeacon != null ? _view.WreckBeacon.GetComponent<Renderer>() : null;
            _bolts = new Bolts(beam != null ? new Material(beam.sharedMaterial) : null, _player.Body.transform);
            var relic = _view.VaseTemplate != null ? _view.VaseTemplate.GetComponentInChildren<Renderer>(true) : null;
            _bodyMaterial = relic != null ? relic.sharedMaterial : beam != null ? beam.sharedMaterial : null;
        }

        /// <summary>A monster that can turn up at this danger, by weight.</summary>
        MonsterType Pick(int danger)
        {
            var total = 0f;
            foreach (var m in C.Monsters) if (m.DangerMin <= danger) total += math.max(0.01f, m.Weight);
            var roll = _rng.NextFloat(total);
            foreach (var m in C.Monsters)
            {
                if (m.DangerMin > danger) continue;
                roll -= math.max(0.01f, m.Weight);
                if (roll <= 0f) return m;
            }
            return C.Monsters[0];
        }

        void Spawn(MonsterType type, Vector3 at, bool aggro)
        {
            var size = new Vector3(type.Size.X > 0f ? type.Size.X : 1f, type.Size.Y > 0f ? type.Size.Y : 1f, type.Size.Z > 0f ? type.Size.Z : 1f);
            var view = MonsterView.Create(type.Id, size, new Color(type.Color.R, type.Color.G, type.Color.B, 1f), _bodyMaterial);
            var m = new Monster { Type = type, View = view, Position = at, Home = at, Hp = math.max(1, type.Hp), Aggro = aggro,
                                  Heading = _rng.NextFloat(360f), Wander = _rng.NextFloat(3f) };
            view.Damaged += (v, source, point) => Hurt(m, source);
            Place(m, 0f);
            _monsters.Add(m);
            _state.Alive.Value = _monsters.Count;
        }

        // ---- Monsters -------------------------------------------------------------------------------------------

        void Think(Monster m, Vector3 me, bool onFoot, float dt)
        {
            if (m.Dying >= 0f)
            {
                m.Dying += dt;
                m.View.transform.localScale = Size(m) * math.max(0.01f, 1f - m.Dying / 0.4f);
                if (m.Dying >= 0.4f)
                {
                    UnityEngine.Object.Destroy(m.View.gameObject);
                    _monsters.Remove(m);
                    _state.Alive.Value = _monsters.FindAll(x => x.Dying < 0f).Count;
                }
                return;
            }
            m.Cooldown -= dt;
            var to = new Vector3(me.x - m.Position.x, 0f, me.z - m.Position.z);
            var distance = to.magnitude;
            if (m.Fleeing >= 0f)
            {
                // Scared off: runs from you and is gone.
                m.Fleeing += dt;
                m.Heading = Mathf.MoveTowardsAngle(m.Heading, Mathf.Atan2(-to.x, -to.z) * Mathf.Rad2Deg, 540f * dt);
                m.Position += Quaternion.Euler(0f, m.Heading, 0f) * Vector3.forward * (m.Type.Speed * 1.6f * dt);
                Place(m, dt);
                if (m.Fleeing > 3.5f) m.Dying = 0f;
                return;
            }
            if (m.Standoff && !m.Aggro && onFoot && _onFootFor > 1f && distance < StandoffDistance) { OpenStandoff(m); return; }
            var calm = m.Ignored || m.Standoff || Time.time < m.CalmUntil;
            if (onFoot && _onFootFor > 4f && distance < m.Type.Sight && !calm) m.Aggro = true;
            if (m.Aggro && Time.time < m.CalmUntil) m.Aggro = false;
            var reach = math.max(0.8f, m.Type.Reach) + Size(m).z * 0.5f;
            if (m.Aggro && onFoot)
            {
                m.Heading = Mathf.MoveTowardsAngle(m.Heading, Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, 360f * dt);
                if (distance > reach * 0.85f)
                    m.Position += Quaternion.Euler(0f, m.Heading, 0f) * Vector3.forward * (m.Type.Speed * dt);
                else if (m.Cooldown <= 0f) Bite(m);
                if (distance > m.Type.Sight * 3f) m.Aggro = false;   // lost you
            }
            else
            {
                // Idle: amble round where it started.
                m.Wander -= dt;
                if (m.Wander <= 0f) { m.Wander = _rng.NextFloat(2f, 5f); m.Heading += _rng.NextFloat(-120f, 120f); }
                var home = m.Home - m.Position;
                if (home.magnitude > 6f) m.Heading = Mathf.MoveTowardsAngle(m.Heading, Mathf.Atan2(home.x, home.z) * Mathf.Rad2Deg, 180f * dt);
                m.Position += Quaternion.Euler(0f, m.Heading, 0f) * Vector3.forward * (m.Type.Speed * 0.25f * dt);
            }
            Place(m, dt);
        }

        static Vector3 Size(Monster m) => new(m.Type.Size.X > 0f ? m.Type.Size.X : 1f, m.Type.Size.Y > 0f ? m.Type.Size.Y : 1f, m.Type.Size.Z > 0f ? m.Type.Size.Z : 1f);

        void Place(Monster m, float dt)
        {
            var size = Size(m);
            m.Position.y = _ground.SurfaceAt(m.Position) + size.y * 0.5f;
            var hop = m.Aggro ? math.abs(math.sin(Time.time * 9f + m.Home.x)) * size.y * 0.12f : 0f;   // scuttles when it's after you
            m.View.transform.SetPositionAndRotation(m.Position + Vector3.up * hop, Quaternion.Euler(0f, m.Heading, 0f));
        }

        /// <summary>A monster's attack: d20 + its attack against your armour; damage takes Stamina, then HP.</summary>
        void Bite(Monster m)
        {
            m.Cooldown = math.max(0.3f, m.Type.Cooldown);
            var armor = PlayerArmor();
            var (d20, total, hit, crit, fumble) = Dice.Attack(m.Type.Attack, armor, ref _rng);
            var damage = hit ? Dice.Roll(m.Type.Damage, ref _rng, crit) : 0;
            _rolls.Publish(new CombatRoll(m.Type.Name, "You", d20, m.Type.Attack, total, armor, hit, crit, fumble, damage, m.Type.Damage, false, "bite"));
            Debug.Log($"[Combat] {m.Type.Name} bites: d20 {d20} + {m.Type.Attack} = {total} vs {armor}: {(hit ? $"hit, {damage}" : "miss")}");
            if (hit) TakeDamage(damage, m.Type.Name);
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

        // ---- Your attacks ---------------------------------------------------------------------------------------

        /// <summary>Hold the button with a weapon equipped and nothing in your hands: bolts toward the crosshair.</summary>
        void Shoot()
        {
            var locked = Cursor.lockState == CursorLockMode.Locked;
            _lockSeen |= locked;
            if (!_input.PointerPressed.Value || !(locked || !_lockSeen) || _firstPerson.Held != null || Time.time < _nextShot) return;
            var item = _pack.EquippedIn(EquipSlot.Weapon);
            if (item == null) return;
            var w = Weapon(item.Id);
            _nextShot = Time.time + math.max(0.05f, w.Cooldown);
            var eye = _player.Camera.transform;
            var from = eye.position + eye.right * 0.22f - eye.up * 0.18f + eye.forward * 0.4f;   // from the hip, right hand
            var target = eye.position + eye.forward * math.max(5f, w.Range);
            var hits = Physics.RaycastAll(eye.position, eye.forward, math.max(5f, w.Range), ~0, QueryTriggerInteraction.Ignore);
            var best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(_player.Body.transform)) continue;
                if (h.distance < best) { best = h.distance; target = h.point; }
            }
            if (!_firedOnce) { _firedOnce = true; Debug.Log($"[Combat] firing {w.Name} ({item.Id}): d20 + {Mod(w.Ability)} {w.Ability}, {w.Damage}"); }
            var c = w.Color;
            _bolts.Fire(from, target, w.Speed, math.max(5f, w.Range), math.max(0.2f, w.Length), math.max(0.02f, w.Width),
                        c.R + c.G + c.B > 0f ? new Color(c.R, c.G, c.B) : new Color(0.4f, 1.6f, 2.6f), item.Id);
        }

        /// <summary>A hit on a monster: d20 + your bonus against its armour, then the weapon's damage dice.</summary>
        void Hurt(Monster m, string source)
        {
            if (m.Dying >= 0f || _down) return;
            var w = Weapon(source);
            var bonus = Mod(w.Ability) + (int)math.floor(C.AttackPerLevel * math.max(1, _sheet.Level.Value));
            if (m.Surprise) { bonus += SurpriseBonus; m.Surprise = false; }   // you chose the fight: the first shot's yours
            m.Ignored = m.Standoff = false;                                     // shot at, it knows you're there
            m.CalmUntil = 0f;
            var armor = math.max(1, m.Type.Armor);
            var (d20, total, hit, crit, fumble) = Dice.Attack(bonus, armor, ref _rng);
            var damage = hit ? Dice.Roll(w.Damage, ref _rng, crit) : 0;
            _rolls.Publish(new CombatRoll("You", m.Type.Name, d20, bonus, total, armor, hit, crit, fumble, damage, w.Damage, true, w.Name ?? source));
            Debug.Log($"[Combat] you {(source == "ship" ? "strafe" : "shoot at")} {m.Type.Name} ({w.Name}): d20 {d20} + {bonus} = {total} vs {armor}: " +
                      $"{(hit ? $"{(crit ? "CRIT " : "")}{damage}" : "miss")}");
            m.Aggro = true;
            if (!hit) return;
            m.View.Flash();
            m.Hp -= damage;
            _state.Target.Value = m.Hp > 0 ? $"{m.Type.Name}|{m.Hp}|{math.max(1, m.Type.Hp)}" : "";
            if (m.Hp <= 0) Kill(m);
        }

        void Kill(Monster m)
        {
            m.Dying = 0f;
            var col = m.View.GetComponent<Collider>();
            if (col != null) col.enabled = false;
            _state.Killed.Value++;
            _state.Alive.Value = _monsters.FindAll(x => x.Dying < 0f).Count;
            if (m.Type.Xp > 0) _xp.Publish(new CharacterXpGained(m.Type.Xp, m.Type.Name));
            if (_job.Active.Value && _job.Kind.Value == JobKind.Clear)
            {
                _job.Count.Value++;
                if (!_job.Done.Value && _job.Count.Value >= _job.Goal.Value)
                {
                    _job.Done.Value = true;
                    Debug.Log($"[Combat] job goal met: {_job.Title.Value}");
                }
            }
            Debug.Log($"[Combat] {m.Type.Name} killed ({_state.Killed.Value} so far, {_state.Alive.Value} left)");
        }

        CombatWeapon Weapon(string source)
        {
            CombatWeapon fallback = null;
            foreach (var w in C.Weapons)
            {
                if (w.Source == source) return w;
                if (w.Source == "default") fallback = w;
            }
            return fallback ?? new CombatWeapon { Source = source, Name = source, Ability = "dex", Damage = "1d6", Cooldown = 0.5f, Speed = 80f, Range = 60f };
        }

        int Mod(string ability)
        {
            var abilities = GameContent.Character.Abilities;
            for (var i = 0; i < abilities.Count && i < _sheet.Mods.Count; i++)
                if (abilities[i].Id == ability) return _sheet.Mods[i];
            return 0;
        }

        int PlayerArmor() => C == null ? 10 : C.PlayerArmor + Mod(C.DefenseAbility);

        // ---- Down: the rescue -----------------------------------------------------------------------------------

        async UniTaskVoid DownAsync(string cause, CancellationToken ct)
        {
            if (_down) return;
            _down = true;
            _state.Downed.Value = true;
            _firstPerson.Active = false;
            _died.Publish(new PlayerDied(cause));
            Debug.Log($"[Combat] down ({cause}): rescue drone");
            var r = C.Rescue;
            DialogueLine.Play(new Dialogue { Speaker = "Suit", Text = r?.DownedText ?? "You're down.", DurationSeconds = 3f, CharsPerSecond = 40f },
                              _view.Dialogue, ct).Forget();
            for (var t = 0f; t < 2.5f; t += Time.deltaTime) { _view.Fade(t / 2.5f); await UniTask.Yield(ct); }
            _view.Fade(1f);
            // The haul stays on the planet; the drone costs you; you're patched up.
            var lost = _pack.TakeRelics();
            var fee = math.min(math.max(0, _voyage.Credits.Value), r?.Fee ?? 0);
            _voyage.Credits.Value -= fee;
            _job.Done.Value = false;
            _health.Current.Value = _health.Max.Value;
            _sheet.Stamina.Value = _sheet.StaminaMax.Value;
            _voyage.Rescued.Value = true;
            _voyage.Returning.Value = true;
            Debug.Log($"[Combat] rescued: lost {lost.Count} relic(s), paid {fee} cr");
            _scenes.Publish(new SceneChangeRequested(GameScene.Station));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
            _bolts?.Dispose();
            foreach (var m in _monsters) if (m.View != null) UnityEngine.Object.Destroy(m.View.gameObject);
        }
    }
}
