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
    /// toward home and hands its pose to the next scene (CameraHandoff) so the switch has no visible cut. Before that,
    /// Play runs character creation (ICharacterSetup: the page's sheet, until Ready), then the hangar (IShipPicker:
    /// your fighter's skin, until Launch). First of all Play asks which save slot (ISaveSlots): a filled one skips
    /// creation and the hangar, loads, and leaves once its universe has formed again (title.continue_seconds).
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
        readonly ICharacterSetup _character;
        readonly IShipPicker _hangar;
        readonly ISaveSlots _saves;
        readonly GameSettings _settings;
        readonly CompositeDisposable _disposables = new();
        readonly System.Threading.CancellationTokenSource _cts = new();
        bool _settingsOpen, _leaving;
        float _uiAlpha = 1f;   // the title UI's fade (the hangar hides it; the launch fades whatever's left)

        [Inject]
        public TitlePresenter(TitleView view, LoadingState loading, ISceneFlow flow,
                              ISubscriber<InputActionPerformed> input,
                              IPublisher<MenuItemSelected> menuPublisher, ISubscriber<MenuItemSelected> menu,
                              IPublisher<SceneChangeRequested> sceneRequests,
                              ISpaceDirector space, CameraHandoff handoff, ICharacterSetup character,
                              IShipPicker hangar, ISaveSlots saves, GameSettings settings)
        {
            _settings = settings;
            _saves = saves;
            _hangar = hangar;
            _character = character;
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
            BindSettings(content.Settings);
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
            PlayAsync(next).Forget();
        }

        async UniTaskVoid PlayAsync(GameScene next)
        {
            var ct = _cts.Token;
            // Which save slot (the page's picker; none without a page: an unsaved run).
            var pick = _saves.PickAsync(ct);
            if (pick.Status == UniTaskStatus.Pending) await FadeUiAsync(0f, 0.3f, ct);
            var slot = await pick;
            if (slot.Back)
            {
                await FadeUiAsync(1f, 0.3f, ct);
                _leaving = false;
                return;
            }
            if (slot.Continue)
            {
                _saves.Load(slot.Index);                                            // your universe forms again,
                await UniTask.Delay(TimeSpan.FromSeconds(Mathf.Max(0f, GameContent.Title.ContinueSeconds)), cancellationToken: ct);
            }
            else
            {
                await _character.RunAsync(ct);                                      // character creation
                var hangar = _hangar.RunAsync(ct);                                  // then your fighter's skin,
                if (hangar.Status == UniTaskStatus.Pending) await FadeUiAsync(0f, 0.3f, ct);   // the title UI out of its way
                await hangar;
                _saves.Begin(slot.Index);
            }
            await _loading.Ready.Where(ready => ready).FirstAsync(ct);              // waits for the preload if early
            LeaveAsync(next).Forget();
        }

        /// <summary>UI fades while the camera eases toward the planet it orbits; then the pose is handed to the next scene.</summary>
        async UniTaskVoid LeaveAsync(GameScene next)
        {
            var content = GameContent.Title;
            var duration = math.max(0.01f, content.PlayTransitionSeconds);
            var cam = _space.Camera.transform;
            _space.TakeCamera();
            _space.TryGetBody(_space.Focus, out var home0, out _);   // the crash site the title orbits
            var offset = cam.position - (Vector3)home0;     // relative to the (moving) planet
            var startRot = cam.rotation;
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                var e = math.smoothstep(0f, 1f, t / duration);
                _space.TryGetBody(_space.Focus, out var home, out _);
                var pos = (Vector3)home + offset * (1f - content.PlayPushIn * e);
                var look = Quaternion.LookRotation((Vector3)home - pos, startRot * Vector3.up);
                cam.SetPositionAndRotation(pos, Quaternion.Slerp(startRot, look, e * 0.35f));
                _view.SetUiAlpha(_uiAlpha * (1f - math.smoothstep(0f, 0.6f, t / duration)));
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

        async UniTask FadeUiAsync(float to, float seconds, System.Threading.CancellationToken ct)
        {
            var from = _uiAlpha;
            for (var t = 0f; t < seconds && !ct.IsCancellationRequested; t += Time.deltaTime)
            {
                _uiAlpha = math.lerp(from, to, math.smoothstep(0f, 1f, t / seconds));
                _view.SetUiAlpha(_uiAlpha);
                await UniTask.Yield();
            }
            _uiAlpha = to;
            _view.SetUiAlpha(to);
        }

        /// <summary>The Settings panel edits GameSettings (Core/SettingsPresenter saves and applies each change).</summary>
        void BindSettings(SettingsPanel labels)
        {
            var s = _settings;
            _view.SetSettings(labels, SettingsPresenter.MinSensitivity, SettingsPresenter.MaxSensitivity,
                onSensitivity: v => s.LookSensitivity.Value = Mathf.Round(v * 20f) / 20f,   // 0.05 steps
                onInvert: () => s.InvertY.Value = !s.InvertY.Value,
                onQuality: i => s.Quality.Value = RenderQuality.Low + i,
                onFps: () => s.ShowFps.Value = !s.ShowFps.Value,
                onReset: () => SettingsPresenter.Reset(s),
                onBack: CloseSettings);
            Observable.CombineLatest(s.LookSensitivity, s.InvertY, s.Quality, s.ShowFps,
                    (look, invert, quality, fps) => (look, invert, quality, fps))
                .Subscribe(v => _view.ShowSettingsValues(v.look, v.invert, (int)v.quality - (int)RenderQuality.Low, v.fps))
                .AddTo(_disposables);
        }

        /// <summary>Close whichever panel is open (Settings or Credits).</summary>
        void CloseSettings()
        {
            _settingsOpen = false;
            _view.ShowSettings(false);
            _view.ShowCredits(false);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _disposables.Dispose();
        }
    }
}
