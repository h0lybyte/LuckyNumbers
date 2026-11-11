using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using UnityEngine.SceneManagement;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>Fetches the next scene's assets in the background (title doubles as preloader).</summary>
    public interface ISceneFlow
    {
        /// <summary>Start fetching what <paramref name="scene"/> needs; progress goes to LoadingState.</summary>
        void Preload(GameScene scene);
    }

    /// <summary>
    /// Owns scene switching: SceneChangeRequested -> load the scene, then FlowState.Scene + SceneEntered. Scene asset
    /// names match GameScene names (Title, Intro, Planet).
    /// Preload never holds a scene load at allowSceneActivation = false: a held load blocks every async load queued
    /// after it, Addressables included, so anything loading meanwhile hung (and a second scene request did too).
    /// Instead it downloads the bundles labelled with the scene's name (AssetPrefetch), and the switch is a plain load
    /// of a light scene whose heavy assets come from those bundles.
    /// </summary>
    [Presenter]
    public sealed class SceneFlow : ISceneFlow, IStartable, IDisposable
    {
        readonly FlowState _flow;
        readonly LoadingState _loading;
        readonly ISubscriber<SceneChangeRequested> _requests;
        readonly IPublisher<SceneEntered> _entered;
        readonly CancellationTokenSource _disposed = new();
        IDisposable _subscription;
        GameScene _prefetching = GameScene.Unspecified;
        AsyncOperation _switching;

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
            if (_prefetching == scene) return;
            _prefetching = scene;
            _loading.Ready.Value = false;
            _loading.Progress.Value = 0f;
            Prefetch(scene).Forget();
        }

        async UniTaskVoid Prefetch(GameScene scene)
        {
            try
            {
                await AssetPrefetch.RunAsync(scene.ToString(), p =>
                {
                    if (_prefetching == scene) _loading.Progress.Value = p;
                }, _disposed.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Debug.LogWarning($"[SceneFlow] prefetch {scene}: {e.Message}"); }
            if (_prefetching != scene) return;   // a newer Preload took over
            _loading.Progress.Value = 1f;
            _loading.Ready.Value = true;
        }

        void Activate(GameScene target)
        {
            if (_switching != null)
            {
                Debug.LogWarning($"[SceneFlow] already switching scenes; {target} request dropped");
                return;
            }
            if (!Application.CanStreamedLevelBeLoaded(target.ToString()))
            {
                Debug.LogWarning($"[SceneFlow] {target} scene not built yet; back to Title");
                target = GameScene.Title;
            }
            _prefetching = GameScene.Unspecified;
            _switching = SceneManager.LoadSceneAsync(target.ToString());
            _switching.completed += _ =>
            {
                _switching = null;
                _flow.Scene.Value = target;
                _entered.Publish(new SceneEntered(target));
            };
        }

        static GameScene Parse(string sceneName) =>
            Enum.TryParse(sceneName, out GameScene scene) ? scene : GameScene.Unspecified;

        public void Dispose()
        {
            _disposed.Cancel();
            _subscription?.Dispose();
        }
    }
}
