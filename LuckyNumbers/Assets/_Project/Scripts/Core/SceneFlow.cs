using System;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>Preloads scenes in the background (title doubles as preloader).</summary>
    public interface ISceneFlow
    {
        /// <summary>Start loading <paramref name="scene"/> without activating it; progress goes to LoadingState.</summary>
        void Preload(GameScene scene);
    }

    /// <summary>
    /// Owns scene switching: SceneChangeRequested -> activate the preloaded scene (or load it),
    /// then FlowState.Scene + SceneEntered. Scene asset names match GameScene names (Title, Intro, Planet).
    /// </summary>
    [Presenter]
    public sealed class SceneFlow : ISceneFlow, IStartable, IDisposable
    {
        readonly FlowState _flow;
        readonly LoadingState _loading;
        readonly ISubscriber<SceneChangeRequested> _requests;
        readonly IPublisher<SceneEntered> _entered;
        IDisposable _subscription;
        AsyncOperation _pending;
        GameScene _pendingScene;

        [Inject]
        public SceneFlow(FlowState flow, LoadingState loading,
                         ISubscriber<SceneChangeRequested> requests, IPublisher<SceneEntered> entered)
        {
            _flow = flow;
            _loading = loading;
            _requests = requests;
            _entered = entered;
        }

        public void Start()
        {
            _flow.Scene.Value = Parse(SceneManager.GetActiveScene().name);
            _subscription = _requests.Subscribe(e => Activate(e.Target));
        }

        public void Preload(GameScene scene)
        {
            if (_pending != null) return;
            _loading.Ready.Value = false;
            _loading.Progress.Value = 0f;
            _pendingScene = scene;
            _pending = SceneManager.LoadSceneAsync(scene.ToString());
            _pending.allowSceneActivation = false;
            TrackProgress(_pending).Forget();
        }

        async UniTaskVoid TrackProgress(AsyncOperation op)
        {
            // With activation held back, Unity stops at 0.9 until allowSceneActivation = true.
            while (op.progress < 0.9f)
            {
                _loading.Progress.Value = op.progress / 0.9f;
                await UniTask.Yield();
            }
            _loading.Progress.Value = 1f;
            _loading.Ready.Value = true;
        }

        void Activate(GameScene target)
        {
            if (!Application.CanStreamedLevelBeLoaded(target.ToString()))
            {
                Debug.LogWarning($"[SceneFlow] {target} scene not built yet; back to Title");
                target = GameScene.Title;
            }
            var op = _pending != null && _pendingScene == target ? _pending : SceneManager.LoadSceneAsync(target.ToString());
            _pending = null;
            op.allowSceneActivation = true;
            op.completed += _ =>
            {
                _flow.Scene.Value = target;
                _entered.Publish(new SceneEntered(target));
            };
        }

        static GameScene Parse(string sceneName) =>
            Enum.TryParse(sceneName, out GameScene scene) ? scene : GameScene.Unspecified;

        public void Dispose() => _subscription?.Dispose();
    }
}
