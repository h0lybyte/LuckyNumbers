using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LuckyNumbers.Spikes.WebThreadsTest
{
    /// <summary>
    /// SPIKE: delete the WebThreadsTest folder when done.
    /// Starts automatically in any scene. Runs the same Burst job on the main thread,
    /// then as a parallel job, and shows average ms/frame and how many threads did work.
    /// </summary>
    public sealed class WebThreadsTestRunner : MonoBehaviour
    {
        const int ItemCount = 100_000;
        const int BatchSize = 256;
        const int WarmupFrames = 60;
        const int MeasureFrames = 300;

        enum Phase { WarmupSingle, Single, WarmupParallel, Parallel, Done }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int WebThreads_IsCrossOriginIsolated();
        [DllImport("__Internal")] static extern int WebThreads_HardwareConcurrency();
        static string Isolation => WebThreads_IsCrossOriginIsolated() == 1 ? "YES" : "NO (check COOP/COEP headers)";
        static int BrowserCores => WebThreads_HardwareConcurrency();
#else
        static string Isolation => "n/a (not a web build)";
        static int BrowserCores => 0;
#endif

        NativeArray<float3> _positions, _velocities;
        NativeArray<int> _threadHits;

        Phase _phase;
        int _frame;
        double _totalMs, _singleMs, _parallelMs;
        int _singleThreads, _parallelThreads;
        readonly StringBuilder _sb = new();
        GUIStyle _style;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject(nameof(WebThreadsTestRunner));
            DontDestroyOnLoad(go);
            go.AddComponent<WebThreadsTestRunner>();
        }

        void Start()
        {
            _positions = new NativeArray<float3>(ItemCount, Allocator.Persistent);
            _velocities = new NativeArray<float3>(ItemCount, Allocator.Persistent);
            _threadHits = new NativeArray<int>(JobsUtility.ThreadIndexCount, Allocator.Persistent);

            var rng = new Unity.Mathematics.Random(1234);
            for (int i = 0; i < ItemCount; i++)
            {
                _positions[i] = rng.NextFloat3(-50f, 50f);
                _velocities[i] = rng.NextFloat3(-1f, 1f);
            }

            Debug.Log($"[WebThreadsTest] started: platform={Application.platform}, cores={SystemInfo.processorCount}, " +
                      $"jobWorkers={JobsUtility.JobWorkerCount}, crossOriginIsolated={Isolation}, " +
                      $"graphics={SystemInfo.graphicsDeviceType}");
        }

        void Update()
        {
            if (_phase == Phase.Done) return;

            var job = new HeavyJob
            {
                dt = 0.016f,
                positions = _positions,
                velocities = _velocities,
                threadHits = _threadHits,
            };

            bool parallel = _phase is Phase.WarmupParallel or Phase.Parallel;
            var sw = Stopwatch.StartNew();
            if (parallel) job.Schedule(ItemCount, BatchSize).Complete();
            else job.Run(ItemCount);
            sw.Stop();

            bool measuring = _phase is Phase.Single or Phase.Parallel;
            if (measuring) _totalMs += sw.Elapsed.TotalMilliseconds;
            if (++_frame < (measuring ? MeasureFrames : WarmupFrames)) return;

            switch (_phase)
            {
                case Phase.WarmupSingle:
                case Phase.WarmupParallel:
                    ClearHits();
                    break;
                case Phase.Single:
                    _singleMs = _totalMs / MeasureFrames;
                    _singleThreads = CountThreads();
                    break;
                case Phase.Parallel:
                    _parallelMs = _totalMs / MeasureFrames;
                    _parallelThreads = CountThreads();
                    Debug.Log("[WebThreadsTest] " + BuildReport().Replace("\n", " | "));
                    break;
            }

            _phase++;
            _frame = 0;
            _totalMs = 0;
        }

        void ClearHits()
        {
            for (int i = 0; i < _threadHits.Length; i++) _threadHits[i] = 0;
        }

        int CountThreads()
        {
            int n = 0;
            for (int i = 0; i < _threadHits.Length; i++) if (_threadHits[i] > 0) n++;
            return n;
        }

        string BuildReport()
        {
            _sb.Clear();
            _sb.AppendLine($"Web threads test: {ItemCount:N0} items, Burst");
            _sb.AppendLine($"Platform: {Application.platform}   Unity {Application.unityVersion}");
            _sb.AppendLine($"Graphics: {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceVersion})");
            _sb.AppendLine($"Cross-origin isolated: {Isolation}");
            _sb.AppendLine($"Cores: Unity {SystemInfo.processorCount}" + (BrowserCores > 0 ? $", browser {BrowserCores}" : "") +
                           $"   Job workers: {JobsUtility.JobWorkerCount}");
            _sb.AppendLine($"Phase: {_phase}  frame {_frame}");
            if (_singleMs > 0)
                _sb.AppendLine($"Main thread: {_singleMs:F2} ms/frame   threads used: {_singleThreads}");
            if (_parallelMs > 0)
            {
                _sb.AppendLine($"Parallel:    {_parallelMs:F2} ms/frame   threads used: {_parallelThreads}");
                _sb.AppendLine($"Speed-up: {_singleMs / _parallelMs:F2}x");
                _sb.AppendLine(_parallelThreads > 1 ? "RESULT: multithreading WORKS" : "RESULT: ran on ONE thread");
            }
            return _sb.ToString();
        }

        void OnGUI()
        {
            _style ??= new GUIStyle(GUI.skin.box)
            {
                fontSize = 20, alignment = TextAnchor.UpperLeft, wordWrap = true,
                normal = { textColor = Color.white },
            };
            GUI.Box(new Rect(10, 10, Mathf.Min(Screen.width - 20, 720), 310), BuildReport(), _style);
        }

        void OnDestroy()
        {
            if (_positions.IsCreated) _positions.Dispose();
            if (_velocities.IsCreated) _velocities.Dispose();
            if (_threadHits.IsCreated) _threadHits.Dispose();
        }
    }
}
