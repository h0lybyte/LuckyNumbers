using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Hands;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The station's shooting range (StationScene.range; the generator's RANGE room, north of comms: door and bench at
    /// its south end). Once the station is built this puts a firing-line bench across the room a few metres in from the
    /// door, lays every weapon on it (the scene's "Range weapons", parked by SceneBuilder), barrels downrange (north),
    /// drops loose crates midway for bolts to push about, and stands target boards at the north wall. While you're in the room RangeState says so and counts your
    /// shots and the hits on its targets (WeaponView.Fired / BoltHit); V cycles the hands side view. Crates and boards
    /// The bench, crates and boards are placeholder blocks (PLACEHOLDER_range_*) until they have models. The start of a
    /// practice mini-game.
    /// Dev / tools (the page's URL): ?ln-range opens the station at the firing line (?ln-range=knife: facing that
    /// weapon, ready to pick up); &amp;ln-slowmo plays at quarter speed (swings, recoil); &amp;ln-sideview starts with the
    /// side view on; &amp;ln-bake picks up every gun and melee weapon in turn, solves each hand's grip live and logs it as
    /// a "[Bake]" JSON line (tools/ln.py bake-grasps runs that headless and writes the grips into weapons.json).
    /// </summary>
    public sealed class RangeRoom : IDisposable
    {
        const string WeaponsRoot = "Range weapons";

        readonly StationView _station;
        readonly PlayerView _player;
        readonly RangeState _state;
        readonly HandsSideView _side;
        readonly HashSet<Collider> _targets = new();
        Transform _root;
        Rect _area;          // the room on the ground plane (x, z), metres
        bool _built;
        Action<Vector3, float, float> _teleport;
        IDisposable _warpSubscription;
        readonly IFirstPerson _firstPerson;
        readonly ArmsView _arms;
        readonly CancellationTokenSource _cts = new();
        float _builtAt = -1f;
        bool _urlDone;

        public RangeRoom(StationView station, PlayerView player, ArmsView arms, RangeState state,
                         MessagePipe.ISubscriber<DevWarpRequested> warps, Action<Vector3, float, float> teleport,
                         IFirstPerson firstPerson)
        {
            _firstPerson = firstPerson;
            _arms = arms;
            _teleport = teleport;
            // Dev (the page's inspector): "range" stands you at the firing line facing downrange, looking down at the
            // bench; "range:aim" the same, looking level at the middle target.
            _warpSubscription = MessagePipe.SubscriberExtensions.Subscribe(warps, e =>
            {
                if ((e.To != "range" && e.To != "range:aim") || !_built) return;
                var pitch = e.To == "range:aim" ? 0f : 28f;
                _teleport?.Invoke(new Vector3(_area.center.x, _player.Body.transform.position.y, _area.yMin + 1.4f), 0f, pitch);
                Debug.Log("[Range] dev warp to the firing line");
            });
            _station = station;
            _player = player;
            _state = state;
            _side = arms != null ? new HandsSideView(player, arms) : null;
            WeaponView.Fired += OnFired;
            WeaponView.BoltHit += OnBoltHit;
        }

        public bool Inside => _state.Inside.Value;

        /// <summary>Lay the range out in the room named `config.room` of the layout just built (no room: nothing).</summary>
        public void Build(Model.StationRange config, StationLayout layout)
        {
            Clear();
            if (config == null || string.IsNullOrEmpty(config.Room)) return;
            var room = layout.Rooms.Find(r => r.Name == config.Room);
            if (room == null) return;
            var plan = _station.Plan;
            var centre = plan.PlanCell(room.Col, room.Row);
            List<Vector2Int> cells = null;
            foreach (var c in plan.Rooms()) if (c.Contains(centre)) { cells = c; break; }
            if (cells == null || cells.Count == 0) return;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var c in cells)
            {
                minX = Math.Min(minX, c.x); maxX = Math.Max(maxX, c.x);
                minY = Math.Min(minY, c.y); maxY = Math.Max(maxY, c.y);
            }
            var cell = StationPlan.Cell;
            _area = Rect.MinMaxRect(minX * cell, minY * cell, (maxX + 1) * cell, (maxY + 1) * cell);
            _root = new GameObject("Range").transform;
            _built = true;

            // The firing line: a bench across the room, 2.4 m in from the south (door) wall; every weapon on it.
            var benchZ = _area.yMin + 2.4f;
            var benchWidth = Mathf.Max(2f, _area.width - 1.2f);
            Block("PLACEHOLDER_range_bench", new Vector3(_area.center.x, 0.42f, benchZ), new Vector3(benchWidth, 0.84f, 0.7f),
                  new Color(0.32f, 0.24f, 0.16f));
            LayWeapons(new Vector3(_area.center.x, 0.84f, benchZ), benchWidth);

            // Loose crates midway (bolts push them), then the target boards at the north wall.
            var rng = new Unity.Mathematics.Random(0x5A7E1u);
            var mid = _area.yMin + _area.height * 0.55f;
            for (var i = 0; i < Math.Max(0, config.Crates); i++)
            {
                var size = rng.NextFloat(0.4f, 0.65f);
                var x = Mathf.Lerp(_area.xMin + 1.2f, _area.xMax - 1.2f, rng.NextFloat());
                var z = mid + rng.NextFloat(-1.5f, 1.5f);
                var crate = Block("PLACEHOLDER_range_crate", new Vector3(x, size * 0.5f + 0.02f + (i % 2) * 0.7f, z),
                                  Vector3.one * size, new Color(0.62f, 0.42f, 0.16f));
                var body = crate.AddComponent<Rigidbody>();
                body.mass = 2f;
            }
            var count = Math.Max(1, config.Targets);
            for (var i = 0; i < count; i++)
            {
                var x = Mathf.Lerp(_area.xMin, _area.xMax, (i + 1f) / (count + 1f));
                var z = _area.yMax - 0.9f;
                Block("PLACEHOLDER_range_target_post", new Vector3(x, 0.6f, z), new Vector3(0.12f, 1.2f, 0.12f), new Color(0.3f, 0.32f, 0.36f));
                var board = Block("PLACEHOLDER_range_target", new Vector3(x, 1.6f, z), new Vector3(1.1f, 1.1f, 0.08f), new Color(0.85f, 0.22f, 0.16f));
                _targets.Add(board.GetComponent<Collider>());
            }
            _state.Hint.Value = config.Hint ?? "";
            _builtAt = Time.time;   // the URL's range options run once the layout has settled (Tick)
            Debug.Log($"[Range] {room.Name}: {_area.width:0}x{_area.height:0} m, {count} targets, {config.Crates} crates"); // marker for ln.py webshot
        }

        /// <summary>Every range weapon along the bench (top centre `bench`), barrels north (downrange), a hair above.</summary>
        static void LayWeapons(Vector3 bench, float width)
        {
            var root = GameObject.Find(WeaponsRoot)?.transform;
            if (root == null) { Debug.LogWarning("[Range] no range weapons in the scene (SceneBuilder)"); return; }
            var items = new List<Transform>();
            foreach (Transform t in root) items.Add(t);
            var step = (width - 0.6f) / Math.Max(1, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                var at = new Vector3(bench.x - (width - 0.6f) / 2f + step * (i + 0.5f), bench.y + 0.12f, bench.z);
                var item = items[i];
                item.gameObject.SetActive(true);
                item.SetPositionAndRotation(at, Quaternion.identity);   // +Z down the barrel = north, downrange
                if (item.TryGetComponent<Rigidbody>(out var body))
                {
                    body.position = at;
                    body.rotation = Quaternion.identity;
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
        }

        GameObject Block(string name, Vector3 at, Vector3 size, Color color)
        {
            var go = LuckyNumbers.Core.Placeholders.Block(name, size, color);   // until it has a model
            go.transform.SetParent(_root, false);
            go.transform.position = at;
            return go;
        }

        /// <summary>Inside the room? Entering starts the count again.</summary>
        public void Tick()
        {
            if (!_urlDone && _built && _builtAt >= 0f && Time.time - _builtAt > 1.5f) FromUrl();
            if (!_built) { if (_state.Inside.Value) _state.Inside.Value = false; return; }
            var p = _player.Body.transform.position;
            var inside = _area.Contains(new Vector2(p.x, p.z));
            if (inside == _state.Inside.Value) return;
            _state.Inside.Value = inside;
            if (inside) Reset();
            else _side?.Off();
        }

        public void Reset()
        {
            _state.Shots.Value = 0;
            _state.Hits.Value = 0;
        }

        /// <summary>V in the range: the hands side view.</summary>
        public void CycleSideView()
        {
            if (_side != null && _state.Inside.Value) _side.Cycle();
        }

        void OnFired(WeaponView gun)
        {
            if (_state.Inside.Value) _state.Shots.Value++;
        }

        void OnBoltHit(WeaponView gun, RaycastHit hit)
        {
            if (!_state.Inside.Value || hit.collider == null || !_targets.Contains(hit.collider)) return;
            _state.Hits.Value++;
            Debug.Log($"[Range] hit {hit.collider.name} ({_state.Hits.Value}/{_state.Shots.Value})");
        }

        // ---- dev / tools (URL) -------------------------------------------------------------------------------

        static string Url => Application.absoluteURL ?? "";

        /// <summary>?ln-range (and its options): to the firing line, or a weapon; slow motion, side view, bake.</summary>
        void FromUrl()
        {
            _urlDone = true;
            var url = Url;
            if (!url.Contains("ln-range")) return;
            if (url.Contains("ln-slowmo")) Time.timeScale = 0.25f;
            var start = StartAt();
            if (start != null) Face(start);
            else _teleport?.Invoke(new Vector3(_area.center.x, _player.Body.transform.position.y, _area.yMin + 1.4f), 0f, 28f);
            if (url.Contains("ln-sideview")) _side?.Cycle();
            var root = GameObject.Find(WeaponsRoot)?.transform;
            Debug.Log($"[Range] ready: {(root != null ? root.GetComponentsInChildren<WeaponView>(true).Length : 0)} guns, " +
                      $"{(root != null ? root.GetComponentsInChildren<MeleeView>(true).Length : 0)} melee" +
                      $"{(start != null ? $", at {start.name}" : "")}");   // marker for ln.py webshot / bake-grasps
            if (url.Contains("ln-bake")) Bake(_cts.Token).Forget();
        }

        /// <summary>The weapon ?ln-range=name asks to start at (null: the firing line).</summary>
        static Transform StartAt()
        {
            var url = Url;
            var at = url.IndexOf("ln-range=", StringComparison.Ordinal);
            if (at < 0) return null;
            var name = Uri.UnescapeDataString(url.Substring(at + "ln-range=".Length).Split('&', '#')[0]);
            var go = GameObject.Find($"{WeaponsRoot}/Weapon {name}");
            if (go == null) Debug.LogWarning($"[Range] no weapon \"{name}\" to start at");
            return go != null ? go.transform : null;
        }

        /// <summary>Behind the bench in arm's reach of it, looking down at it (pickup takes what's within 15 degrees).</summary>
        void Face(Transform item)
        {
            var p = item.position;
            var stand = new Vector3(p.x, _player.Body.transform.position.y, p.z - 1.2f);
            var flat = new Vector2(p.x - stand.x, p.z - stand.z).magnitude;
            _teleport?.Invoke(stand, 0f, Mathf.Atan2(1.65f - p.y, flat) * Mathf.Rad2Deg);
        }

        [Serializable] sealed class BakeLine { public string item; public GraspData left, right; }

        /// <summary>Every range gun and melee weapon in turn: pick it up, wait for each hand's grip to settle, log it, put it down.</summary>
        async UniTaskVoid Bake(CancellationToken ct)
        {
            var root = GameObject.Find(WeaponsRoot)?.transform;
            if (root == null || _firstPerson == null || _arms == null) { Debug.LogWarning("[Bake] nothing to bake with"); return; }
            ArmsView.UsePresets = false;   // solve each one fresh
            ArmsView.ClearBakedGrasps();
            var items = new List<(Rigidbody body, int[] hands)>();
            foreach (var gun in root.GetComponentsInChildren<WeaponView>()) items.Add((gun.GetComponent<Rigidbody>(), new[] { 0, 1 }));
            foreach (var blade in root.GetComponentsInChildren<MeleeView>()) items.Add((blade.GetComponent<Rigidbody>(), new[] { 1 }));
            var baked = 0;
            foreach (var (body, hands) in items)
            {
                var name = body.name.StartsWith("Weapon ") ? body.name.Substring("Weapon ".Length) : body.name;
                Face(body.transform);
                await UniTask.Delay(400, cancellationToken: ct);
                if (!_firstPerson.PickUp(body)) { Debug.LogWarning($"[Bake] couldn't pick up {name}"); continue; }
                var waited = 0f;
                while (waited < 15f && (_firstPerson.Held != body || Array.Exists(hands, s => _arms.BakedGrip(s) == null)))
                {
                    await UniTask.Yield(ct);
                    waited += Time.unscaledDeltaTime;
                }
                if (_firstPerson.Held == body && !Array.Exists(hands, s => _arms.BakedGrip(s) == null))
                {
                    var line = new BakeLine { item = name };
                    foreach (var s in hands) if (s == 0) line.left = _arms.BakedGrip(0); else line.right = _arms.BakedGrip(1);
                    Debug.Log("[Bake] " + JsonUtility.ToJson(line));
                    baked++;
                }
                else Debug.LogWarning($"[Bake] {name}: the grip never settled (15 s)");
                _firstPerson.Drop();
                await UniTask.Delay(600, cancellationToken: ct);
            }
            ArmsView.UsePresets = true;
            Debug.Log($"[Bake] done: {baked} of {items.Count}");
        }

        void Clear()
        {
            _targets.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
            _built = false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            Time.timeScale = 1f;
            ArmsView.UsePresets = true;
            _warpSubscription?.Dispose();
            WeaponView.Fired -= OnFired;
            WeaponView.BoltHit -= OnBoltHit;
            _side?.Off();
            _state.Inside.Value = false;
            Clear();
        }
    }
}
