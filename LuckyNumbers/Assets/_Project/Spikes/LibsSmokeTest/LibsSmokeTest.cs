using System;
using System.Text;
using Cysharp.Threading.Tasks;
using MessagePipe;
using ObservableCollections;
using R3;
using UnityEngine;
using VContainer;

namespace LuckyNumbers.Spikes.LibsSmokeTest
{
    /// <summary>
    /// SPIKE: delete the LibsSmokeTest folder when done.
    /// Exercises UniTask, R3, ObservableCollections(+R3) and MessagePipe at runtime, so a Web
    /// (IL2CPP) build shows whether they survive stripping. Logs "[LibsSmokeTest] ..." lines.
    /// </summary>
    public sealed class LibsSmokeTest : MonoBehaviour
    {
        readonly StringBuilder _report = new();
        int _failures;
        bool _done;
        GUIStyle _style;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject(nameof(LibsSmokeTest));
            DontDestroyOnLoad(go);
            go.AddComponent<LibsSmokeTest>();
        }

        void Start() => RunAsync().Forget();

        async UniTaskVoid RunAsync()
        {
            Line($"Libs smoke test (v{Application.version}, {Application.platform})");

            Check("R3 ReactiveProperty", () =>
            {
                var rp = new ReactiveProperty<int>(1);
                var seen = 0;
                using var _ = rp.Subscribe(v => seen = v);
                rp.Value = 42;
                return seen == 42;
            });

            Check("ObservableCollections + R3 ObserveAdd", () =>
            {
                var list = new ObservableList<int>();
                var added = 0;
                using var _ = list.ObserveAdd().Subscribe(e => added = e.Value);
                list.Add(7);
                return added == 7 && list.Count == 1;
            });

            Check("MessagePipe publish/subscribe", () =>
            {
                // IL2CPP has no open-generic support: every message type is registered explicitly.
                var builder = new BuiltinContainerBuilder();
                builder.AddMessagePipe();
                builder.AddMessageBroker<int>();
                GlobalMessagePipe.SetProvider(builder.BuildServiceProvider());
                var got = 0;
                using var _ = GlobalMessagePipe.GetSubscriber<int>().Subscribe(x => got = x);
                GlobalMessagePipe.GetPublisher<int>().Publish(5);
                return got == 5;
            });

            Line($"INFO R3 default frame provider: {ObservableSystem.DefaultFrameProvider.GetType().Name}");

            // VContainer (+ MessagePipe.VContainer). Separate checks so a stripped
            // constructor shows up as its own failure.
            VContainer.Unity.LifetimeScope scope = null;
            Check("VContainer scope build", () => (scope = VContainerProbe.CreateScope()) != null);
            if (scope != null)
            {
                Check("VContainer resolve ([Inject] ctor)", () => scope.Container.Resolve<InjectedService>() != null);
                Check("VContainer resolve (plain ctor)", () => scope.Container.Resolve<PlainService>().Answer == 42);
                Check("MessagePipe via VContainer", () =>
                {
                    var got = 0;
                    using var _ = scope.Container.Resolve<ISubscriber<ScoreChanged>>().Subscribe(e => got = e.Value);
                    scope.Container.Resolve<InjectedService>().Raise(9);
                    return got == 9;
                });
                await UniTask.DelayFrame(2); // entry points start from VContainer's PlayerLoop
                Check("VContainer entry point (IStartable)", () => StartProbe.Started);
                scope.Dispose();
            }

            await CheckAsync("UniTask.DelayFrame(3)", async () =>
            {
                var start = Time.frameCount;
                await UniTask.DelayFrame(3);
                return Time.frameCount - start >= 3;
            });

            await CheckAsync("UniTask.Delay(100ms)", async () =>
            {
                var start = Time.realtimeSinceStartup;
                await UniTask.Delay(100);
                return Time.realtimeSinceStartup - start >= 0.09f;
            });

            await CheckAsync("R3 Observable.TimerFrame(3)", async () =>
            {
                await Observable.TimerFrame(3).FirstAsync();
                return true;
            });

            // Informational: managed thread pool on Web (System.Threading is single-threaded there).
            // Bounded by a timeout so a hang can't block the result line.
            try
            {
                var mainId = Environment.CurrentManagedThreadId;
                var poolId = await UniTask.RunOnThreadPool(() => Environment.CurrentManagedThreadId)
                    .Timeout(TimeSpan.FromSeconds(3));
                Line($"INFO UniTask thread pool: main={mainId} pool={poolId} " +
                     (poolId != mainId ? "(separate thread)" : "(same thread)"));
            }
            catch (Exception e)
            {
                Line($"INFO UniTask thread pool: {e.GetType().Name}: {e.Message}");
            }

            Line(_failures == 0 ? "RESULT: ALL PASS" : $"RESULT: {_failures} FAILED");
            _done = true;
        }

        void Check(string name, Func<bool> test)
        {
            try { Result(name, test(), null); }
            catch (Exception e) { Result(name, false, e); }
        }

        async UniTask CheckAsync(string name, Func<UniTask<bool>> test)
        {
            try { Result(name, await test(), null); }
            catch (Exception e) { Result(name, false, e); }
        }

        void Result(string name, bool ok, Exception e)
        {
            if (!ok) _failures++;
            Line((ok ? "PASS " : "FAIL ") + name + (e != null ? $" ({e.GetType().Name}: {e.Message})" : ""));
        }

        void Line(string s)
        {
            _report.AppendLine(s);
            Debug.Log("[LibsSmokeTest] " + s); // per line, so a hang shows where it stopped
        }

        void OnGUI()
        {
            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize = 18, alignment = TextAnchor.UpperLeft, wordWrap = true,
                normal = { textColor = _failures == 0 ? Color.white : Color.red },
            };
            var text = _done ? _report.ToString() : _report + "running...";
            GUI.Box(new Rect(10, 330, Mathf.Min(Screen.width - 20, 720), 300), text, _style);
        }
    }
}
