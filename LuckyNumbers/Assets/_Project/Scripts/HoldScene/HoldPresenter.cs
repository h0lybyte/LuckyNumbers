using System;
using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// The endless mines under a dwarven hold, streamed round the player. Each region (MineGenerator) is generated from
    /// the lucky number and its coordinates, meshed off the main thread (HoldMesher jobs) and shown as chunks (HoldView).
    /// Loaded: the 3 x 3 regions at the player's depth plus the ones straight above and below. You drop down shafts;
    /// in a shaft's bottom cell, E climbs back up (no ladders yet).
    /// </summary>
    [Presenter]
    public sealed class HoldPresenter : IStartable, ITickable, IDisposable
    {
        const int BuildsPerFrame = 2;
        const string ClimbHint = "E  climb up the shaft";
        const string WalkHint = "WASD walk · Shift run · Space jump · drop down shafts to go deeper";

        sealed class Region
        {
            public HoldGrid Grid;
            public HoldMeshBuild Build;
            public readonly List<long> Chunks = new();
        }

        readonly HoldConfig _config;
        readonly HoldView _view;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly Dictionary<int3, Region> _regions = new();
        readonly HashSet<int3> _wanted = new();
        readonly List<int3> _drop = new();
        MineParams _rules;
        HoldMeshSettings _mesh;
        NativeArray<PaletteLayers> _palettes;
        IDisposable _seedSub, _actionSub;
        uint _seed;
        bool _started, _interact;
        float _fade = 1f, _hintAlpha = 1f;
        float3 _regionSize;   // metres
        int _deepest;         // lowest region depth reached (logged when it changes)
        float3 _spawn;

        [Inject]
        public HoldPresenter(HoldConfig config, HoldView view, PlayerView player, FirstPersonPresenter firstPerson,
                             PlanetView ui, IBufferedSubscriber<UniverseSeedChanged> seeds,
                             ISubscriber<InputActionPerformed> actions)
        {
            _config = config;
            _view = view;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _seeds = seeds;
            _actions = actions;
        }

        public void Start()
        {
            _ui.Bind();
            _ui.Fade(1f);
            _ui.Hint(WalkHint, 1f);
            _rules = MineParams.From(_config);
            _mesh = HoldMeshSettings.From(_config);
            _palettes = HoldPalettes.Resolve(_config.Textures, Allocator.Persistent);
            _regionSize = new float3(_config.RegionCells * _config.CellSize, _config.RegionLevels * _config.StoreyHeight,
                                     _config.RegionCells * _config.CellSize);
            _actionSub = _actions.Subscribe(e => { if (e.Action == InputAction.Interact) _interact = true; });
            _seedSub = _seeds.Subscribe(roll =>
            {
                if (_started) return;
                _started = true;
                _seed = roll.Seed == 0 ? 1u : roll.Seed;
                Enter();
            });
        }

        /// <summary>First region now (synchronously), the player on its top level, then the rest streams in.</summary>
        void Enter()
        {
            var home = new int3(0, 0, 0);
            var region = Load(home);
            region.Build.Handle.Complete();
            ShowBuilt(region);
            var spawn = SpawnCell(region.Grid);
            _spawn = CellFloor(region.Grid.ToWorld(spawn)) + new float3(0f, 0.05f, 0f);
            Teleport(_spawn, 0f);
            _firstPerson.Unlock(ControlLock.Spawn);
            Debug.Log($"[Hold] mines under lucky number {_seed}: spawned at cell {region.Grid.ToWorld(spawn)}");
        }

        public void Tick()
        {
            if (!_started) return;
            var pos = (float3)_player.Body.transform.position;
            var here = RegionOf(pos);

            // Stream: the 3 x 3 at this depth, plus straight above and below.
            _wanted.Clear();
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
                _wanted.Add(here + new int3(dx, 0, dz));
            _wanted.Add(here + new int3(0, 1, 0));
            _wanted.Add(here + new int3(0, -1, 0));

            _drop.Clear();
            foreach (var key in _regions.Keys) if (!_wanted.Contains(key)) _drop.Add(key);
            foreach (var key in _drop) Unload(key);

            var started = 0;
            foreach (var key in _wanted)
            {
                if (_regions.ContainsKey(key)) continue;
                if (started++ >= BuildsPerFrame) break;
                Load(key);
            }
            foreach (var region in _regions.Values)
                if (region.Build != null && region.Build.IsCompleted) ShowBuilt(region);

            if (here.y < _deepest)
            {
                _deepest = here.y;
                Debug.Log($"[Hold] deeper: region {here}");
            }

            // Fell through everything loaded (a region below not built yet): back to where you came in.
            if (!_regions.ContainsKey(here) && !_regions.ContainsKey(here + new int3(0, 1, 0))) Teleport(_spawn, 0f);

            Climb(pos);

            _fade = math.max(0f, _fade - Time.deltaTime * 1.2f);
            _ui.Fade(_fade);
            _hintAlpha = math.max(0f, _hintAlpha - Time.deltaTime * 0.08f);
            _interact = false;
        }

        /// <summary>In a shaft's bottom cell: show the prompt, and E puts you beside the shaft on the floor above.</summary>
        void Climb(float3 pos)
        {
            var cell = CellOf(pos + new float3(0f, 0.2f, 0f));
            if (!TryCell(cell, out var c) || c.Kind != CellKind.ShaftUp)
            {
                _ui.Hint(WalkHint, math.saturate(_hintAlpha * 2f));
                return;
            }
            _ui.Hint(ClimbHint, 1f);
            if (!_interact) return;
            var above = cell + new int3(0, 1, 0);
            foreach (var d in new[] { new int3(1, 0, 0), new int3(-1, 0, 0), new int3(0, 0, 1), new int3(0, 0, -1) })
            {
                var to = above + d;
                if (!TryCell(to, out var t) || !t.Walkable || t.Kind == CellKind.ShaftDown || (t.Flags & CellFlags.Pillar) != 0) continue;
                var yaw = math.degrees(math.atan2(d.x, d.z));
                Teleport(CellFloor(to) + new float3(0f, 0.05f, 0f), yaw);
                return;
            }
        }

        Region Load(int3 key)
        {
            var region = new Region { Grid = MineGenerator.Region(_seed, key, _rules, Allocator.Persistent) };
            region.Build = HoldMesher.Schedule(region.Grid, _palettes, _mesh);
            _regions[key] = region;
            return region;
        }

        void ShowBuilt(Region region)
        {
            var chunks = region.Build.Complete();
            region.Build.Dispose();
            region.Build = null;
            var origin = region.Grid.Origin;
            foreach (var chunk in chunks)
            {
                var key = ChunkKey(origin, chunk.Index);
                _view.Show(key, chunk.Mesh, chunk.Colliders);
                region.Chunks.Add(key);
            }
        }

        void Unload(int3 key)
        {
            var region = _regions[key];
            _regions.Remove(key);
            if (region.Build != null)
            {
                region.Build.Handle.Complete();
                foreach (var chunk in region.Build.Complete()) UnityEngine.Object.Destroy(chunk.Mesh);
                region.Build.Dispose();
            }
            foreach (var chunk in region.Chunks) _view.Hide(chunk);
            region.Grid.Dispose();
        }

        /// <summary>A walkable, plain cell on the top level, nearest the region's middle.</summary>
        static int3 SpawnCell(in HoldGrid grid)
        {
            var best = new int3(grid.Size.x / 2, grid.Size.y - 1, grid.Size.z / 2);
            var bestD = int.MaxValue;
            for (var level = grid.Size.y - 1; level >= 0 && bestD == int.MaxValue; level--)
            for (var z = 1; z < grid.Size.z - 1; z++)
            for (var x = 1; x < grid.Size.x - 1; x++)
            {
                var c = new int3(x, level, z);
                var cell = grid.Get(c);
                if (cell.Kind != CellKind.Mine || (cell.Flags & CellFlags.Pillar) != 0) continue;
                var d = math.abs(x - grid.Size.x / 2) + math.abs(z - grid.Size.z / 2);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        bool TryCell(int3 world, out HoldCell cell)
        {
            var key = new int3(FloorDiv(world.x, _config.RegionCells), FloorDiv(world.y, _config.RegionLevels),
                               FloorDiv(world.z, _config.RegionCells));
            if (_regions.TryGetValue(key, out var region))
            {
                cell = region.Grid.Get(world - region.Grid.Origin);
                return true;
            }
            cell = HoldCell.Solid;
            return false;
        }

        int3 CellOf(float3 pos) => (int3)math.floor(pos / new float3(_config.CellSize, _config.StoreyHeight, _config.CellSize));
        int3 RegionOf(float3 pos) => (int3)math.floor(pos / _regionSize);

        float3 CellFloor(int3 world) =>
            new((world.x + 0.5f) * _config.CellSize, world.y * _config.StoreyHeight, (world.z + 0.5f) * _config.CellSize);

        static int FloorDiv(int a, int b) => (int)math.floor(a / (float)b);

        /// <summary>World chunk column + region level: unique per chunk mesh (20 bits each, wraps far beyond play).</summary>
        long ChunkKey(int3 origin, int2 chunk)
        {
            var n = math.max(1, _config.ChunkCells);
            long x = FloorDiv(origin.x, n) + chunk.x, z = FloorDiv(origin.z, n) + chunk.y, y = origin.y;
            return (x & 0xFFFFF) | ((z & 0xFFFFF) << 20) | ((y & 0xFFFFF) << 40);
        }

        void Teleport(float3 position, float yaw)
        {
            var body = _player.Body;
            body.enabled = false;   // a CharacterController ignores transform moves while enabled
            body.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, 0f);
        }

        public void Dispose()
        {
            _seedSub?.Dispose();
            _actionSub?.Dispose();
            // The scene is going: its objects are gone with it, so only free the native memory.
            foreach (var region in _regions.Values)
            {
                if (region.Build != null)
                {
                    region.Build.Handle.Complete();
                    region.Build.Dispose();
                }
                region.Grid.Dispose();
            }
            _regions.Clear();
            if (_palettes.IsCreated) _palettes.Dispose();
        }
    }
}
