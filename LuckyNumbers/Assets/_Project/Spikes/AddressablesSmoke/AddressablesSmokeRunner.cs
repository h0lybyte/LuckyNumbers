using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Spikes.AddressablesSmoke
{
    /// <summary>
    /// SPIKE: delete the AddressablesSmoke folder (and its rule in Editor/AddressablesSetup) once real assets use
    /// AssetScope. Opt-in like the other spikes: page URL contains "tests". Checks the lifecycle on the real build:
    /// a label prefetch that blocks nothing, load from a bundle, one handle per key, a pool that warms over frames and doesn't grow when reused, dispose
    /// destroying every copy, a reload after release, and a missing key failing with its name.
    /// Logs one line: "[AddressablesSmoke] PASS ..." or "[AddressablesSmoke] FAIL ...".
    /// </summary>
    public static class AddressablesSmokeRunner
    {
        const int Prewarm = 8, PerFrame = 2, Rounds = 1000;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!(Application.absoluteURL ?? "").Contains("tests")) return;
            Run().Forget();
        }

        static async UniTaskVoid Run()
        {
            var fails = new List<string>();
            void Step(string what) => Debug.Log("[Smoke] " + what);
            void Check(bool ok, string what) { if (!ok) fails.Add(what); }
            try
            {
                Step("start");
                var last = -1f;
                await AssetPrefetch.RunAsync("Station", p => last = p);
                Check(last >= 1f, $"prefetch of the Station label finished (progress {last:F2})");
                Step("prefetched");
                var scope = new AssetScope();
                var t0 = Time.realtimeSinceStartup;
                var template = await scope.LoadAsync(AssetKeys.SpikeSmokeCube);
                Step("loaded");
                var loadMs = (Time.realtimeSinceStartup - t0) * 1000f;
                Check(template != null && template.name == "SmokeCube", "template loaded");
                Check(await scope.LoadAsync(AssetKeys.SpikeSmokeCube) == template, "second load shares the asset");
                Check(scope.Count == 1, "one handle per key");

                Step("shared");
                var frame0 = Time.frameCount;
                var pool = await scope.PoolAsync(AssetKeys.SpikeSmokeCube, Prewarm, PerFrame);
                var warmFrames = Time.frameCount - frame0;
                Check(pool.Created == Prewarm && pool.Free == Prewarm, $"prewarm made {Prewarm} (got {pool.Created})");
                Check(warmFrames >= Prewarm / PerFrame - 1, $"prewarm spread over frames (took {warmFrames})");

                Step("warmed");
                var out_ = new GameObject[Prewarm];
                for (var round = 0; round < Rounds; round++)
                {
                    for (var i = 0; i < Prewarm; i++) out_[i] = pool.Get(new Vector3(i, 0, 0), Quaternion.identity);
                    for (var i = 0; i < Prewarm; i++) pool.Release(out_[i]);
                }
                Check(pool.Created == Prewarm, $"{Rounds} rounds reuse, no growth (created {pool.Created})");
                var held = pool.Get();
                Check(held.activeInHierarchy, "a Get is awake");

                Step("reused");
                scope.Dispose();
                await UniTask.Yield();   // Destroy lands at end of frame
                Check(held == null, "dispose destroyed copies still out");
                Check(scope.Count == 0, "dispose released every handle");
                var threw = false;
                try { await scope.LoadAsync(AssetKeys.SpikeSmokeCube); }
                catch (ObjectDisposedException) { threw = true; }
                Check(threw, "a disposed scope refuses loads");

                Step("disposed");
                var again = new AssetScope();
                Check(await again.LoadAsync(AssetKeys.SpikeSmokeCube) != null, "reload after release");
                Step("reloaded");
                var missing = "";
                try { await again.LoadAsync(new AssetKey<GameObject>("spike/not-there")); }
                catch (InvalidOperationException e) { missing = e.Message; }
                Check(missing.Contains("spike/not-there"), "missing key fails naming it");
                Check(again.Count == 1, "failed load holds no handle");
                again.Dispose();

                // The old hang: Station preloads its next scene on Start, and a load made meanwhile never finished
                // (SceneFlow held that preload at allowSceneActivation = false). Then a second switch must still go.
                var root = Object.FindAnyObjectByType<RootLifetimeScope>().Container;
                var switchMs = await Switch(root, GameScene.Station);
                Step($"in Station ({switchMs:F0} ms)");
                await UniTask.DelayFrame(2);   // StationPresenter.Start has called Preload
                var inStation = new AssetScope();
                var loadedThere = await inStation.LoadAsync(AssetKeys.SpikeSmokeCube).Timeout(TimeSpan.FromSeconds(10));
                Check(loadedThere != null, "a load while Station preloads its next scene");
                inStation.Dispose();
                var backMs = await Switch(root, GameScene.Title);
                Step($"back in Title ({backMs:F0} ms)");

                if (fails.Count == 0)
                    Debug.Log($"[AddressablesSmoke] PASS load {loadMs:F0} ms, warm {Prewarm} over {warmFrames} frames, " +
                              $"{Rounds * Prewarm} gets on {pool.Created} copies, load during Station's preload, " +
                              $"switches {switchMs:F0} + {backMs:F0} ms");
                else
                    Debug.LogError("[AddressablesSmoke] FAIL " + string.Join("; ", fails));
            }
            catch (Exception e)
            {
                Debug.LogError($"[AddressablesSmoke] FAIL {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Ask SceneFlow for `target` and wait (30 s at most) for it to be entered; ms taken.</summary>
        static async UniTask<float> Switch(IObjectResolver root, GameScene target)
        {
            var entered = new UniTaskCompletionSource();
            using var _ = root.Resolve<ISubscriber<SceneEntered>>()
                              .Subscribe(e => { if (e.Scene == target) entered.TrySetResult(); });
            var t0 = Time.realtimeSinceStartup;
            root.Resolve<IPublisher<SceneChangeRequested>>().Publish(new SceneChangeRequested(target));
            await entered.Task.Timeout(TimeSpan.FromSeconds(30));
            return (Time.realtimeSinceStartup - t0) * 1000f;
        }
    }
}
