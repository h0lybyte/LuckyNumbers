using System;
using LuckyNumbers.Core;
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
    /// the universe behind it is SpacePresenter's. Preloads `next`; Play switches once loaded.
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
        readonly CompositeDisposable _disposables = new();
        bool _settingsOpen;

        [Inject]
        public TitlePresenter(TitleView view, LoadingState loading, ISceneFlow flow,
                              ISubscriber<InputActionPerformed> input,
                              IPublisher<MenuItemSelected> menuPublisher, ISubscriber<MenuItemSelected> menu,
                              IPublisher<SceneChangeRequested> sceneRequests)
        {
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
            }).AddTo(_disposables);

            // Keyboard/gamepad: Confirm = Play, Cancel = close settings.
            _input.Subscribe(e =>
            {
                if (e.Action == Model.InputAction.Confirm && !_settingsOpen) Play(content.Next);
                if (e.Action == Model.InputAction.Cancel && _settingsOpen) CloseSettings();
            }).AddTo(_disposables);
        }

        void Play(GameScene next) =>
            // Waits for the preload if Play is pressed early.
            _loading.Ready.Where(ready => ready).Take(1)
                .Subscribe(_ => _sceneRequests.Publish(new SceneChangeRequested(next)))
                .AddTo(_disposables);

        void CloseSettings()
        {
            _settingsOpen = false;
            _view.ShowSettings(false);
        }

        public void Dispose() => _disposables.Dispose();
    }
}
