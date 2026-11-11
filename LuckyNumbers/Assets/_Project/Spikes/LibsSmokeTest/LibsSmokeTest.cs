using System;
using System.Text;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
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
            // Opt-in: only when the page URL contains "tests" (e.g. http://localhost:8080/?tests).
            if (!(Application.absoluteURL ?? "").Contains("tests")) return;
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
                // A local provider: GlobalMessagePipe belongs to the game (RootLifetimeScope), don't replace it.
                var builder = new BuiltinContainerBuilder();
                builder.AddMessagePipe();
                builder.AddMessageBroker<int>();
                var provider = builder.BuildServiceProvider();
                var got = 0;
                using var _ = ((ISubscriber<int>)provider.GetService(typeof(ISubscriber<int>))).Subscribe(x => got = x);
                ((IPublisher<int>)provider.GetService(typeof(IPublisher<int>))).Publish(5);
                return got == 5;
            });

            Line($"INFO R3 default frame provider: {ObservableSystem.DefaultFrameProvider.GetType().Name}");

            Check("Generated content: intro + title", () =>
            {
                // content/intro.txtpb: the ship arrives first (the dice are rolled on the title now), ends on Planet.
                return GameContent.Intro.Steps[0].Step == CutsceneStep.StepCase.ShipArrives
                       && GameContent.Intro.Steps[^1].GoTo.Scene == GameScene.Planet
                       && GameContent.Title.Title == "Lucky Numbers";
            });

            // Informational: does stripping keep a constructor nothing marks? (Medium: no.) Its own container, so
            // the probe failing can't break the scope below. The rule this shows: registered types need [Inject].
            try
            {
                var plain = new ContainerBuilder();
                plain.Register<PlainService>(Lifetime.Singleton);
                using var c = plain.Build();
                Line($"INFO VContainer plain ctor (no [Inject]): kept ({c.Resolve<PlainService>().Answer})");
            }
            catch (Exception e)
            {
                Line($"INFO VContainer plain ctor (no [Inject]): stripped, so register only [Inject] ctors ({e.GetType().Name})");
            }

            // VContainer (+ MessagePipe.VContainer). Separate checks so a stripped
            // constructor shows up as its own failure.
            VContainer.Unity.LifetimeScope scope = null;
            Check("VContainer scope build", () => (scope = VContainerProbe.CreateScope()) != null);
            if (scope != null)
            {
                Check("VContainer resolve ([Inject] ctor)", () => scope.Container.Resolve<InjectedService>() != null);
                Check("Generated event via MessagePipe + VContainer", () =>
                {
                    var got = GameScene.Unspecified;
                    using var _ = scope.Container.Resolve<ISubscriber<SceneChangeRequested>>().Subscribe(e => got = e.Target);
                    scope.Container.Resolve<InjectedService>().Request(GameScene.Planet);
                    return got == GameScene.Planet;
                });
                Check("Generated state (R3) resolved as singleton", () =>
                {
                    var flow = scope.Container.Resolve<FlowState>();
                    var seen = GameScene.Unspecified;
                    using var _ = flow.Scene.Subscribe(s => seen = s);
                    flow.Scene.Value = GameScene.Intro;
                    return seen == GameScene.Intro && ReferenceEquals(flow, scope.Container.Resolve<FlowState>());
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

            // Informational: managed thread pool on Web. C# threads don't run there (only Burst job workers do),
            // so a timeout is the expected answer; Burst jobs are covered by the WebThreadsTest spike.
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
