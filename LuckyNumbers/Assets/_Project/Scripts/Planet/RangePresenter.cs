using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Hands;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Dev shooting range: walk to the bench, E picks up a gun (both hands on it), click fires energy bolts at the
    /// crates and targets downrange, E drops it; melee weapons (click swings) and mods lie on the table to the left.
    /// Reach it from the debug inspector (?ln-debug) or ?ln-range; ?ln-range=knife (any model's name) starts you
    /// facing that one, ready to pick up; &ln-slowmo plays at quarter speed (to watch swings and recoil).
    /// V cycles a picture-in-picture view of the hands and what they hold (HandsSideView, shared with the station range).
    /// ?ln-bake picks up every gun and melee weapon in turn, solves each hand's grip live and logs it as a "[Bake]"
    /// JSON line: tools/ln.py bake-grasps runs that headless and writes the grips into weapons.json.
    /// </summary>
    public sealed class RangePresenter : IStartable, IDisposable
    {
        readonly IFirstPerson _firstPerson;
        readonly PlanetView _ui;
        readonly PlayerView _player;
        readonly ArmsView _arms;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _subscription;
        readonly CancellationTokenSource _cts = new();
        HandsSideView _side;

        const string Hint = "E picks up a weapon - click fires / swings - E drops it - V side view";

        [Inject]
        public RangePresenter(IFirstPerson firstPerson, PlanetView ui, PlayerView player, ArmsView arms,
                              ISubscriber<InputActionPerformed> actions)
        {
            _firstPerson = firstPerson;
            _ui = ui;
            _player = player;
            _arms = arms;
            _actions = actions;
        }

        public void Dispose()
        {
            Time.timeScale = 1f;
            _subscription?.Dispose();
            _cts.Cancel();
            ArmsView.UsePresets = true;
        }

        public void Start()
        {
            _ui.Bind();
            _ui.Fade(0f);
            _ui.Dialogue(null, null, 0);
            _ui.MapHint(null);
            _ui.Hint(Hint, 1f);
            _subscription = _actions.Subscribe(e => { if (e.Action == InputAction.SideView) CycleView(); });
            if (SideViewAtStart()) CycleView();
            _firstPerson.Look(0f, 12f);   // facing downrange, the bench in view
            var url = Application.absoluteURL ?? "";
            if (url.Contains("ln-slowmo")) Time.timeScale = 0.25f;
            var start = StartAt();
            if (start != null) Face(start);
            _firstPerson.Active = true;
            if (url.Contains("ln-bake")) Bake(_cts.Token).Forget();
            Debug.Log($"[Range] ready: {Object.FindObjectsByType<WeaponView>().Length} guns, " +
                      $"{Object.FindObjectsByType<MeleeView>().Length} melee{(start != null ? $", at {start.name}" : "")}"); // marker for ln.py webshot
        }

        [Serializable] sealed class BakeLine { public string item; public GraspData left, right; }

        /// <summary>Every gun and melee weapon in turn: pick it up, wait for each hand's grip to settle, log it, put it down.</summary>
        async UniTaskVoid Bake(CancellationToken ct)
        {
            ArmsView.UsePresets = false;   // solve each one fresh
            ArmsView.ClearBakedGrasps();
            var items = new List<(Rigidbody body, int[] hands)>();
            foreach (var gun in Object.FindObjectsByType<WeaponView>()) items.Add((gun.GetComponent<Rigidbody>(), new[] { 0, 1 }));
            foreach (var blade in Object.FindObjectsByType<MeleeView>()) items.Add((blade.GetComponent<Rigidbody>(), new[] { 1 }));
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

        static bool SideViewAtStart() => (Application.absoluteURL ?? "").Contains("ln-sideview");   // start with it on

        /// <summary>Next side view (V).</summary>
        void CycleView()
        {
            _side ??= new HandsSideView(_player, _arms);
            var name = _side.Cycle();
            _ui.Hint($"{Hint} ({name})", 1f);
        }

        /// <summary>The model ?ln-range=name asks to start at (null: the default spot).</summary>
        static Transform StartAt()
        {
            var url = Application.absoluteURL ?? "";
            var at = url.IndexOf("ln-range=", StringComparison.Ordinal);
            if (at < 0) return null;
            var name = url.Substring(at + "ln-range=".Length).Split('&', '#')[0];
            var go = GameObject.Find($"Weapon {Uri.UnescapeDataString(name)}");
            if (go == null) Debug.LogWarning($"[Range] no model \"{name}\" to start at");
            return go != null ? go.transform : null;
        }

        /// <summary>Stand in arm's reach of it (bench: behind it; side table: in front of it), looking down at it.</summary>
        void Face(Transform item)
        {
            var p = item.position;
            var onTable = p.x < -4.5f;
            var stand = onTable ? new Vector3(p.x + 1.0f, 0f, p.z) : new Vector3(p.x, 0f, -1.2f);
            var yaw = onTable ? -90f : 0f;
            var body = _player.Body;
            body.enabled = false;   // a CharacterController ignores transform moves while enabled
            body.transform.SetPositionAndRotation(stand, Quaternion.Euler(0f, yaw, 0f));
            body.enabled = true;
            // Look at it from standing eye height (about 1.65 m): the pickup picks what's within 15 degrees of the look.
            var flat = new Vector2(p.x - stand.x, p.z - stand.z).magnitude;
            _firstPerson.Look(yaw, Mathf.Atan2(1.65f - p.y, flat) * Mathf.Rad2Deg);
        }
    }
}
