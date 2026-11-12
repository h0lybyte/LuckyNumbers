using System;
using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using VContainer;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Fills a region of the deep. The first registered source that claims a region generates it. MinesSource
    /// claims everything, so HoldScope registers it last; register others (e.g. the hall at the top) before it.
    /// </summary>
    public interface IRegionSource
    {
        bool Claims(uint seed, int3 region);
        HoldGrid Generate(uint seed, int3 region, Allocator allocator);
    }

    /// <summary>
    /// Where the player starts. Asked in registration order once region (0,0,0) is loaded; the first that answers
    /// wins. MinesSpawn always answers, so HoldScope registers it last.
    /// </summary>
    public interface IHoldSpawn
    {
        bool TrySpawn(HoldWorld world, out float3 position, out float yawDegrees);
    }

    /// <summary>
    /// The deep as the scene sees it: regions generated (by their IRegionSource), meshed off the main thread and
    /// shown (HoldView), streamed round a point; cell lookups across regions; one prompt line per frame.
    /// Plain service (HoldScope singleton); HoldPresenter drives it. Other presenters read cells and listen for
    /// regions coming and going (RegionShown / RegionHidden) to place things in them.
    /// </summary>
    public sealed class HoldWorld : IDisposable
    {
        const int BuildsPerFrame = 2;

        sealed class Region
        {
            public HoldGrid Grid;
            public HoldMeshBuild Build;
            public bool Shown;
            public readonly List<long> Chunks = new();
        }

        readonly HoldConfig _config;
        readonly HoldView _view;
        readonly IReadOnlyList<IRegionSource> _sources;
        readonly Dictionary<int3, Region> _regions = new();
        readonly HashSet<int3> _wanted = new();
        readonly List<int3> _drop = new();
        readonly HoldMeshSettings _mesh;
        NativeArray<PaletteLayers> _palettes;

        public HoldConfig Config => _config;
        public uint Seed { get; private set; }
        public float3 RegionSize { get; }
        /// <summary>Metres per cell: (cell, storey, cell).</summary>
        public float3 CellSize { get; }

        /// <summary>A region's grid is in and its meshes are showing (the grid stays valid until RegionHidden).</summary>
        public event Action<int3, HoldGrid> RegionShown;
        /// <summary>A region is about to go (its grid is disposed right after).</summary>
        public event Action<int3> RegionHidden;

        /// <summary>The line to show at the bottom this frame (e.g. "E  climb"); HoldPresenter shows it and clears it.</summary>
        public string Prompt { get; set; }

        [Inject]
        public HoldWorld(HoldConfig config, HoldView view, IEnumerable<IRegionSource> sources)
        {
            _config = config;
            _view = view;
            _sources = new List<IRegionSource>(sources);
            _mesh = HoldMeshSettings.From(config);
            _palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            CellSize = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            RegionSize = new float3(config.RegionCells, config.RegionLevels, config.RegionCells) * CellSize;
        }

        public void Begin(uint seed) => Seed = seed;

        public bool Loaded(int3 region) => _regions.TryGetValue(region, out var r) && r.Shown;

        /// <summary>Generate, mesh and show a region now (blocks): the start region.</summary>
        public void LoadNow(int3 key)
        {
            if (!_regions.TryGetValue(key, out var region)) region = Load(key);
            if (!region.Shown) Show(key, region);
        }

        /// <summary>Keep the 3 x 3 regions at this point's depth plus the ones straight above and below.</summary>
        public void Stream(float3 around)
        {
            var here = RegionOf(around);
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
            _drop.Clear();
            foreach (var pair in _regions)
                if (!pair.Value.Shown && pair.Value.Build.IsCompleted) _drop.Add(pair.Key);
            foreach (var key in _drop) Show(key, _regions[key]);
        }

        /// <summary>The cell at world cell coordinates, if its region is loaded.</summary>
        public bool TryCell(int3 world, out HoldCell cell)
        {
            if (_regions.TryGetValue(RegionOfCell(world), out var region))
            {
                cell = region.Grid.Get(world - region.Grid.Origin);
                return true;
            }
            cell = HoldCell.Solid;
            return false;
        }

        /// <summary>A loaded region's grid (valid until RegionHidden).</summary>
        public bool TryGrid(int3 region, out HoldGrid grid)
        {
            if (_regions.TryGetValue(region, out var r)) { grid = r.Grid; return true; }
            grid = default;
            return false;
        }

        public int3 CellOf(float3 position) => (int3)math.floor(position / CellSize);
        public int3 RegionOf(float3 position) => (int3)math.floor(position / RegionSize);
        public int3 RegionOfCell(int3 world) =>
            new(FloorDiv(world.x, _config.RegionCells), FloorDiv(world.y, _config.RegionLevels), FloorDiv(world.z, _config.RegionCells));
        /// <summary>World position of the middle of a cell's floor.</summary>
        public float3 CellFloor(int3 world) => (new float3(world) + new float3(0.5f, 0f, 0.5f)) * CellSize;

        static int FloorDiv(int a, int b) => (int)math.floor(a / (float)b);

        Region Load(int3 key)
        {
            var grid = default(HoldGrid);
            foreach (var source in _sources)
                if (source.Claims(Seed, key)) { grid = source.Generate(Seed, key, Allocator.Persistent); break; }
            if (!grid.IsCreated) throw new InvalidOperationException($"no IRegionSource claims region {key}");
            var region = new Region { Grid = grid, Build = HoldMesher.Schedule(grid, _palettes, _mesh) };
            _regions[key] = region;
            return region;
        }

        void Show(int3 key, Region region)
        {
            var chunks = region.Build.Complete();
            region.Build.Dispose();
            region.Build = null;
            region.Shown = true;
            foreach (var chunk in chunks)
            {
                var id = ChunkKey(region.Grid.Origin, chunk.Index);
                _view.Show(id, chunk.Mesh, chunk.Colliders);
                region.Chunks.Add(id);
            }
            RegionShown?.Invoke(key, region.Grid);
        }

        void Unload(int3 key)
        {
            var region = _regions[key];
            if (region.Shown) RegionHidden?.Invoke(key);
            _regions.Remove(key);
            if (region.Build != null)
            {
                foreach (var chunk in region.Build.Complete()) UnityEngine.Object.Destroy(chunk.Mesh);
                region.Build.Dispose();
            }
            foreach (var chunk in region.Chunks) _view.Hide(chunk);
            region.Grid.Dispose();
        }

        /// <summary>World chunk column + region level: unique per chunk mesh (20 bits each, wraps far beyond play).</summary>
        long ChunkKey(int3 origin, int2 chunk)
        {
            var n = math.max(1, _config.ChunkCells);
            long x = FloorDiv(origin.x, n) + chunk.x, z = FloorDiv(origin.z, n) + chunk.y, y = origin.y;
            return (x & 0xFFFFF) | ((z & 0xFFFFF) << 20) | ((y & 0xFFFFF) << 40);
        }

        /// <summary>The scene is going: its objects go with it, so only free the native memory.</summary>
        public void Dispose()
        {
            foreach (var region in _regions.Values)
            {
                region.Build?.Dispose();
                region.Grid.Dispose();
            }
            _regions.Clear();
            if (_palettes.IsCreated) _palettes.Dispose();
        }
    }

    /// <summary>The endless mines (MineGenerator): every region nobody else claims.</summary>
    public sealed class MinesSource : IRegionSource
    {
        readonly MineParams _rules;

        [Inject]
        public MinesSource(HoldConfig config) => _rules = MineParams.From(config);

        public bool Claims(uint seed, int3 region) => true;
        public HoldGrid Generate(uint seed, int3 region, Allocator allocator) => MineGenerator.Region(seed, region, _rules, allocator);
    }

    /// <summary>Fallback spawn: a plain mine cell on region (0,0,0)'s top level, nearest its middle.</summary>
    public sealed class MinesSpawn : IHoldSpawn
    {
        // Explicit [Inject] constructor: IL2CPP strips implicit ones and VContainer then can't build the type.
        [Inject]
        public MinesSpawn() { }

        public bool TrySpawn(HoldWorld world, out float3 position, out float yawDegrees)
        {
            yawDegrees = 0f;
            position = default;
            if (!world.TryGrid(new int3(0, 0, 0), out var grid)) return false;
            var best = new int3(grid.Size.x / 2, grid.Size.y - 1, grid.Size.z / 2);
            var bestD = int.MaxValue;
            for (var level = grid.Size.y - 1; level >= 0 && bestD == int.MaxValue; level--)
            for (var z = 1; z < grid.Size.z - 1; z++)
            for (var x = 1; x < grid.Size.x - 1; x++)
            {
                var cell = grid.Get(new int3(x, level, z));
                if (cell.Kind != CellKind.Mine || (cell.Flags & CellFlags.Pillar) != 0) continue;
                var d = math.abs(x - grid.Size.x / 2) + math.abs(z - grid.Size.z / 2);
                if (d < bestD) { bestD = d; best = new int3(x, level, z); }
            }
            position = world.CellFloor(grid.ToWorld(best));
            return true;
        }
    }
}
