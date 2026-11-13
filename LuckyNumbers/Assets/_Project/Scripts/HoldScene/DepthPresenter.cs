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
    /// The Depth pass in the scene (docs/holds.md, "Depth"; rules DepthRules): long falls hurt (FirstPersonPresenter
    /// .Landed: past safe_fall, through HoldHurt to DeepCreatures' Stamina-then-HP and its rescue), drop lips get a
    /// warning plate and a prompt, depth seals are iron hatches (collider) over every way down at a band's bottom that
    /// open (E) once you carry the band's seal key, keys lie on plinths in a few regions of each band, and the first
    /// time you enter a band its name shows. Keys held and seals opened last for this visit.
    /// E and the prompts go through InteractionPresenter (IInteractable): Tick finds what's nearest, Offer hands it over.
    /// Dev: LN.send('DevWarpRequested', {to: 'seal' | 'key' | 'drop' | 'collapse' | 'sealkey'}).
    /// </summary>
    [Presenter]
    public sealed class DepthPresenter : IInteractable, IStartable, ITickable, IDisposable
    {
        static readonly object Lip = new();   // the drop warning (said only)

        sealed class Seal
        {
            public int Band;
            public int3 Cell;            // world
            public Vector3 At;           // floor centre
            public Transform Hatch;
            public Transform[] Chains;
            public BoxCollider Collider;
            public float Lift = -1f;     // opening: seconds in, else -1
            public bool Open;
            public float ChainTop;
        }

        sealed class Key
        {
            public int Band;
            public int3 Cell;
            public Vector3 At;
            public Transform Root, Item, Glint;
        }

        sealed class Placed
        {
            public readonly List<Seal> Seals = new();
            public readonly List<Key> Keys = new();
            public readonly List<int3> Lips = new();    // world cells
            public readonly List<GameObject> Objects = new();
        }

        const float Lift = 1.3f, LiftSeconds = 1.6f;

        readonly HoldWorld _world;
        readonly HoldView _view;
        readonly HoldPresenter _hold;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly PlanetScene _walk;
        readonly IPublisher<HoldHurt> _hurt;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly MineParams _p;
        readonly Dictionary<int3, Placed> _byRegion = new();
        readonly HashSet<int> _keys = new();          // bands whose key you carry (this visit)
        readonly HashSet<int3> _opened = new();       // world cells of opened seals
        readonly HashSet<int3> _taken = new();        // world cells of taken keys
        readonly HashSet<int> _bandsSeen = new();
        readonly CancellationTokenSource _cts = new();
        IDisposable _warpSub;
        Mesh _hatch, _chain, _plate, _plinth;
        Light _glint;
        float _top = float.NaN;
        object _near;       // this frame's nearest: a Key, a Seal, Lip, or null
        float _nearDistance;

        static DepthRules R => GameContent.Hold.Depth ?? new DepthRules();

        [Inject]
        public DepthPresenter(HoldWorld world, HoldView view, HoldPresenter hold, PlayerView player, FirstPersonPresenter firstPerson,
                              PlanetView ui, PlanetScene walk, IPublisher<HoldHurt> hurt, ISubscriber<DevWarpRequested> warps)
        {
            _world = world;
            _view = view;
            _hold = hold;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _walk = walk;
            _hurt = hurt;
            _warps = warps;
            _p = MineParams.From(world.Config);
            // Now, not in Start: HoldPresenter's Start may show the first region before ours runs.
            _world.RegionShown += Shown;
            _world.RegionHidden += Hidden;
            _firstPerson.Landed += Landed;
        }

        public void Start()
        {
            _warpSub = _warps.Subscribe(e => Warp(e.To));
            _glint = new GameObject("Seal key glint light").AddComponent<Light>();
            _glint.type = LightType.Point;
            _glint.color = new Color(0.75f, 0.85f, 1f);
            _glint.range = 3.5f;
            _glint.intensity = 1.4f;
            _glint.shadows = LightShadows.None;
            _glint.enabled = false;
        }

        void EnsureMeshes()
        {
            if (_hatch != null) return;
            var c = _world.Config;
            var stone = "";
            foreach (var pal in c.Textures.Palettes) if (pal.Name == (c.Mines?.Palette ?? "mine")) stone = pal.Wall;
            var s = DepthKit.Slices.From(c.Textures, stone);
            _hatch = DepthKit.SealHatch(c.CellSize, s, 0.62f);
            _chain = DepthKit.Chain(c.CellSize, s, 0.55f);
            _plate = DepthKit.WarningPlate(c.CellSize, s, 1f);
            _plinth = DepthKit.KeyPlinth(c.CellSize, s, 0.55f);
        }

        // ---- regions ---------------------------------------------------------------------------------------------

        void Shown(int3 region, HoldGrid grid)
        {
            if (region.y > 0 || !grid.Fixtures.IsCreated || grid.Fixtures.Length == 0) return;
            Placed placed = null;
            foreach (var f in grid.Fixtures)
            {
                if (f.Kind != FixtureKind.Seal && f.Kind != FixtureKind.SealKey && f.Kind != FixtureKind.DropLip) continue;
                EnsureMeshes();
                placed ??= new Placed();
                var cell = grid.ToWorld(f.Cell);
                switch (f.Kind)
                {
                    case FixtureKind.Seal: placed.Seals.Add(BuildSeal(f.Data, cell, placed)); break;
                    case FixtureKind.SealKey: if (!_taken.Contains(cell)) placed.Keys.Add(BuildKey(grid, f, placed)); break;
                    default: placed.Lips.Add(cell); BuildPlate(grid, f.Cell, placed); break;
                }
            }
            if (placed != null) _byRegion[region] = placed;
        }

        void Hidden(int3 region)
        {
            if (!_byRegion.Remove(region, out var placed)) return;
            foreach (var go in placed.Objects) if (go != null) UnityEngine.Object.Destroy(go);
        }

        GameObject Piece(string name, Mesh mesh, Transform parent)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _view.Material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        Seal BuildSeal(int band, int3 cell, Placed placed)
        {
            var at = (Vector3)_world.CellFloor(cell);
            var root = new GameObject($"Seal {cell.x},{cell.y},{cell.z} (band {band})").transform;
            root.position = at;
            placed.Objects.Add(root.gameObject);
            var hatch = Piece("Hatch", _hatch, root).transform;
            var col = hatch.gameObject.AddComponent<BoxCollider>();
            // Taller than the hatch: nobody stands on it (from up there the ladder below would offer a climb).
            col.center = new Vector3(0f, 1.2f, 0f);
            col.size = new Vector3(DepthKit.HatchSize, 2.4f, DepthKit.HatchSize);
            var seal = new Seal { Band = band, Cell = cell, At = at, Hatch = hatch, Collider = col, ChainTop = _world.CellSize.y - 0.5f };
            seal.Chains = new Transform[2];
            for (var i = 0; i < 2; i++)
            {
                seal.Chains[i] = Piece("Chain", _chain, root).transform;
                seal.Chains[i].localPosition = new Vector3(i == 0 ? -0.08f : 0.08f, DepthKit.HatchHeight + 0.05f, 0f);
            }
            if (_opened.Contains(cell)) { seal.Open = true; SetLift(seal, 1f); }
            else SetLift(seal, 0f);
            return seal;
        }

        void SetLift(Seal s, float t)
        {
            var y = Lift * Mathf.SmoothStep(0f, 1f, t);
            s.Hatch.localPosition = new Vector3(0f, y, 0f);
            foreach (var chain in s.Chains)
            {
                var bottom = y + DepthKit.HatchHeight + 0.05f;
                chain.localPosition = new Vector3(chain.localPosition.x, bottom, 0f);
                chain.localScale = new Vector3(1f, math.max(0.02f, s.ChainTop - bottom), 1f);
            }
            s.Collider.enabled = t <= 0f;
        }

        /// <summary>The key on a stone plinth against one of its tunnel's rock sides, with a cold glint over it.</summary>
        Key BuildKey(HoldGrid grid, HoldFixture f, Placed placed)
        {
            var cell = grid.ToWorld(f.Cell);
            var wall = new int3(1, 0, 0);
            for (var i = 0; i < 4; i++)
            {
                var d = HoldRules.Around(i);
                if (!grid.Get(f.Cell + d).Walkable) { wall = d; break; }
            }
            var dir = new Vector3(wall.x, 0f, wall.z);
            var at = (Vector3)_world.CellFloor(cell) + dir * (_world.CellSize.x * 0.5f - 0.28f);
            var root = new GameObject($"Seal key {cell.x},{cell.y},{cell.z} (band {f.Data})").transform;
            root.SetPositionAndRotation(at, Quaternion.LookRotation(-dir, Vector3.up));
            placed.Objects.Add(root.gameObject);
            var plinth = Piece("Plinth", _plinth, root);
            var pc = plinth.AddComponent<BoxCollider>();
            pc.center = new Vector3(0f, 0.41f, 0f);
            pc.size = new Vector3(0.68f, 0.82f, 0.52f);
            var item = Placeholders.Block("PLACEHOLDER_seal_key", new Vector3(0.42f, 0.06f, 0.14f), new Color(0.62f, 0.5f, 0.24f), false).transform;
            item.SetParent(root, false);
            item.localPosition = new Vector3(0f, 0.85f, 0f);
            item.localRotation = Quaternion.Euler(0f, 18f, 0f);
            var glint = Placeholders.Block("PLACEHOLDER_seal_key_glint", Vector3.one * 0.08f, new Color(1.4f, 1.6f, 2f), false).transform;
            glint.SetParent(root, false);
            glint.localPosition = new Vector3(0f, 1.15f, 0f);
            return new Key { Band = f.Data, Cell = cell, At = at, Root = root, Item = item, Glint = glint };
        }

        /// <summary>A hazard plate hung on the lip's rail, facing the first side you can walk up from.</summary>
        void BuildPlate(HoldGrid grid, int3 local, Placed placed)
        {
            for (var i = 0; i < 4; i++)
            {
                var d = HoldRules.Around(i);
                if (!HoldRules.StepOff(grid.Get(local + d))) continue;
                var dir = new Vector3(d.x, 0f, d.z);
                var at = (Vector3)_world.CellFloor(grid.ToWorld(local)) + dir * (_world.CellSize.x * 0.5f + 0.2f) + Vector3.up * 0.78f;
                var go = Piece($"Drop warning {grid.ToWorld(local)}", _plate, null);
                // The plate's face is -z: it looks out at the walker (along +d).
                go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(-dir, Vector3.up) * Quaternion.Euler(0f, 0f, 4f));
                placed.Objects.Add(go);
                return;
            }
        }

        // ---- ticking ---------------------------------------------------------------------------------------------

        public void Tick()
        {
            _near = null;
            var body = _player.Body;
            var me = body.transform.position;
            // Fall height: the highest point since you last stood (or held a ladder).
            if (body.isGrounded || _firstPerson.Climbing || float.IsNaN(_top)) _top = me.y;
            else _top = math.max(_top, me.y);

            if (!_firstPerson.Active) return;
            Band(me);
            var t = Time.time;
            var reach = R.UseDistance > 0f ? R.UseDistance : 1.9f;
            Seal seal = null;
            Key key = null;
            float bestSeal = reach + DepthKit.HatchSize * 0.5f, bestKey = reach, bestGlint = 9f;
            Key glint = null;
            var nearLip = false;
            var here = _world.CellOf(me + Vector3.up * 0.2f);
            foreach (var placed in _byRegion.Values)
            {
                foreach (var s in placed.Seals)
                {
                    if (s.Lift >= 0f)
                    {
                        s.Lift += Time.deltaTime;
                        SetLift(s, s.Lift / LiftSeconds);
                        if (s.Lift >= LiftSeconds) s.Lift = -1f;
                    }
                    if (s.Open || math.abs(s.At.y - me.y) > 1.6f) continue;
                    var d = Flat(s.At, me);
                    if (d < bestSeal) { bestSeal = d; seal = s; }
                }
                foreach (var k in placed.Keys)
                {
                    var pulse = 0.8f + 0.35f * Mathf.Sin(t * 2.7f + k.Cell.x);
                    k.Glint.localScale = Vector3.one * (0.08f * pulse);
                    k.Glint.localRotation = Quaternion.Euler(45f, t * 80f, 45f);
                    if (math.abs(k.At.y - me.y) > 1.5f) continue;
                    var d = Flat(k.At, me);
                    if (d < bestKey) { bestKey = d; key = k; }
                    if (d < bestGlint) { bestGlint = d; glint = k; }
                }
                foreach (var lip in placed.Lips)
                    if (lip.y == here.y && math.cmax(math.abs(lip.xz - here.xz)) <= 1) nearLip = true;
            }
            _glint.enabled = glint != null;
            if (glint != null) _glint.transform.position = glint.Glint.position;

            if (key != null) { _near = key; _nearDistance = bestKey; }
            else if (seal != null) { _near = seal; _nearDistance = bestSeal; }
            else if (nearLip) { _near = Lip; _nearDistance = 0f; }
        }

        // ---- IInteractable: a key to take, a seal to open (with its band's key), else what's said ----------------

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = _near switch
            {
                Key => new Interaction(_near, InteractionPriority.Pickup, _nearDistance),
                Seal s when _keys.Contains(s.Band) => new Interaction(_near, InteractionPriority.Place, _nearDistance),
                Seal or { } => new Interaction(_near, InteractionPriority.Info, _nearDistance, usable: false),
                null => default,
            };
            return _near != null;
        }

        public string Prompt(object key) => key switch
        {
            Key k => Fill(R.KeyPrompt ?? "E  take the seal key of {name}", k.Band),
            Seal s when _keys.Contains(s.Band) => R.SealPrompt ?? "E  unseal the way down",
            Seal s => Fill(R.SealLocked ?? "Sealed. The key of {name} lies somewhere in these depths.", s.Band),
            _ => R.DropPrompt ?? "A long drop.",
        };

        public void Interact(object key)
        {
            if (key is Key k && !_taken.Contains(k.Cell)) TakeKey(k);
            else if (key is Seal s && !s.Open && _keys.Contains(s.Band)) OpenSeal(s);
        }

        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        void TakeKey(Key k)
        {
            _taken.Add(k.Cell);
            _keys.Add(k.Band);
            k.Item.gameObject.SetActive(false);
            k.Glint.gameObject.SetActive(false);
            foreach (var placed in _byRegion.Values) placed.Keys.Remove(k);
            Debug.Log($"[Depth] took band {k.Band}'s seal key at {k.Cell}");
            Say("Seal key", Fill(R.KeyText ?? "The seal key of {name}.", k.Band));
        }

        void OpenSeal(Seal s)
        {
            s.Open = true;
            s.Lift = 0f;
            _opened.Add(s.Cell);
            // Every seal of the band opens with its key; this one winches up now, the rest when you reach them.
            Debug.Log($"[Depth] unsealed {s.Cell} (band {s.Band})");
        }

        /// <summary>First time this visit in a band: its name ("Depth 4: the Old Works") and blurb.</summary>
        void Band(Vector3 me)
        {
            var region = _world.RegionOf(me);
            if (region.y > 0 || _p.SealEvery <= 0) return;
            var band = MinePasses.BandOf(MinePasses.DepthOf(region), _p);
            if (!_bandsSeen.Add(band)) return;
            var b = BandDef(band);
            var title = (R.BandTitle ?? "Depth {depth}: {name}").Replace("{depth}", MinePasses.BandTop(band, _p).ToString()).Replace("{name}", BandName(band));
            Debug.Log($"[Depth] entered band {band}: {title}");
            Say(title, b?.Blurb ?? "");
        }

        DepthBand BandDef(int band)
        {
            var bands = R.Bands;
            return bands.Count == 0 ? null : bands[math.min(band, bands.Count - 1)];
        }

        string BandName(int band)
        {
            var b = BandDef(band);
            var name = b?.Name ?? $"band {band + 1}";
            return name.Replace("{n}", (band - R.Bands.Count + 2).ToString());
        }

        string Fill(string text, int band) =>
            text.Replace("{name}", BandName(band)).Replace("{depth}", MinePasses.BandBottom(band, _p).ToString());

        void Say(string speaker, string text) =>
            DialogueLine.Play(new Dialogue { Speaker = speaker, Text = text, DurationSeconds = 4f, CharsPerSecond = 50f }, _ui.Dialogue, _cts.Token).Forget();

        // ---- falling ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A landing at `speed` m/s: the drop it took (the lesser of v^2 / 2g and the height since you last stood, so a
        /// teleport mid-fall doesn't count) past safe_fall hurts, fall_damage_per_metre per metre.
        /// </summary>
        void Landed(float speed)
        {
            var g = math.max(0.1f, _walk.Gravity);
            var y = _player.Body.transform.position.y;
            var drop = speed * speed / (2f * g);
            if (!float.IsNaN(_top)) drop = math.min(drop, _top - y);
            _top = y;
            var safe = R.SafeFall > 0f ? R.SafeFall : 4.2f;
            if (drop <= safe || R.FallDamagePerMetre <= 0f) return;
            var damage = math.max(1, (int)math.round((drop - safe) * R.FallDamagePerMetre));
            Debug.Log($"[Depth] fell {drop:F1} m ({speed:F1} m/s): {damage} damage");
            _hurt.Publish(new HoldHurt(damage, string.IsNullOrEmpty(R.FallSource) ? "a fall" : R.FallSource));
        }

        // ---- dev warps -------------------------------------------------------------------------------------------

        void Warp(string to)
        {
            if (to != "seal" && to != "key" && to != "drop" && to != "collapse" && to != "sealkey") return;
            var me = _player.Body.transform.position;
            var here = _world.RegionOf(me);
            var band = here.y > 0 ? 0 : MinePasses.BandOf(MinePasses.DepthOf(here), _p);
            var seed = _world.Seed;
            switch (to)
            {
                case "sealkey":
                    _keys.Add(band);
                    Debug.Log($"[Depth] dev: given band {band}'s seal key");
                    return;
                case "seal":
                {
                    var y = -MinePasses.BandBottom(band, _p);
                    for (var r = 0; r <= 2; r++)
                    for (var dx = -r; dx <= r; dx++)
                    for (var dz = -r; dz <= r; dz++)
                    {
                        if (math.max(math.abs(dx), math.abs(dz)) != r) continue;
                        var region = new int3(here.x + dx, y, here.z + dz);
                        if (!MineGenerator.ShaftBelow(seed, region, _p, out _)) continue;
                        if (WarpTo(region, FixtureKind.Seal)) return;
                    }
                    break;
                }
                case "key":
                    for (var i = 0; i < _p.KeysPerBand; i++)
                        if (WarpTo(MinePasses.KeyRegion(seed, band, i, _p), FixtureKind.SealKey)) return;
                    break;
                case "drop":
                    for (var dy = 0; dy <= 4; dy++)
                    for (var r = 0; r <= 3; r++)
                    for (var dx = -r; dx <= r; dx++)
                    for (var dz = -r; dz <= r; dz++)
                    {
                        if (math.max(math.abs(dx), math.abs(dz)) != r) continue;
                        var region = new int3(here.x + dx, math.min(0, here.y) - dy, here.z + dz);
                        if (MinePasses.DropBelow(seed, region, _p, out _) && WarpTo(region, FixtureKind.DropLip)) return;
                    }
                    break;
                case "collapse":
                    if (WarpToRubble(here.y > 0 ? new int3(here.x, 0, here.z) : here)) return;
                    break;
            }
            Debug.Log($"[Depth] dev warp {to}: none found near region {here}");
        }

        bool WarpTo(int3 region, FixtureKind kind)
        {
            _world.LoadNow(region);
            if (!_world.TryGrid(region, out var grid) || !grid.Fixtures.IsCreated) return false;
            foreach (var f in grid.Fixtures)
            {
                if (f.Kind != kind) continue;
                if (StandBeside(grid, f.Cell, kind == FixtureKind.SealKey ? 2 : 1))
                {
                    Debug.Log($"[Depth] dev warp: {kind} at {grid.ToWorld(f.Cell)} (region {region})");
                    return true;
                }
            }
            return false;
        }

        bool WarpToRubble(int3 region)
        {
            _world.LoadNow(region);
            if (!_world.TryGrid(region, out var grid)) return false;
            for (var i = 0; i < grid.Cells.Length; i++)
            {
                if (grid.Cells[i].Kind != CellKind.Rubble) continue;
                var c = new int3(i % grid.Size.x, i / (grid.Size.x * grid.Size.z), i / grid.Size.x % grid.Size.z);
                if (StandBeside(grid, c, 2))
                {
                    Debug.Log($"[Depth] dev warp: rubble at {grid.ToWorld(c)} (region {region})");
                    return true;
                }
            }
            return false;
        }

        /// <summary>Stand `away` cells off a target cell on its level (a plain standable cell), facing it.</summary>
        bool StandBeside(HoldGrid grid, int3 target, int away)
        {
            for (var a = away; a >= 1; a--)
            for (var i = 0; i < 4; i++)
            {
                var d = HoldRules.Around(i);
                var c = target + d * a;
                var cell = grid.Get(c);
                if (!HoldLifePlan.Passable(cell)) continue;
                var at = _world.CellFloor(grid.ToWorld(c)) + new float3(0f, 0.05f, 0f);
                var face = -new float3(d);
                _hold.Teleport(at, math.degrees(math.atan2(face.x, face.z)));
                _top = at.y;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            _world.RegionShown -= Shown;
            _world.RegionHidden -= Hidden;
            _firstPerson.Landed -= Landed;
            _warpSub?.Dispose();
            _cts.Cancel();
            _cts.Dispose();
            foreach (var placed in _byRegion.Values)
                foreach (var go in placed.Objects) if (go != null) UnityEngine.Object.Destroy(go);
            foreach (var m in new[] { _hatch, _chain, _plate, _plinth }) if (m != null) UnityEngine.Object.Destroy(m);
        }
    }
}
