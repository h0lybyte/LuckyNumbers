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

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Dwarven strongboxes in the mines (HoldLife, placed by HoldLifePlan as each region is shown, gone with it). A
    /// cache is a named placeholder block with a lantern-coloured glint; the nearest shut one within reach also gets
    /// the one glint light. E opens it: its find goes into the pack (IInventory.TryAdd, as the crash site's relics
    /// do), so a Kind RELIC find sells to the museum back at the station. A unique find you already carry falls back
    /// to a lesser tier; no room in the pack and it stays shut. Opened caches stay open for this visit (by world cell).
    /// </summary>
    [Presenter]
    public sealed class RelicCaches : IStartable, ITickable, IDisposable
    {
        sealed class Cache
        {
            public CachePlan Plan;
            public Transform Root, Lid, Glint;
            public Vector3 At;
            public bool Open;
        }

        static readonly Color Iron = new(0.24f, 0.2f, 0.17f), Band = new(0.55f, 0.42f, 0.2f);
        static readonly Vector3 BoxSize = new(0.8f, 0.45f, 0.5f);

        readonly HoldWorld _world;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly IInventory _pack;
        readonly IPublisher<CharacterXpGained> _xp;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly Dictionary<int3, List<Cache>> _byRegion = new();
        readonly HashSet<int3> _opened = new();   // world cells: this visit's opened caches
        readonly List<CachePlan> _plans = new();
        readonly List<CreaturePlan> _unused = new();
        readonly CancellationTokenSource _cts = new();
        IDisposable _sub;
        Light _light;
        bool _press, _claimedE;
        Color _lantern;

        static HoldLife Life => GameContent.Hold.Life;

        [Inject]
        public RelicCaches(HoldWorld world, PlayerView player, FirstPersonPresenter firstPerson, PlanetView ui, IInventory pack,
                           IPublisher<CharacterXpGained> xp, ISubscriber<InputActionPerformed> actions)
        {
            _world = world;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _pack = pack;
            _xp = xp;
            _actions = actions;
            var c = world.Config;
            _lantern = new Color(c.LanternR, c.LanternG, c.LanternB);
            // Now, not in Start: HoldPresenter's Start may show the first region before ours runs.
            _world.RegionShown += Shown;
            _world.RegionHidden += Hidden;
        }

        public void Start()
        {
            _sub = _actions.Subscribe(e => { if (e.Action == InputAction.Interact) _press = true; });
            _light = new GameObject("Cache glint light").AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = _lantern;
            _light.range = 3.5f;
            _light.intensity = 1.6f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        void Shown(int3 region, HoldGrid grid)
        {
            if (Life == null) return;
            HoldLifePlan.Plan(_world.Seed, region, grid, Life, _plans, _unused);
            if (_plans.Count == 0) return;
            var list = new List<Cache>();
            foreach (var plan in _plans) list.Add(Build(plan));
            _byRegion[region] = list;
        }

        void Hidden(int3 region)
        {
            if (!_byRegion.Remove(region, out var list)) return;
            foreach (var c in list) if (c.Root != null) UnityEngine.Object.Destroy(c.Root.gameObject);
        }

        /// <summary>The box against its wall, facing into the room: body, banded lid on a back hinge, and a glint above.</summary>
        Cache Build(CachePlan plan)
        {
            var wall = new Vector3(plan.Wall.x, 0f, plan.Wall.y);
            var half = _world.CellSize.x * 0.5f;
            var at = (Vector3)_world.CellFloor(plan.Cell) + wall * (half - BoxSize.z * 0.5f - 0.08f);
            var root = new GameObject($"Cache {plan.Cell.x},{plan.Cell.y},{plan.Cell.z}").transform;
            root.SetPositionAndRotation(at, Quaternion.LookRotation(-wall, Vector3.up));   // front faces away from the wall

            var body = Placeholders.Block("PLACEHOLDER_dwarven_strongbox", new Vector3(BoxSize.x, BoxSize.y * 0.75f, BoxSize.z), Iron).transform;
            body.SetParent(root, false);
            body.localPosition = new Vector3(0f, BoxSize.y * 0.375f, 0f);
            var hinge = new GameObject("Lid hinge").transform;
            hinge.SetParent(root, false);
            hinge.localPosition = new Vector3(0f, BoxSize.y * 0.75f, -BoxSize.z * 0.5f);
            var lid = Placeholders.Block("PLACEHOLDER_dwarven_strongbox_lid", new Vector3(BoxSize.x + 0.04f, BoxSize.y * 0.25f, BoxSize.z + 0.04f), Band, false).transform;
            lid.SetParent(hinge, false);
            lid.localPosition = new Vector3(0f, BoxSize.y * 0.125f, BoxSize.z * 0.5f);
            var glint = Placeholders.Block("PLACEHOLDER_strongbox_glint", Vector3.one * 0.09f, _lantern * 2.2f, false).transform;
            glint.SetParent(root, false);
            glint.localPosition = new Vector3(0.18f, BoxSize.y + 0.22f, 0.05f);

            BlobCaster.Attach(root.gameObject, new BlobSpec { Size = BoxSize }).SetGround(at.y);   // a soft blob on the floor under it
            var cache = new Cache { Plan = plan, Root = root, Lid = hinge, Glint = glint, At = at };
            if (_opened.Contains(plan.Cell)) SetOpen(cache);
            return cache;
        }

        static void SetOpen(Cache c)
        {
            c.Open = true;
            c.Lid.localRotation = Quaternion.Euler(-105f, 0f, 0f);
            c.Glint.gameObject.SetActive(false);
        }

        public void Tick()
        {
            var press = _press;
            _press = false;
            var me = _player.Body.transform.position;
            Cache near = null, glow = null;
            var reach = Life != null && Life.OpenDistance > 0f ? Life.OpenDistance : 1.8f;
            float best = reach, bestGlow = 9f;
            var t = Time.time;
            foreach (var list in _byRegion.Values)
            foreach (var c in list)
            {
                if (c.Open) continue;
                var s = 0.8f + 0.35f * Mathf.Sin(t * 3.1f + c.Plan.Cell.x * 1.7f + c.Plan.Cell.z);
                c.Glint.localScale = Vector3.one * (0.09f * s);
                c.Glint.localRotation = Quaternion.Euler(45f, t * 90f, 45f);
                if (math.abs(c.At.y - me.y) > 1.5f) continue;
                var d = Vector2.Distance(new Vector2(c.At.x, c.At.z), new Vector2(me.x, me.z));
                if (d < best) { best = d; near = c; }
                if (d < bestGlow) { bestGlow = d; glow = c; }
            }

            // One light, on the nearest shut box: the glint catches the stone round it.
            _light.enabled = glow != null;
            if (glow != null)
            {
                _light.transform.position = glow.Glint.position;
                _light.intensity = 1.2f + 0.5f * Mathf.Sin(t * 3.1f);
            }

            // E is ours while a box is in reach (the gun stays put); hand it back once we're not.
            if (near != null) { _firstPerson.InteractElsewhere = true; _claimedE = true; }
            else if (_claimedE) { _firstPerson.InteractElsewhere = false; _claimedE = false; }
            if (near == null || !_firstPerson.Active) return;
            _world.Prompt = (Life.OpenPrompt ?? "E  open the {name}").Replace("{name}", Life.CacheName ?? "strongbox");
            if (press) OpenCache(near);
        }

        void OpenCache(Cache c)
        {
            var def = Find(c.Plan, out var any);
            if (def == null)
            {
                SetOpen(c);
                _opened.Add(c.Plan.Cell);
                Say(Life.CacheName ?? "Strongbox", any ? "Nothing in it you don't already have." : Life.EmptyText ?? "Empty.");
                Debug.Log($"[HoldLife] cache {c.Plan.Cell} empty (tier {c.Plan.Tier})");
                return;
            }
            if (!_pack.TryAdd(def.Id))
            {
                Say("Pack", (GameContent.Items.FullPrompt ?? "No room for {name}").Replace("{name}", def.Name));
                return;   // stays shut: come back with room
            }
            SetOpen(c);
            _opened.Add(c.Plan.Cell);
            var tier = c.Plan.Tier >= 0 ? Life.Tiers[c.Plan.Tier].Name : "";
            Debug.Log($"[HoldLife] cache {c.Plan.Cell} ({c.Plan.Spot}, depth {HoldLifePlan.Depth(_world.RegionOfCell(c.Plan.Cell))}): {def.Name} ({tier}, {def.Value} cr)");
            var xp = GameContent.Character.XpPerRelic;
            if (xp > 0) _xp.Publish(new CharacterXpGained(xp, def.Name));
            Say($"{def.Name}{(string.IsNullOrEmpty(tier) ? "" : $" - {tier}")}",
                $"{def.Description} Worth {def.Value:N0} credits to the museum.");
        }

        /// <summary>
        /// The planned item, or (it's unique and you have it) the same pick in each lesser tier down to the first.
        /// `any`: the cache had something, you just carry it all already.
        /// </summary>
        ItemDef Find(CachePlan plan, out bool any)
        {
            any = false;
            for (var tier = plan.Tier; tier >= 0; tier--)
            {
                var items = Life.Tiers[tier].Items;
                if (items.Count == 0) continue;
                var id = items[(plan.Item < 0 ? 0 : plan.Item) % items.Count];
                var def = Item(id);
                if (def == null) { Debug.LogWarning($"[HoldLife] no item '{id}' (items.txtpb)"); continue; }
                any = true;
                if (def.Unique && _pack.Has(def.Id)) continue;
                return def;
            }
            return null;
        }

        static ItemDef Item(string id)
        {
            foreach (var d in GameContent.Items.Items) if (d.Id == id) return d;
            return null;
        }

        void Say(string speaker, string text) =>
            DialogueLine.Play(new Dialogue { Speaker = speaker, Text = text, DurationSeconds = 3.5f, CharsPerSecond = 50f }, _ui.Dialogue, _cts.Token).Forget();

        public void Dispose()
        {
            _world.RegionShown -= Shown;
            _world.RegionHidden -= Hidden;
            if (_claimedE) _firstPerson.InteractElsewhere = false;
            _sub?.Dispose();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
