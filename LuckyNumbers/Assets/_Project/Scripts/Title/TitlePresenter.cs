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
    /// Title + preloader. Everything shown comes from GameContent.Title (content/title.txtpb):
    /// text, space backdrop, prompts. Starts loading `next` immediately; "any button" switches once ready.
    /// </summary>
    [Presenter]
    public sealed class TitlePresenter : IStartable, IDisposable
    {
        readonly TitleView _view;
        readonly LoadingState _loading;
        readonly ISceneFlow _flow;
        readonly ISubscriber<InputActionPerformed> _input;
        readonly IPublisher<SceneChangeRequested> _sceneRequests;
        readonly CompositeDisposable _disposables = new();

        [Inject]
        public TitlePresenter(TitleView view, LoadingState loading, ISceneFlow flow,
                              ISubscriber<InputActionPerformed> input, IPublisher<SceneChangeRequested> sceneRequests)
        {
            _view = view;
            _loading = loading;
            _flow = flow;
            _input = input;
            _sceneRequests = sceneRequests;
        }

        public void Start()
        {
            var content = GameContent.Title;
            var backdrop = content.Backdrop;

            _view.SetTitle(content.Title);
            backdrop.Stars.ApplyTo(_view.StarfieldMaterial);   // generated from (ln.material)
            backdrop.Planet.ApplyTo(_view.PlanetMaterial);
            _view.Planet.localPosition = backdrop.PlanetPosition.ToVector2();
            _view.Planet.localScale = Vector3.one * backdrop.PlanetScale;

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

            _input.Subscribe(e =>
            {
                if (e.Action == Model.InputAction.AnyButton && _loading.Ready.Value)
                    _sceneRequests.Publish(new SceneChangeRequested(content.Next));
            }).AddTo(_disposables);
        }

        public void Dispose() => _disposables.Dispose();
    }
}
