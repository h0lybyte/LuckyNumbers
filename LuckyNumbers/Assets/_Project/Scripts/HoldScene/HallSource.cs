using System;
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
    /// The hold's top (HallGenerator): region (0, 1, 0) is the gate and great hall, every other region at y >= 1 solid
    /// rock (nothing to mesh). Keeps the last hall's plan (gate, spawn, shaft) for HallSpawn and HallGatePresenter.
    /// Register before MinesSource.
    /// </summary>
    public sealed class HallSource : IRegionSource
    {
        readonly HoldConfig _config;
        HallPlan _plan;

        [Inject]
        public HallSource(HoldConfig config) => _config = config;

        public bool Claims(uint seed, int3 region) => HallGenerator.IsTop(region);

        public HoldGrid Generate(uint seed, int3 region, Allocator allocator)
        {
            if (!region.Equals(HallGenerator.HallRegion)) return HallGenerator.Rock(region, _config, allocator);
            var grid = HallGenerator.Generate(seed, _config, allocator, out var plan);
            _plan = plan;
            Debug.Log($"[Hall] seed {seed}: {plan.Rooms.Count} rooms, gate {plan.Gate[0]} out {plan.Outward}, shaft {plan.Shaft} " +
                      $"(attempt {plan.Attempts}{(plan.Valid ? "" : ", INVALID: " + plan.Failure)})");
            return grid;
        }

        /// <summary>The hall's plan for this seed (generates the plan alone if the region isn't built yet).</summary>
        public HallPlan Plan(uint seed)
        {
            if (_plan != null && _plan.Seed == seed) return _plan;
            HallGenerator.Generate(seed, _config, Allocator.Temp, out var plan).Dispose();
            return _plan = plan;
        }
    }

    /// <summary>Always: just inside the gate, facing into the great hall (from the surface or straight in).</summary>
    public sealed class HallSpawn : IHoldSpawn
    {
        readonly HallSource _hall;

        [Inject]
        public HallSpawn(HallSource hall) => _hall = hall;

        public bool TrySpawn(HoldWorld world, out float3 position, out float yawDegrees)
        {
            world.LoadNow(HallGenerator.HallRegion);
            var plan = _hall.Plan(world.Seed);
            var origin = HallGenerator.HallRegion * new int3(world.Config.RegionCells, world.Config.RegionLevels, world.Config.RegionCells);
            position = world.CellFloor(origin + plan.Spawn);
            var inward = -plan.Outward;
            yawDegrees = math.degrees(math.atan2(inward.x, inward.z));
            return true;
        }
    }

    /// <summary>
    /// The way out: near the gate (facing it) the bottom line says so; walking into the gate's outer edge fades out,
    /// sets HoldTrip.Leaving and asks for the Planet scene (it puts you outside the gate).
    /// </summary>
    [Presenter]
    public sealed class HallGatePresenter : ITickable, ILateTickable
    {
        const string WayOutHint = "The way out  ·  walk through the gate to the surface";
        const float FadeSeconds = 0.9f, EdgeReach = 0.6f, PromptReach = 5f;

        readonly HoldWorld _world;
        readonly HallSource _hall;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly HoldTrip _trip;
        readonly IPublisher<SceneChangeRequested> _scenes;
        float _leaving = -1f;
        bool _sent;

        [Inject]
        public HallGatePresenter(HoldWorld world, HallSource hall, PlayerView player, FirstPersonPresenter firstPerson,
                                 PlanetView ui, HoldTrip trip, IPublisher<SceneChangeRequested> scenes)
        {
            _world = world;
            _hall = hall;
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _trip = trip;
            _scenes = scenes;
        }

        public void Tick()
        {
            if (_leaving >= 0f || !_world.Loaded(HallGenerator.HallRegion) || !_world.TryGrid(HallGenerator.HallRegion, out var grid)) return;
            var plan = _hall.Plan(_world.Seed);
            var pos = (float3)_player.Body.transform.position;
            var outward = (float3)plan.Outward;

            // The gate's outer edge: a plane on the region boundary, spanning the gate cells, at the hall floor.
            float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
            foreach (var g in plan.Gate)
            {
                var floor = _world.CellFloor(grid.ToWorld(g));
                lo = math.min(lo, floor - _world.CellSize * 0.5f);
                hi = math.max(hi, floor + _world.CellSize * 0.5f);
            }
            var centre = (lo + hi) * 0.5f;
            var edge = centre + outward * _world.CellSize.x * 0.5f;
            var past = math.dot(pos - edge, outward);          // < 0 inside, 0 at the edge
            var across = math.abs(math.dot(pos - centre, new float3(outward.z, 0f, -outward.x)));
            var halfWidth = math.abs(math.dot(hi - lo, new float3(outward.z, 0f, -outward.x))) * 0.5f;
            var level = pos.y >= lo.y - 0.5f && pos.y <= lo.y + _world.CellSize.y * 2f;
            if (!level || across > halfWidth + 0.5f) return;

            if (past > -EdgeReach)
            {
                _leaving = 0f;
                _firstPerson.Lock(ControlLock.Leaving);
                Debug.Log("[Hall] out through the gate: to the surface");
                return;
            }
            var facing = (float3)_player.Body.transform.forward;
            if (past > -PromptReach && math.dot(facing, outward) > 0.3f) _world.Prompt = WayOutHint;
        }

        /// <summary>After HoldPresenter's fade-in: our fade-out wins the frame.</summary>
        public void LateTick()
        {
            if (_leaving < 0f) return;
            _leaving += Time.deltaTime;
            _ui.Fade(math.saturate(_leaving / FadeSeconds));
            if (_sent || _leaving < FadeSeconds) return;
            _sent = true;
            _trip.Leaving.Value = true;
            _scenes.Publish(new SceneChangeRequested(GameScene.Planet));
        }
    }
}
