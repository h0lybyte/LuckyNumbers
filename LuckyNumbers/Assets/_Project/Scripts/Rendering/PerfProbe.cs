using System;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace LuckyNumbers.Rendering
{
    /// <summary>
    /// Dev: ?ln-perf in the page's URL (or -lnPerf on the command line) logs, every 5 s, the frame time (median, p95),
    /// managed allocations per frame, GC runs and the memory totals, for checking low-end runs:
    ///   [perf] frame ms median 4.1 p95 6.0 | gc alloc/frame avg 0.3 KB max 12 KB (mono), 0 GCs | mono 18/32 MB | total 210 MB
    /// Allocations come from the "GC Allocated In Frame" profiler counter when the player has it, else from the growth
    /// of the managed heap between frames (frames a GC ran in are skipped). Off: no component, no cost.
    /// </summary>
    public sealed class PerfProbe : MonoBehaviour
    {
        const float Window = 5f;
        const int MaxFrames = 8192;

        readonly float[] _frames = new float[MaxFrames];
        int _count;
        float _since;
        long _allocSum, _allocMax;
        int _allocFrames;
        long _lastMono;
        int _lastGcs, _gcsAtStart;
        ProfilerRecorder _gcAlloc, _batches, _setPass, _tris;
        long _batchSum, _setPassSum, _trisSum;
        bool _recorder;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            var url = Application.absoluteURL ?? "";
            if (!url.Contains("ln-perf") && Array.IndexOf(Environment.GetCommandLineArgs(), "-lnPerf") < 0) return;
            var go = new GameObject(nameof(PerfProbe)) { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            go.AddComponent<PerfProbe>();
        }

        void OnEnable()
        {
            _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _lastMono = Profiler.GetMonoUsedSizeLong();
            _lastGcs = _gcsAtStart = GC.CollectionCount(0);
        }

        void OnDisable()
        {
            _gcAlloc.Dispose();
            _batches.Dispose();
            _setPass.Dispose();
            _tris.Dispose();
        }

        void Update()
        {
            if (_count < MaxFrames) _frames[_count++] = Time.unscaledDeltaTime * 1000f;
            _batchSum += _batches.Valid ? _batches.LastValue : 0;
            _setPassSum += _setPass.Valid ? _setPass.LastValue : 0;
            _trisSum += _tris.Valid ? _tris.LastValue : 0;

            // The counter holds the previous frame's total once a frame has ended.
            long alloc = -1;
            if (_gcAlloc.Valid && _gcAlloc.LastValue > 0) { alloc = _gcAlloc.LastValue; _recorder = true; }
            var mono = Profiler.GetMonoUsedSizeLong();
            var gcs = GC.CollectionCount(0);
            if (!_recorder && gcs == _lastGcs) alloc = Math.Max(0, mono - _lastMono);
            _lastMono = mono;
            _lastGcs = gcs;
            if (alloc >= 0)
            {
                _allocSum += alloc;
                _allocMax = Math.Max(_allocMax, alloc);
                _allocFrames++;
            }

            _since += Time.unscaledDeltaTime;
            if (_since < Window) return;
            Array.Sort(_frames, 0, _count);
            var median = _count > 0 ? _frames[_count / 2] : 0f;
            var p95 = _count > 0 ? _frames[Mathf.Min(_count - 1, (int)(_count * 0.95f))] : 0f;
            const float Mb = 1f / (1024f * 1024f);
            Debug.Log($"[perf] {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}: frame ms median {median:0.0} p95 {p95:0.0} ({_count} frames) | " +
                      $"gc alloc/frame avg {(_allocFrames > 0 ? _allocSum / (float)_allocFrames / 1024f : 0f):0.00} KB max {_allocMax / 1024f:0.0} KB " +
                      $"({(_recorder ? "counter" : "mono")}), {gcs - _gcsAtStart} GCs | " +
                      $"mono {Profiler.GetMonoUsedSizeLong() * Mb:0.0}/{Profiler.GetMonoHeapSizeLong() * Mb:0.0} MB | " +
                      $"total {Profiler.GetTotalAllocatedMemoryLong() * Mb:0}/{Profiler.GetTotalReservedMemoryLong() * Mb:0} MB | " +
                      $"gfx {Profiler.GetAllocatedMemoryForGraphicsDriver() * Mb:0} MB | " +
                      (_batches.Valid ? $"batches {_batchSum / Mathf.Max(1, _count)} setpass {_setPassSum / Mathf.Max(1, _count)} tris {_trisSum / Mathf.Max(1, _count) / 1000}k" : "no render counters"));
            _batchSum = _setPassSum = _trisSum = 0;
            _count = 0;
            _since = 0f;
            _allocSum = _allocMax = 0;
            _allocFrames = 0;
            _gcsAtStart = gcs;
        }
    }
}
