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
    /// The mines' puzzles (MinePasses.Puzzles; rules HoldConfig.puzzles): as a region is shown, its locked doors,
    /// keys, portcullises and levers are built (PuzzleKit), and destroyed when it goes. E within reach: pick up a key,
    /// unlock a door with the key of the same link (it's used up; without it the prompt says the key's somewhere in
    /// these tunnels), pull a lever (its portcullis rises). For this visit, picked keys stay picked (world cell), keys
    /// carried stay carried (region + link), and opened doors, raised gates and pulled levers stay so (world cell),
    /// however often their region streams out and back in. E and the prompts go through InteractionPresenter.
    /// </summary>
    [Presenter]
    public sealed class PuzzlePresenter : IInteractable, IStartable, ITickable, IDisposable
    {
        sealed class Thing
        {
            public HoldFixture Fixture;
            public int3 Region, Cell;     // Cell: world
            public PuzzleParts Parts;
            public bool Done;             // opened / raised / pulled / picked
            public float T;               // animation 0..1
            public int Side;              // door: which way it swung
        }

        readonly HoldWorld _world;
        readonly HoldView _view;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly Dictionary<int3, List<Thing>> _byRegion = new();
        readonly HashSet<int3> _picked = new();         // world cells of keys taken this visit
        readonly HashSet<int4> _carried = new();        // (region xyz, link) of keys in hand
        readonly Dictionary<int3, int> _opened = new(); // world cells of opened doors (-> swing side), raised gates, pulled levers
        readonly CancellationTokenSource _cts = new();
        readonly HoldMeshSettings _mesh;
        readonly Color _glint;
        PuzzleKit.Slices _slices;
        Light _light;
        Thing _near;          // this frame's nearest in reach (facing it, unless it's a key)
        float _nearDistance;

        static PuzzleRules Rules => GameContent.Hold.Puzzles;

        [Inject]
        public PuzzlePresenter(HoldWorld world, HoldView view, PlayerView player, FirstPersonPresenter firstPerson, PlanetView ui)
        {
            _world = world;
            _view = view;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            var c = world.Config;
            _mesh = HoldMeshSettings.From(c);
            _glint = new Color(c.LanternR, c.LanternG, c.LanternB) * 2.2f;
            _slices = PuzzleKit.Slices.From(c.Textures);
            // Now, not in Start: HoldPresenter's Start may show the first region before ours runs.
            _world.RegionShown += Shown;
            _world.RegionHidden += Hidden;
        }

        public void Start()
        {
            _light = new GameObject("Key glint light").AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = _glint / 2.2f;
            _light.range = 3f;
            _light.intensity = 1.4f;
            _light.shadows = LightShadows.None;
            _light.enabled = false;
        }

        void Shown(int3 region, HoldGrid grid)
        {
            if (!grid.Fixtures.IsCreated || grid.Fixtures.Length == 0) return;
            List<Thing> list = null;
            foreach (var f in grid.Fixtures)
            {
                if (f.Kind != FixtureKind.LockedDoor && f.Kind != FixtureKind.Key && f.Kind != FixtureKind.Portcullis && f.Kind != FixtureKind.Lever)
                    continue;
                var cell = grid.ToWorld(f.Cell);
                if (f.Kind == FixtureKind.Key && _picked.Contains(cell)) continue;
                var parts = PuzzleKit.Build(grid, f, _view.Material, _slices, _mesh, _glint);
                if (parts == null) continue;
                var thing = new Thing { Fixture = f, Region = region, Cell = cell, Parts = parts };
                if (_opened.TryGetValue(cell, out var side))
                {
                    thing.Done = true;
                    thing.T = 1f;
                    thing.Side = side;
                }
                PuzzleKit.Pose(parts, thing.T, thing.Side, _mesh);
                (list ??= new List<Thing>()).Add(thing);
            }
            if (list != null) _byRegion[region] = list;
        }

        void Hidden(int3 region)
        {
            if (!_byRegion.Remove(region, out var list)) return;
            foreach (var t in list) t.Parts.Destroy();
        }

        public void Tick()
        {
            var rules = Rules;
            var dt = Time.deltaTime;
            var time = Time.time;
            var me = _player.Body.transform.position;
            var look = _player.Body.transform.forward;
            var flatLook = new Vector2(look.x, look.z).normalized;
            var reach = rules != null && rules.Reach > 0f ? rules.Reach : 1.7f;
            Thing near = null, glow = null;
            float best = float.MaxValue, bestGlow = 9f;

            foreach (var list in _byRegion.Values)
            foreach (var t in list)
            {
                var kind = t.Fixture.Kind;
                if (t.Done && t.T < 1f)
                {
                    var seconds = kind == FixtureKind.Portcullis ? Seconds(rules?.GateRaiseSeconds, 1.6f)
                        : kind == FixtureKind.LockedDoor ? Seconds(rules?.DoorSwingSeconds, 0.9f) : 0.35f;
                    t.T = math.min(1f, t.T + dt / seconds);
                    PuzzleKit.Pose(t.Parts, t.T, t.Side, _mesh);
                }
                if (kind == FixtureKind.Key && t.Parts.Glint != null)
                {
                    var s = 0.8f + 0.35f * Mathf.Sin(time * 3.1f + t.Cell.x * 1.7f + t.Cell.z);
                    t.Parts.Glint.localScale = Vector3.one * (0.07f * s);
                    t.Parts.Glint.localRotation = Quaternion.Euler(45f, time * 90f, 45f);
                }
                if (t.Done) continue;
                var point = t.Parts.Point;
                if (math.abs(point.y - (me.y + 0.9f)) > 1.6f) continue;
                var to = new Vector2(point.x - me.x, point.z - me.z);
                var d = to.magnitude;
                if (kind == FixtureKind.Key && d < bestGlow) { bestGlow = d; glow = t; }
                if (d > reach) continue;
                // Doors, gates and levers: you're facing them (keys: just near). Nearest wins.
                if (kind != FixtureKind.Key && d > 0.3f && Vector2.Dot(flatLook, to / d) < 0.35f) continue;
                if (d < best) { best = d; near = t; }
            }

            _light.enabled = glow != null;
            if (glow != null)
            {
                _light.transform.position = glow.Parts.Glint.position;
                _light.intensity = 1.1f + 0.4f * Mathf.Sin(time * 3.1f);
            }

            _near = near;
            _nearDistance = best;
        }

        // ---- IInteractable: pick up a key, unlock a door you've its key for, pull a lever; else what's said -----

        bool Usable(Thing t) => t.Fixture.Kind switch
        {
            FixtureKind.Key or FixtureKind.Lever => true,
            FixtureKind.LockedDoor => Has(t),
            _ => false,
        };

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = default;
            var near = _near;
            if (near == null || near.Done) return false;
            var priority = near.Fixture.Kind == FixtureKind.Key ? InteractionPriority.Pickup
                : Usable(near) ? InteractionPriority.Place : InteractionPriority.Info;
            offer = new Interaction(near, priority, _nearDistance, Usable(near));
            return true;
        }

        public string Prompt(object key)
        {
            if (key is not Thing t) return "";
            var rules = Rules;
            switch (t.Fixture.Kind)
            {
                case FixtureKind.Key: return rules?.PickupPrompt ?? "E  pick up the key";
                case FixtureKind.LockedDoor:
                    if (Has(t)) return rules?.UnlockPrompt ?? "E  unlock the door";
                    var text = rules?.LockedText ?? "Locked: the key is somewhere in these tunnels";
                    return _carried.Count > 0 ? $"{text} (your {Plural(_carried.Count)} won't fit)" : text;
                case FixtureKind.Lever: return rules?.LeverPrompt ?? "E  pull the lever";
                default: return rules?.GateText ?? "A portcullis: there's a lever somewhere near";
            }
        }

        public void Interact(object key)
        {
            if (key is not Thing t || t.Done || !Usable(t)) return;
            switch (t.Fixture.Kind)
            {
                case FixtureKind.Key: PickUp(t); break;
                case FixtureKind.LockedDoor: Unlock(t); break;
                case FixtureKind.Lever: Pull(t); break;
            }
        }

        static float Seconds(float? v, float fallback) => v.HasValue && v.Value > 0.05f ? v.Value : fallback;

        string Plural(int n) => n == 1 ? "key" : $"{n} keys";

        static int4 KeyOf(int3 region, ushort link) => new(region, link);

        bool Has(Thing door) => _carried.Contains(KeyOf(door.Region, door.Fixture.Link));

        void PickUp(Thing key)
        {
            key.Done = true;
            _picked.Add(key.Cell);
            _carried.Add(KeyOf(key.Region, key.Fixture.Link));
            key.Parts.Destroy();
            _byRegion[key.Region].Remove(key);
            var rules = Rules;
            var name = string.IsNullOrEmpty(rules?.KeyName) ? "iron key" : rules.KeyName;
            var text = (rules?.PickupText ?? "It fits a door somewhere in these tunnels. Keys carried: {count}.").Replace("{count}", _carried.Count.ToString());
            Say(char.ToUpperInvariant(name[0]) + name[1..], text);
            Debug.Log($"[Puzzles] key {key.Cell} (region {key.Region}, link {key.Fixture.Link}) picked up; carrying {_carried.Count}");
        }

        void Unlock(Thing door)
        {
            _carried.Remove(KeyOf(door.Region, door.Fixture.Link));
            // Swing away from you: you're on local +z, so it goes to -z (side +1), and the other way round.
            var local = door.Parts.Root.InverseTransformPoint(_player.Body.transform.position);
            door.Side = local.z >= 0f ? 1 : -1;
            door.Done = true;
            _opened[door.Cell] = door.Side;
            Debug.Log($"[Puzzles] door {door.Cell} (region {door.Region}, link {door.Fixture.Link}) unlocked");
        }

        void Pull(Thing lever)
        {
            lever.Done = true;
            _opened[lever.Cell] = 0;
            foreach (var t in _byRegion[lever.Region])
            {
                if (t.Fixture.Kind != FixtureKind.Portcullis || t.Fixture.Link != lever.Fixture.Link || t.Done) continue;
                t.Done = true;
                _opened[t.Cell] = 0;
                Debug.Log($"[Puzzles] lever {lever.Cell} raised portcullis {t.Cell} (region {lever.Region}, link {lever.Fixture.Link})");
            }
        }

        void Say(string speaker, string text) =>
            DialogueLine.Play(new Dialogue { Speaker = speaker, Text = text, DurationSeconds = 3.5f, CharsPerSecond = 50f }, _ui.Dialogue, _cts.Token).Forget();

        public void Dispose()
        {
            _world.RegionShown -= Shown;
            _world.RegionHidden -= Hidden;
            _cts.Cancel();
            _cts.Dispose();
            foreach (var list in _byRegion.Values) foreach (var t in list) t.Parts.Destroy();
            _byRegion.Clear();
        }
    }
}
