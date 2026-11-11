using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using VContainer;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// When a scene may be seen. Every scene starts black (its presenter sets Fade(1)) and fades itself in; the
    /// curtain says when: presenters Hold it with whatever must be there first (a hull from Addressables, say), and
    /// whoever fades in awaits Opened. It opens once every hold has finished and none is left, checked after the
    /// first frame so every presenter's Start has had its say. A hold that fails is logged and let go: the scene
    /// opens without it rather than staying black.
    /// Registered Scoped in RootLifetimeScope: each scene scope gets its own, gone with the scene.
    /// </summary>
    public sealed class SceneCurtain : IDisposable
    {
        const float SlowSeconds = 5f;

        readonly UniTaskCompletionSource _opened = new();
        readonly List<string> _holding = new();
        readonly float _since = Time.realtimeSinceStartup;
        bool _watching, _disposed;

        [Inject]
        public SceneCurtain() { }

        public bool IsOpen { get; private set; }

        /// <summary>Done when the scene may be seen (cancelled if the scene goes first). Any number may await it.</summary>
        public UniTask Opened
        {
            get
            {
                Watch();
                return _opened.Task;
            }
        }

        /// <summary>Keep the curtain shut until `work` is done; `what` names it in the log.</summary>
        public void Hold(string what, UniTask work)
        {
            if (IsOpen)
            {
                Debug.LogWarning($"[Curtain] '{what}' held after the scene opened: it shows when it's ready");
                work.Forget();
                return;
            }
            _holding.Add(what);
            Release(what, work).Forget();
            Watch();
        }

        async UniTaskVoid Release(string what, UniTask work)
        {
            try { await work; }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[Curtain] '{what}' failed, opening without it: {e.Message}"); }
            _holding.Remove(what);
        }

        void Watch()
        {
            if (_watching) return;
            _watching = true;
            WatchAsync().Forget();
        }

        async UniTaskVoid WatchAsync()
        {
            await UniTask.Yield();   // every presenter's Start, so all their holds are in
            var warned = false;
            while (_holding.Count > 0 && !_disposed)
            {
                await UniTask.Yield();
                if (warned || Time.realtimeSinceStartup - _since < SlowSeconds) continue;
                warned = true;
                Debug.LogWarning($"[Curtain] still waiting after {SlowSeconds:F0}s on {string.Join(", ", _holding)}");
            }
            if (_disposed) return;
            IsOpen = true;
            Debug.Log($"[Curtain] open after {(Time.realtimeSinceStartup - _since) * 1000f:F0} ms");
            _opened.TrySetResult();
        }

        public void Dispose()
        {
            _disposed = true;
            _opened.TrySetCanceled();
        }
    }
}
