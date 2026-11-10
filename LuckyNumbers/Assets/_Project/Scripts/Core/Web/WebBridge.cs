using System;
using System.Runtime.InteropServices;
using System.Text;
using LuckyNumbers.Model;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Unity &lt;-&gt; web page bridge for (ln.web) states and events (see WebMirror.g.cs and window.LN in the
    /// WebGL template). Built for the threaded Web build:
    /// <list type="bullet">
    /// <item>All interop happens here, on the main thread, once per frame (PostLateUpdate): jslib calls are
    /// never made from job/worker threads, which have no DOM and would need proxying.</item>
    /// <item>Out: states are coalesced (each changed state serialized once, however often it changed) and
    /// sent with that frame's events as one string, one boundary crossing, nothing when idle.</item>
    /// <item>In: the page queues events in JS; we drain the queue in one call per frame instead of
    /// SendMessage (no GameObject lookup, no per-message crossing).</item>
    /// </list>
    /// Outside a Web player (editor, desktop) it does nothing.
    /// </summary>
    [Presenter]
    public sealed class WebBridge : IWebSink, IStartable, IPostLateTickable, IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void LN_Push(string batch);
        [DllImport("__Internal")] static extern string LN_Drain();
        [DllImport("__Internal")] static extern void LN_Ready();
        static readonly bool Enabled = true;
#else
        static void LN_Push(string batch) { }
        static string LN_Drain() => null;
        static void LN_Ready() { }
        static readonly bool Enabled = false; // readonly, not const: no unreachable-code warnings
#endif

        readonly IObjectResolver _resolver;
        readonly bool[] _dirty = new bool[WebMirror.States.Length];
        readonly StringBuilder _events = new StringBuilder(256);
        readonly StringBuilder _batch = new StringBuilder(1024);
        bool _anyDirty;
        int _eventCount;
        IDisposable _binding;

        [Inject]
        public WebBridge(IObjectResolver resolver) => _resolver = resolver;

        public void Start()
        {
            if (!Enabled) return;
            _binding = WebMirror.Bind(_resolver, this);
            LN_Ready();
        }

        public void PostLateTick()
        {
            if (!Enabled) return;
            // In first: page-sent events publish now, so their effects (and echoes) go out in this frame's batch.
            var inbound = LN_Drain();
            if (!string.IsNullOrEmpty(inbound)) Receive(inbound);
            Flush();
        }

        public void MarkState(int index)
        {
            _dirty[index] = true;
            _anyDirty = true;
        }

        public StringBuilder BeginEvent(string name)
        {
            _events.Append(_eventCount++ == 0 ? "[\"" : ",[\"").Append(name).Append("\",");
            return _events;
        }

        public void EndEvent() => _events.Append(']');

        void Flush()
        {
            if (!_anyDirty && _eventCount == 0) return;
            _batch.Clear().Append("{\"s\":{");
            var first = true;
            for (var i = 0; i < _dirty.Length; i++)
            {
                if (!_dirty[i]) continue;
                _dirty[i] = false;
                if (!first) _batch.Append(',');
                first = false;
                _batch.Append('"').Append(WebMirror.States[i]).Append("\":");
                WebMirror.WriteState(_resolver, i, _batch);
            }
            _batch.Append("},\"e\":[").Append(_events).Append("]}");
            _anyDirty = false;
            _eventCount = 0;
            _events.Clear();
            LN_Push(_batch.ToString());
        }

        void Receive(string json)
        {
            foreach (var (name, payload) in WebMirror.Json.ParseBatch(json))
            {
                try
                {
                    if (!WebMirror.Publish(_resolver, name, payload))
                        Debug.LogWarning($"[WebBridge] page sent unknown event '{name}'");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public void Dispose() => _binding?.Dispose();
    }
}
