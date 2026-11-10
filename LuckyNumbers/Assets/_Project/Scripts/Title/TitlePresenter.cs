using System;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Space;
using Unity.Mathematics;
using LuckyNumbers.Model;
using MessagePipe;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Title
{
    /// <summary>
    /// Title + preloader. Text, menu and settings come from GameContent.Title (content/title.txtpb);
    /// the universe behind it is SpacePresenter's. Preloads `next`; Play fades the UI, pushes the camera
    /// toward home and hands its pose to the next scene (CameraHandoff) so the switch has no visible cut.
    /// </summary>
    [Presenter]
    public sealed class TitlePresenter : IStartable, IDisposable
    {
        readonly TitleView _view;
        readonly LoadingState _loading;
        readonly ISceneFlow _flow;
        readonly ISubscriber<InputActionPerformed> _input;
        readonly IPublisher<MenuItemSelected> _menuPublisher;
        readonly ISubscriber<MenuItemSelected> _menu;
        readonly IPublisher<SceneChangeRequested> _sceneRequests;
        readonly ISpaceDirector _space;
        readonly CameraHandoff _handoff;
        readonly CompositeDisposable _disposables = new();
        bool _settingsOpen, _leaving;

        [Inject]
        public TitlePresenter(TitleView view, LoadingState loading, ISceneFlow flow,
                              ISubscriber<InputActionPerformed> input,
                              IPublisher<MenuItemSelected> menuPublisher, ISubscriber<MenuItemSelected> menu,
                              IPublisher<SceneChangeRequested> sceneRequests,
                              ISpaceDirector space, CameraHandoff handoff)
        {
            _space = space;
            _handoff = handoff;
            _view = view;
            _loading = loading;
            _flow = flow;
            _input = input;
            _menuPublisher = menuPublisher;
            _menu = menu;
            _sceneRequests = sceneRequests;
        }

        public void Start()
        {
            var content = GameContent.Title;
            _view.Bind();
            _view.SetTitle(content.Title);
            foreach (var item in content.Menu)
            {
                var action = item.Action;
                _view.AddMenuButton(item.Label, () => _menuPublisher.Publish(new MenuItemSelected(action)));
            }
            _view.SetSettings(content.Settings.Title, content.Settings.Body, content.Settings.BackLabel, CloseSettings);
            _view.ShowSettings(false);
            _view.SetCredits(GameContent.Credits, CloseSettings, url => { Debug.Log($"[Title] open {url}"); Application.OpenURL(url); });
            _view.ShowCredits(false);

            _flow.Preload(content.Next);
            _loading.Ready
                .Subscribe(ready =>
                {
                    _view.SetPrompt(ready ? content.StartPrompt : content.LoadingPrompt);
                    if (ready) Debug.Log($"[Title] ready: {content.Next} preloaded"); // marker for ln.py webshot
                })
                .AddTo(_disposables);

            var period = Mathf.Max(0.1f, content.PromptPulseSeconds);
            Observable.EveryUpdate()
                .Subscribe(_ => _view.SetPromptAlpha(0.55f + 0.45f * Mathf.Cos(Time.time * 2f * Mathf.PI / period)))
                .AddTo(_disposables);

            _menu.Subscribe(e =>
            {
                Debug.Log($"[Title] menu: {e.Action}");
                if (e.Action == MenuAction.Play) Play(content.Next);
                else if (e.Action == MenuAction.Settings) { _settingsOpen = true; _view.ShowSettings(true); }
                else if (e.Action == MenuAction.Credits) { _settingsOpen = true; _view.ShowCredits(true); }
            }).AddTo(_disposables);

            // Keyboard/gamepad: Confirm = Play, Cancel = close the open panel.
            _input.Subscribe(e =>
            {
                if (e.Action == Model.InputAction.Confirm && !_settingsOpen) Play(content.Next);
                if (e.Action == Model.InputAction.Cancel && _settingsOpen) CloseSettings();
            }).AddTo(_disposables);
        }

        void Play(GameScene next)
        {
            if (_leaving) return;
            _leaving = true;
            // Waits for the preload if Play is pressed early.
            _loading.Ready.Where(ready => ready).Take(1)
                .Subscribe(_ => LeaveAsync(next).Forget())
                .AddTo(_disposables);
        }

        /// <summary>UI fades while the camera eases toward home; then the pose is handed to the next scene.</summary>
        async UniTaskVoid LeaveAsync(GameScene next)
        {
            var content = GameContent.Title;
            var duration = math.max(0.01f, content.PlayTransitionSeconds);
            var cam = _space.Camera.transform;
            _space.TakeCamera();
            _space.TryGetBody(content.Backdrop.Camera.Target, out var home0, out _);
            var offset = cam.position - (Vector3)home0;     // relative to the (moving) planet
            var startRot = cam.rotation;
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                var e = math.smoothstep(0f, 1f, t / duration);
                _space.TryGetBody(content.Backdrop.Camera.Target, out var home, out _);
                var pos = (Vector3)home + offset * (1f - content.PlayPushIn * e);
                var look = Quaternion.LookRotation((Vector3)home - pos, startRot * Vector3.up);
                cam.SetPositionAndRotation(pos, Quaternion.Slerp(startRot, look, e * 0.35f));
                _view.SetUiAlpha(1f - math.smoothstep(0f, 0.6f, t / duration));
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);
            }
            _view.SetUiAlpha(0f);
            _handoff.Position.Value = new Vec3(cam.position.x, cam.position.y, cam.position.z);
            var f = cam.forward;
            var u = cam.up;
            _handoff.Forward.Value = new Vec3(f.x, f.y, f.z);
            _handoff.Up.Value = new Vec3(u.x, u.y, u.z);
            _handoff.Valid.Value = true;
            _sceneRequests.Publish(new SceneChangeRequested(next));
        }

        /// <summary>Close whichever panel is open (Settings or Credits).</summary>
        void CloseSettings()
        {
            _settingsOpen = false;
            _view.ShowSettings(false);
            _view.ShowCredits(false);
        }

        public void Dispose() => _disposables.Dispose();
    }
}
