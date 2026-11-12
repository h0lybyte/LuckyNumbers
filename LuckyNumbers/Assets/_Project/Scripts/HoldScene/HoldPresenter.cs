using System;
using System.Collections.Generic;
using LuckyNumbers.Hold;
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
    /// The Hold scene's driver: starts the deep (HoldWorld) from the lucky number, puts the player at the first
    /// IHoldSpawn that answers (else in the mines' first region), streams regions round them, fades in and shows
    /// the bottom line (HoldWorld.Prompt from other presenters, else the controls).
    /// </summary>
    // Registered by hand in HoldScope (AsSelf), not [Presenter]: other presenters inject it.
    public sealed class HoldPresenter : IStartable, ITickable, ILateTickable, IDisposable
    {
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static readonly int SunColor = Shader.PropertyToID("_LnSunColor");
        static readonly int SkyColor = Shader.PropertyToID("_LnSkyColor");
        static readonly int GroundColor = Shader.PropertyToID("_LnGroundColor");
        const string WalkHint = "WASD walk · Shift run · Space jump · climb shafts to go deeper";

        readonly HoldWorld _world;
        readonly IReadOnlyList<IHoldSpawn> _spawns;
        readonly PlayerView _player;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlanetView _ui;
        readonly IBufferedSubscriber<UniverseSeedChanged> _seeds;
        readonly ISubscriber<DevWarpRequested> _warps;
        IDisposable _seedSub, _warpSub;
        bool _started;
        float _fade = 1f, _hintAlpha = 1f;
        float3 _spawn;
        float _spawnYaw;
        int _deepest;

        [Inject]
        public HoldPresenter(HoldWorld world, IEnumerable<IHoldSpawn> spawns, PlayerView player,
                             FirstPersonPresenter firstPerson, PlanetView ui, IBufferedSubscriber<UniverseSeedChanged> seeds,
                             ISubscriber<DevWarpRequested> warps)
        {
            _warps = warps;
            _world = world;
            _spawns = new List<IHoldSpawn>(spawns);
            _player = player;
            _firstPerson = firstPerson;
            _ui = ui;
            _seeds = seeds;
        }

        public void Start()
        {
            _ui.Bind();
            _ui.Fade(1f);
            _ui.Hint(WalkHint, 1f);
            // Lit models (creatures, the Ship shader) underground: a dim warm "sun" kept overhead (lantern light from
            // above), cool rock from the sides and below.
            var c = _world.Config;
            Shader.SetGlobalVector(SunColor, new Vector4(c.LanternR, c.LanternG, c.LanternB, 1f) * 0.55f);
            Shader.SetGlobalVector(SkyColor, new Vector4(0.45f, 0.4f, 0.34f, 1f));
            Shader.SetGlobalVector(GroundColor, new Vector4(0.3f, 0.28f, 0.26f, 1f));
            _seedSub = _seeds.Subscribe(roll =>
            {
                if (_started) return;
                _started = true;
                _world.Begin(roll.Seed == 0 ? 1u : roll.Seed);
                Enter();
            });
            // Dev (the page's LN.send('DevWarpRequested', {to: 'deep'})): one region down, onto its top level.
            _warpSub = _warps.Subscribe(e => { if (_started && e.To == "deep") Deeper(); });
        }

        void Deeper()
        {
            var region = _world.RegionOf((float3)_player.Body.transform.position) + new int3(0, -1, 0);
            _world.LoadNow(region);
            if (!_world.TryGrid(region, out var grid)) return;
            for (var level = grid.Size.y - 1; level >= 0; level--)
            for (var z = 2; z < grid.Size.z - 2; z++)
            for (var x = 2; x < grid.Size.x - 2; x++)
            {
                var c = grid.Get(new int3(x, level, z));
                if (c.Kind != CellKind.Mine || (c.Flags & CellFlags.Pillar) != 0) continue;
                Teleport(_world.CellFloor(grid.ToWorld(new int3(x, level, z))) + new float3(0f, 0.05f, 0f), 0f);
                Debug.Log($"[Hold] dev warp deep: region {region}");
                return;
            }
        }

        void Enter()
        {
            _world.LoadNow(new int3(0, 0, 0));
            foreach (var s in _spawns)
                if (s.TrySpawn(_world, out _spawn, out _spawnYaw)) break;
            _world.LoadNow(_world.RegionOf(_spawn));
            Teleport(_spawn + new float3(0f, 0.05f, 0f), _spawnYaw);
            _firstPerson.Unlock(ControlLock.Spawn);
            Debug.Log($"[Hold] mines under lucky number {_world.Seed}: spawned at cell {_world.CellOf(_spawn)}");
        }

        public void Tick()
        {
            if (!_started) return;
            var pos = (float3)_player.Body.transform.position;
            Shader.SetGlobalVector(SunPosition, new Vector4(pos.x + 8f, pos.y + 40f, pos.z - 6f, 1f));
            _world.Stream(pos);
            var here = _world.RegionOf(pos);
            if (here.y < _deepest)
            {
                _deepest = here.y;
                Debug.Log($"[Hold] deeper: region {here}");
            }
            // Fell through everything loaded (a region below not built yet): back to where you came in.
            if (!_world.Loaded(here) && !_world.Loaded(here + new int3(0, 1, 0))) Teleport(_spawn + new float3(0f, 0.05f, 0f), _spawnYaw);

            _fade = math.max(0f, _fade - Time.deltaTime * 1.2f);
            _ui.Fade(_fade);
            _hintAlpha = math.max(0f, _hintAlpha - Time.deltaTime * 0.08f);
        }

        /// <summary>After every presenter's Tick: their prompt if any, else the fading controls line.</summary>
        public void LateTick()
        {
            if (!_started) return;
            var prompt = _world.Prompt;
            if (!string.IsNullOrEmpty(prompt)) _ui.Hint(prompt, 1f);
            else _ui.Hint(WalkHint, math.saturate(_hintAlpha * 2f));
            _world.Prompt = null;
        }

        public void Teleport(float3 position, float yaw)
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
            _warpSub?.Dispose();
        }
    }
}
