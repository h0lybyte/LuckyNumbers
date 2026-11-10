using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using MessagePipe;
using R3;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Title
{
    /// <summary>The hangar, between character creation and the launch: pick your fighter's skin.</summary>
    public interface IShipPicker
    {
        /// <summary>Shows the fighter and the page's picker; completes on its Launch (at once with no page).</summary>
        UniTask RunAsync(CancellationToken ct);
    }

    /// <summary>
    /// Your fighter turning in front of the title camera (GameContent.Title.Hangar), every skin it has one step
    /// away: the page's picker (ShipPicker state) steps through them (ShipSkinStepRequested) and launches
    /// (ShipPickDone). Each step is kept in PlayerShip, so the intro, station and planet fly that skin.
    /// </summary>
    [Presenter]
    public sealed class ShipPickerPresenter : IShipPicker, IStartable, IDisposable
    {
        readonly ShipView _ship;
        readonly ISpaceDirector _space;
        readonly PlayerShip _playerShip;
        readonly ShipPicker _state;
        readonly ISubscriber<ShipPickerReady> _pageReady;
        readonly ISubscriber<ShipSkinStepRequested> _step;
        readonly ISubscriber<ShipPickDone> _done;
        readonly CompositeDisposable _subscriptions = new();
        UniTaskCompletionSource _pick;
        bool _hasPage;

        [Inject]
        public ShipPickerPresenter(ShipView ship, ISpaceDirector space, PlayerShip playerShip, ShipPicker state,
                                   ISubscriber<ShipPickerReady> pageReady, ISubscriber<ShipSkinStepRequested> step,
                                   ISubscriber<ShipPickDone> done)
        {
            _ship = ship;
            _space = space;
            _playerShip = playerShip;
            _state = state;
            _pageReady = pageReady;
            _step = step;
            _done = done;
        }

        public void Start()
        {
            _ship.Hide();
            _state.Open.Value = false;
            _state.Count.Value = _ship.SkinCount;
            _subscriptions.Add(_pageReady.Subscribe(e => _hasPage = e.On));
            _subscriptions.Add(_step.Subscribe(e => Step(e.Step)));
            _subscriptions.Add(_done.Subscribe(_ => Finish()));
        }

        public async UniTask RunAsync(CancellationToken ct)
        {
            if (!_hasPage || _ship.SkinCount == 0) return;   // no picker on the page: keep the skin you have
            var skin = PlayerShipSkin.Current(_playerShip);
            var index = 0;
            for (var i = 0; i < _ship.SkinCount; i++) if (_ship.SkinAt(i) == skin) index = i;
            Select(index);
            _pick?.TrySetCanceled();
            _pick = new UniTaskCompletionSource();
            _state.Open.Value = true;
            Debug.Log($"[Hangar] open: {_ship.SkinCount} skins, {skin}");
            using (ct.Register(Finish))
                await PoseAsync(ct);   // until the pick, and the slide out
            Debug.Log($"[Hangar] launch: {_playerShip.Skin.Value}");
        }

        void Step(int step)
        {
            if (!_state.Open.Value || _ship.SkinCount == 0) return;
            Select(((_state.Index.Value + step) % _ship.SkinCount + _ship.SkinCount) % _ship.SkinCount);
        }

        void Select(int index)
        {
            var skin = _ship.SkinAt(index);
            _playerShip.Skin.Value = skin;
            _state.Index.Value = index;
            _state.Skin.Value = skin;
            _ship.Show(skin, GameContent.Title.Hangar?.ShipLength ?? 1f);
        }

        void Finish()
        {
            _state.Open.Value = false;
            _pick?.TrySetResult();
            _pick = null;
        }

        /// <summary>Keeps the ship turning in front of the camera (which still orbits) until the pick; slides it in and out.</summary>
        async UniTask PoseAsync(CancellationToken ct)
        {
            var h = GameContent.Title.Hangar ?? new ShipHangar { ShipLength = 1f, Distance = 2.4f };
            var enter = math.max(0.01f, h.EnterSeconds);
            var angle = 200f;   // starts three-quarter on, nose to the left
            var shown = 0f;
            while (!ct.IsCancellationRequested)
            {
                var leaving = !_state.Open.Value;
                if (leaving && shown <= 0f) break;
                shown = math.saturate(shown + (leaving ? -1f : 1f) * Time.deltaTime / enter);
                angle += h.SpinDegreesPerSecond * Time.deltaTime;
                var cam = _space.Camera.transform;
                var e = math.smoothstep(0f, 1f, shown);
                // Slides up from below the view as it appears, back down as it goes.
                var below = cam.up * (h.ShipLength * (h.Drop + (1f - e) * 1.5f));
                var at = cam.position + cam.forward * h.Distance - below;
                var facing = Quaternion.LookRotation(-cam.forward, cam.up);   // nose toward the camera at 0
                _ship.Pose(at, facing * Quaternion.AngleAxis(h.TiltDegrees, Vector3.right) * Quaternion.AngleAxis(angle, Vector3.up));
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            }
            _ship.Hide();
        }

        public void Dispose()
        {
            _pick?.TrySetCanceled();
            _subscriptions.Dispose();
        }
    }
}
