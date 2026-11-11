using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using VContainer;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The one owner of Addressables handles for a lifetime; nothing else calls Addressables. Registered Scoped in
    /// RootLifetimeScope, so the root and every scene scope get their own, and a scene's is disposed with its
    /// LifetimeScope (scene unload): whatever a scene loaded goes when the scene goes, no Release calls to forget.
    /// One handle per key per scope (loading a key twice shares it). Dispose destroys this scope's pools first,
    /// then releases the handles: an instance must never outlive the bundle its meshes and textures live in.
    /// Main thread only.
    /// </summary>
    public sealed class AssetScope : IDisposable
    {
        const float SlowSeconds = 5f;

        readonly Dictionary<string, AsyncOperationHandle> _handles = new();
        readonly List<AssetPool> _pools = new();
        readonly CancellationTokenSource _disposed = new();

        [Inject]
        public AssetScope() { }

        public bool IsDisposed { get; private set; }

        /// <summary>How many keys this scope holds (loaded or loading).</summary>
        public int Count => _handles.Count;

        /// <summary>The asset behind `key`, loading it if this scope hasn't. Throws (naming the key) if it can't load.</summary>
        public async UniTask<T> LoadAsync<T>(AssetKey<T> key, CancellationToken ct = default) where T : Object
        {
            ThrowIfDisposed();
            if (!_handles.TryGetValue(key.Address, out var handle))
            {
                handle = Addressables.LoadAssetAsync<T>(key.Address);
                _handles.Add(key.Address, handle);
            }
            if (!handle.IsDone)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
                var start = Time.realtimeSinceStartup;
                var warned = false;
                while (!handle.IsDone)
                {
                    await UniTask.Yield(linked.Token);
                    if (warned || Time.realtimeSinceStartup - start < SlowSeconds) continue;
                    warned = true;   // a stall should be loud: a held scene load (allowSceneActivation = false) blocks every load
                    Debug.LogWarning($"[Assets] '{key}' still loading after {SlowSeconds:F0}s ({handle.PercentComplete:P0}); " +
                                     "is an async scene load being held?");
                }
            }
            if (handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded && handle.Result is T asset)
                return asset;
            var why = handle.IsValid() ? handle.OperationException?.Message ?? $"not a {typeof(T).Name}" : "released";
            Forget(key.Address, handle);
            throw new InvalidOperationException($"[Assets] can't load '{key}': {why}");
        }

        /// <summary>The asset behind `key` if this scope already finished loading it.</summary>
        public bool TryGet<T>(AssetKey<T> key, out T asset) where T : Object
        {
            asset = null;
            return _handles.TryGetValue(key.Address, out var h) && h.IsDone &&
                   h.Status == AsyncOperationStatus.Succeeded && (asset = h.Result as T) != null;
        }

        /// <summary>
        /// A pool of copies of `key`'s prefab, with `prewarm` made up front, `perFrame` a frame so warming never
        /// hitches. The pool belongs to this scope: it's destroyed (copies included) before the prefab is released.
        /// </summary>
        public async UniTask<AssetPool> PoolAsync(AssetKey<GameObject> key, int prewarm = 0, int perFrame = 4,
                                                  CancellationToken ct = default)
        {
            var template = await LoadAsync(key, ct);
            ThrowIfDisposed();
            var pool = new AssetPool(template, key.Address);
            _pools.Add(pool);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            await pool.WarmAsync(prewarm, perFrame, linked.Token);
            return pool;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _disposed.Cancel();
            for (var i = _pools.Count - 1; i >= 0; i--) _pools[i].Dispose();
            _pools.Clear();
            foreach (var handle in _handles.Values) Release(handle);
            _handles.Clear();
        }

        void Forget(string address, AsyncOperationHandle handle)
        {
            if (!_handles.TryGetValue(address, out var held) || !held.Equals(handle)) return;
            _handles.Remove(address);
            Release(handle);
        }

        static void Release(AsyncOperationHandle handle)
        {
            if (!handle.IsValid()) return;
            if (handle.IsDone) Addressables.Release(handle);
            else handle.Completed += Addressables.Release;   // still loading: let it land, then let it go
        }

        void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(AssetScope));
        }
    }
}
