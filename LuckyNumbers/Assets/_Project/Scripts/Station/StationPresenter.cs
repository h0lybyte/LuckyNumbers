using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Intro;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The player's station: builds it from content/station.txtpb, puts the player in their cabin with first-person
    /// control, and hangs it in their universe (two scales, two cameras: the universe is seen from a fixed point in
    /// orbit, turned with the player's head every frame; the station and arms draw over it, so windows look out at
    /// the real system). Their ship waits in the docking bay: E beside it boards and goes on to StationScene.next.
    ///
    /// Walking into the call's room rings it (E, or the page's Answer, picks up); Dispatch's lines play in the dialogue
    /// box, then the objective points at the ship. Consoles (E): lights on / off / emergency, the PS1 look. Doors slide
    /// open as you come near. The page can switch lights, the PS1 look and rebuild the whole plan (layout editor).
    /// </summary>
    [Presenter]
    public sealed class StationPresenter : IStartable, IPostLateTickable, IDisposable
    {
        const float UseDistance = 1.7f;   // metres from a console to use it

        readonly StationScene _config;
        readonly StationView _station;
        readonly PlayerView _player;
        readonly IFirstPerson _firstPerson;
        readonly ISpaceDirector _space;
        readonly PlanetView _ui;
        readonly ISceneFlow _flow;
        readonly StationState _state;
        readonly ShipView _ship;
        readonly PlayerShip _playerShip;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly ISubscriber<StationLightsRequested> _lightRequests;
        readonly ISubscriber<InputActionPerformed> _actions;
        readonly ISubscriber<StationCallAnswered> _answers;
        readonly ISubscriber<StationPlanRequested> _plans;
        readonly ISubscriber<PsxModeRequested> _psxRequests;
        readonly ISubscriber<DevWarpRequested> _warps;
        readonly CancellationTokenSource _cts = new();
        readonly List<IDisposable> _subscriptions = new();
        static readonly int Psx = Shader.PropertyToID("_LnPsx");

        StationLayout _layout;   // as built: content's, seeded (generator), or the page's editor
        readonly Dictionary<Vector2Int, string> _roomOf = new();
        bool _inSpace, _leaving, _interact, _toggleMap, _closeMap;
        Vector3 _orbitPosition, _dockPosition;
        Bounds _hull;   // the docked ship's world bounds (boarding is measured from it)
        Quaternion _stationInSpace = Quaternion.identity;
        float _fade = 1f, _hintAlpha;
        string _planet = "the planet", _freighter = "the freighter", _museum = "Museum";

        // The call.
        int _line = -1;
        float _lineTime;

        [Inject]
        public StationPresenter(StationScene config, StationView station, PlayerView player, IFirstPerson firstPerson,
                                ISpaceDirector space, PlanetView ui, ISceneFlow flow, StationState state, ShipView ship,
                                PlayerShip playerShip, IPublisher<SceneChangeRequested> scenes,
                                ISubscriber<StationLightsRequested> lightRequests, ISubscriber<InputActionPerformed> actions,
                                ISubscriber<StationCallAnswered> answers, ISubscriber<StationPlanRequested> plans,
                                ISubscriber<PsxModeRequested> psxRequests, ISubscriber<DevWarpRequested> warps)
        {
            _config = config;
            _station = station;
            _player = player;
            _firstPerson = firstPerson;
            _space = space;
            _ui = ui;
            _flow = flow;
            _state = state;
            _ship = ship;
            _playerShip = playerShip;
            _scenes = scenes;
            _lightRequests = lightRequests;
            _actions = actions;
            _answers = answers;
            _plans = plans;
            _psxRequests = psxRequests;
            _warps = warps;
        }

        public void Start()
        {
            Build(StationLayout.FromContent(_config));
            SetLights(StationLightMode.On);
            SetGlow(_config.Glow);
            SetPsx(false);
            _state.Call.Value = CallPhase.None;
            _state.Caller.Value = _config.Call?.Caller ?? "";
            _state.Objective.Value = "";
            _subscriptions.Add(_lightRequests.Subscribe(e => SetLights(e.Mode)));
            _subscriptions.Add(_actions.Subscribe(e =>
            {
                if (e.Action == InputAction.Interact) _interact = true;
                if (e.Action == InputAction.Map) _toggleMap = true;      // M: the station map (overlay)
                if (e.Action == InputAction.Cancel) _closeMap = true;
            }));
            _state.MapOpen.Value = false;
            _subscriptions.Add(_answers.Subscribe(_ => Answer()));
            _subscriptions.Add(_plans.Subscribe(e => Rebuild(e.Plan)));
            _subscriptions.Add(_warps.Subscribe(e => WarpTo(e.To)));
            _subscriptions.Add(_psxRequests.Subscribe(e => SetPsx(e.On)));

            var spawn = StationPlan.Centre(_station.Plan.Spawn);
            Teleport(spawn, _config.SpawnYaw);
            _firstPerson.Active = true;
            PlaceWeapon(spawn, _config.SpawnYaw);

            _ui.Bind();
            _ui.Fade(1f);
            _ui.Dialogue(null, null, 0);
            _ui.MapHint(null);
            _hintAlpha = 1f;
            _ui.Hint(_config.Hint, _hintAlpha);
            DockShip();
            _flow.Preload(_config.Next);
            PlaceInSpace(_cts.Token).Forget();
        }

        void Build(StationLayout layout)
        {
            _layout = layout;
            _station.Build(_config, layout);
            _roomOf.Clear();
            var plan = _station.Plan;
            foreach (var cells in plan.Rooms())
            {
                string name = null;
                foreach (var r in layout.Rooms)
                    if (cells.Contains(plan.PlanCell(r.Col, r.Row))) name = r.Name;
                if (name == null) continue;
                foreach (var c in cells) _roomOf[c] = name;
            }
            _state.Plan.Value = string.Join("\n", layout.Plan);
            // For the map: every room ("name|col|row") and the dock.
            _state.Rooms.Clear();
            foreach (var r in layout.Rooms) _state.Rooms.Add($"{r.Name}|{r.Col}|{r.Row}");
            if (layout.Dock != null)
            {
                _state.DockCol.Value = layout.Dock.Col;
                _state.DockRow.Value = layout.Dock.Row;
            }
        }

        /// <summary>Lamp glow: the scene's bloom (a Volume built by SceneBuilder), from content.</summary>
        static void SetGlow(float intensity)
        {
            var volume = UnityEngine.Object.FindAnyObjectByType<Volume>();
            if (volume == null || volume.sharedProfile == null || !volume.sharedProfile.TryGet<Bloom>(out var bloom)) return;
            bloom.active = intensity > 0f;
            bloom.intensity.Override(intensity);
        }

        /// <summary>The test gun (if the scene has one): dropped on the deck just ahead of where you start.</summary>
        static void PlaceWeapon(Vector3 spawn, float yaw)
        {
            var gun = UnityEngine.Object.FindAnyObjectByType<WeaponView>(FindObjectsInactive.Include);
            if (gun == null) return;
            var facing = Quaternion.Euler(0f, yaw, 0f);
            if (gun.TryGetComponent<Rigidbody>(out var body)) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            gun.transform.SetPositionAndRotation(spawn + facing * new Vector3(0.25f, 0.35f, 1.0f), facing * Quaternion.Euler(0f, 80f, 0f));
            gun.gameObject.SetActive(true);
        }

        /// <summary>
        /// Dev (the page's inspector): stand 1.6 m from the first prop of piece `piece` (e.g. "cryo_pod"), on the side
        /// of its room's middle, facing it. For looking at things up close quickly.
        /// </summary>
        void WarpTo(string piece)
        {
            if (_layout == null || string.IsNullOrEmpty(piece)) return;
            var prop = _layout.Props.Find(p => p.Piece == piece);
            if (prop == null) { Debug.Log($"[Station] dev warp: no prop '{piece}'"); return; }
            var at = _station.Plan.PlanToWorld(prop.Col, prop.Row);
            // Towards the nearest room's middle: that's the open side of something against a wall.
            var inside = at + Vector3.forward;
            var best = float.MaxValue;
            foreach (var r in _layout.Rooms)
            {
                var c = _station.Plan.PlanToWorld(r.Col, r.Row);
                var d = Vector3.Distance(c, at);
                if (d < best) { best = d; inside = c; }
            }
            var away = Vector3.ProjectOnPlane(inside - at, Vector3.up);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            var stand = at + away.normalized * 1.6f;
            stand.y = _player.Body.transform.position.y;
            var face = at - stand;
            Teleport(stand, Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg);
            Debug.Log($"[Station] dev warp to {piece}");
        }

        void Teleport(Vector3 position, float yaw)
        {
            var body = _player.Body;
            body.enabled = false;   // a CharacterController ignores transform moves while enabled
            body.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            _firstPerson.Look(yaw, 0f);
        }

        void DockShip()
        {
            var dock = _layout.Dock;
            if (dock == null || dock.Length <= 0f) return;
            _dockPosition = _station.Plan.PlanToWorld(dock.Col, dock.Row);
            _ship.Show(PlayerShipSkin.Current(_playerShip), dock.Length);
            // Resting on its belly a little above the deck, nose along the dock's heading.
            _ship.Pose(_dockPosition + Vector3.up * (dock.Length * 0.16f), Quaternion.Euler(0f, dock.Yaw, 0f));
            var first = true;
            foreach (var r in _ship.GetComponentsInChildren<Renderer>())
            {
                if (first) { _hull = r.bounds; first = false; }
                else _hull.Encapsulate(r.bounds);
            }
            if (first) _hull = new Bounds(_dockPosition, Vector3.one * dock.Length);
        }

        /// <summary>Wait for the universe, then park its camera in orbit around the station's body.</summary>
        async UniTaskVoid PlaceInSpace(CancellationToken ct)
        {
            while (_space.Worlds.Count == 0) await UniTask.Yield(ct);
            var worlds = _space.Worlds;
            var target = string.IsNullOrEmpty(_config.Orbit) ? SpacePresenter.CrashSite(worlds, _space.Seed) : _config.Orbit;
            float3 at = default;
            var diameter = 1f;
            while (!_space.TryGetBody(target, out at, out diameter)) await UniTask.Yield(ct);
            _space.TakeCamera();

            // Names for the call: the planet the station orbits, the freighter that's going down (cargo manifest).
            var index = 0;
            for (var i = 0; i < worlds.Count; i++) if (worlds[i] == target) index = i;
            _planet = BodyNames.Planet(_space.Seed, index);
            if (_config.Generator != null && _config.Generator.Enabled)
            {
                // Your station, from your lucky number: built while the screen is still black.
                Build(LayoutGenerator.Generate(_config, _space.Seed));
                SetLights(_state.Lights.Value);
                Teleport(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);
                PlaceWeapon(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);   // the new spawn's
                DockShip();
            }
            var manifest = LuckyNumbers.Cargo.Manifest.Generate(GameContent.Cargo, _space.Seed);
            _freighter = manifest.Ship;
            _museum = manifest.Museum;

            // Sunward side (lit), a little above the orbit plane; the station's north faces the body.
            var toSun = _space.TryGetBody("sun", out var sun, out _) ? math.normalizesafe(sun - at, new float3(1, 0, 0)) : new float3(1, 0, 0);
            var side = math.normalizesafe(math.cross(new float3(0, 1, 0), toSun), new float3(0, 0, 1));
            var away = math.normalize(toSun * 0.6f + side * 0.75f + new float3(0, 0.2f, 0));
            _orbitPosition = (Vector3)(at + away * diameter * math.max(0.6f, _config.OrbitDiameters));
            _stationInSpace = Quaternion.LookRotation((Vector3)(at - (float3)_orbitPosition), Vector3.up);
            _space.Camera.nearClipPlane = math.max(0.001f, diameter * 0.01f);
            _inSpace = true;
            Debug.Log($"[Station] ready: orbiting {target} \"{_planet}\" (seed {_space.Seed})"); // marker for ln.py webshot
        }

        public void PostLateTick()
        {
            var dt = Time.deltaTime;
            var eyes = _player.Camera;
            if (_inSpace)
            {
                // The universe seen from the station's point in orbit, turned as the player's head turns.
                var cam = _space.Camera;
                cam.transform.SetPositionAndRotation(_orbitPosition, _stationInSpace * eyes.transform.rotation);
                cam.fieldOfView = eyes.fieldOfView;
                if (!_leaving) _fade = Mathf.MoveTowards(_fade, 0f, dt / 0.8f);
            }
            if (_leaving) _fade = Mathf.MoveTowards(_fade, 1f, dt / 0.6f);
            _ui.Fade(_fade);

            var position = _player.Body.transform.position;

            // The map: M toggles it, Esc closes it; it follows you (plan coordinates, compass heading).
            if (_toggleMap) _state.MapOpen.Value = !_state.MapOpen.Value;
            else if (_closeMap && _state.MapOpen.Value) _state.MapOpen.Value = false;
            _toggleMap = _closeMap = false;
            var plan = _station.Plan;
            var col = position.x / StationPlan.Cell - 0.5f;
            var row = plan.Depth - 1 - (position.z / StationPlan.Cell - 0.5f);
            var yaw = eyes.transform.eulerAngles.y;
            if (Mathf.Abs(col - _state.PlayerCol.Value) > 0.05f || Mathf.Abs(row - _state.PlayerRow.Value) > 0.05f)
            {
                _state.PlayerCol.Value = col;
                _state.PlayerRow.Value = row;
            }
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, _state.PlayerYaw.Value)) > 3f) _state.PlayerYaw.Value = yaw;

            _station.UpdateDoors(position, _config.DoorOpenDistance > 0f ? _config.DoorOpenDistance : 2.6f, dt);

            // Room you're in, and the call it may set off.
            var cell = StationPlan.CellOf(position);
            var room = _roomOf.TryGetValue(cell, out var r) ? r : "";
            if (_state.Room.Value != room) _state.Room.Value = room;
            var call = _config.Call;
            if (call != null && _state.Call.Value == CallPhase.None && room != "" && room == _layout.CallRoom)
            {
                _state.Call.Value = CallPhase.Ringing;
                Debug.Log($"[Station] call from {call.Caller}");
            }
            TickCall(dt);

            // What E would do here: answer a ringing call, use the nearest console, or board the ship.
            var prompt = "";
            StationSwitch console = null;
            if (_state.Call.Value == CallPhase.Ringing) prompt = Fill(call.RingPrompt);
            else
            {
                var best = UseDistance;
                foreach (var sw in _layout.Switches)
                {
                    if (sw.Action == StationAction.Answer) continue;   // only while ringing
                    var at = _station.Plan.PlanToWorld(sw.Col, sw.Row);
                    var d = Vector2.Distance(new Vector2(at.x, at.z), new Vector2(position.x, position.z));
                    if (d < best) { best = d; console = sw; }
                }
                if (console != null) prompt = console.Prompt;
                else if (NearShip(position)) prompt = _layout.Dock.BoardPrompt;
            }
            if (_state.Prompt.Value != prompt) _state.Prompt.Value = prompt;

            if (_interact && !_leaving)
            {
                if (_state.Call.Value == CallPhase.Ringing) Answer();
                else if (console != null) Use(console.Action);
                else if (NearShip(position)) Board();
            }
            _interact = false;

            // Hint line: the E prompt, else the objective, else the controls (fading out).
            _hintAlpha = Mathf.MoveTowards(_hintAlpha, 0f, dt / 8f);
            if (prompt != "") _ui.Hint(prompt, 1f);
            else if (_state.Objective.Value != "") _ui.Hint(_state.Objective.Value, 0.8f);
            else _ui.Hint(_config.Hint, Mathf.Clamp01(_hintAlpha * 2f));
        }

        bool NearShip(Vector3 position)
        {
            var dock = _layout.Dock;
            if (dock == null || dock.Length <= 0f) return false;
            // From the hull (its bounds, on the deck plane), not the middle: a 9 m ship can be boarded from any side.
            var near = _hull.ClosestPoint(new Vector3(position.x, _hull.center.y, position.z));
            var d = Vector2.Distance(new Vector2(near.x, near.z), new Vector2(position.x, position.z));
            return d < Mathf.Max(1f, dock.BoardDistance);
        }

        void Use(StationAction action)
        {
            switch (action)
            {
                case StationAction.Lights:
                    var next = _state.Lights.Value switch
                    {
                        StationLightMode.On => StationLightMode.Off,
                        StationLightMode.Off => StationLightMode.Emergency,
                        _ => StationLightMode.On,
                    };
                    SetLights(next);
                    break;
                case StationAction.Psx:
                    SetPsx(!_state.Psx.Value);
                    break;
                case StationAction.Answer:
                    Answer();
                    break;
            }
        }

        void Answer()
        {
            if (_state.Call.Value != CallPhase.Ringing) return;
            _state.Call.Value = CallPhase.Active;
            _line = 0;
            _lineTime = 0f;
        }

        void TickCall(float dt)
        {
            var call = _config.Call;
            if (call == null || _state.Call.Value != CallPhase.Active) return;
            if (_line >= call.Lines.Count)
            {
                _ui.Dialogue(null, null, 0);
                _state.Call.Value = CallPhase.Ended;
                _state.Objective.Value = Fill(call.Objective);
                return;
            }
            var line = call.Lines[_line];
            var text = Fill(line.Text);
            _lineTime += dt;
            var cps = call.CharsPerSecond > 0f ? call.CharsPerSecond : 36f;
            _ui.Dialogue(Fill(line.Speaker), text, Mathf.FloorToInt(_lineTime * cps));
            var hold = Mathf.Max(line.Seconds, text.Length / cps + 1.2f);
            if (_lineTime >= hold)
            {
                _line++;
                _lineTime = 0f;
            }
        }

        string Fill(string s) => (s ?? "").Replace("{caller}", _config.Call?.Caller ?? "")
            .Replace("{planet}", _planet).Replace("{ship}", _freighter).Replace("{museum}", _museum);

        void Board()
        {
            _leaving = true;
            _firstPerson.Active = false;
            _state.Prompt.Value = "";
            Leave(_cts.Token).Forget();
        }

        async UniTaskVoid Leave(CancellationToken ct)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.6), cancellationToken: ct);
            Debug.Log($"[Station] to {_config.Next}");
            _scenes.Publish(new SceneChangeRequested(_config.Next));
        }

        /// <summary>The page's layout editor: rebuild from a new plan; back on the old one if it fails.</summary>
        void Rebuild(string planText)
        {
            if (string.IsNullOrWhiteSpace(planText)) return;
            var rows = new List<string>(planText.Replace("\r", "").Split('\n'));
            rows.RemoveAll(string.IsNullOrEmpty);
            var previous = _layout;
            try
            {
                Build(_layout.WithPlan(rows));
                SetLights(_state.Lights.Value);
                var here = StationPlan.CellOf(_player.Body.transform.position);
                if (!StationPlan.Walkable(_station.Plan.At(here))) Teleport(StationPlan.Centre(_station.Plan.Spawn), _config.SpawnYaw);
                Debug.Log($"[Station] rebuilt from the page: {rows.Count} rows");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Station] plan rejected ({e.Message}); keeping the old one");
                Build(previous);
            }
        }

        void SetLights(StationLightMode mode)
        {
            if (mode == StationLightMode.Unspecified) mode = StationLightMode.On;
            _station.SetLights(mode);
            _state.Lights.Value = mode;
        }

        void SetPsx(bool on)
        {
            Shader.SetGlobalFloat(Psx, on ? 1f : 0f);
            _state.Psx.Value = on;
        }

        public void Dispose()
        {
            Shader.SetGlobalFloat(Psx, 0f);
            _cts.Cancel();
            _cts.Dispose();
            foreach (var s in _subscriptions) s.Dispose();
        }
    }
}
