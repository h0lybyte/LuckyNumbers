using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Copies of one loaded prefab on a <see cref="Pool{T}"/>; made by <see cref="AssetScope.PoolAsync"/>, which owns
    /// it. Free copies wait under an inactive root, so making one (prewarm) runs no Awake/OnEnable and a Get only
    /// reparents it (waking it) instead of instantiating. Get and Release never touch Addressables. The root lives
    /// in the active scene: a scene change takes it along, and Get skips (and replaces) copies destroyed that way.
    /// </summary>
    public sealed class AssetPool : IDisposable
    {
        readonly GameObject _template;
        readonly string _name;
        readonly Pool<GameObject> _pool;
        readonly List<GameObject> _made = new();
        Transform _root;

        internal AssetPool(GameObject template, string name)
        {
            _template = template;
            _name = name;
            _pool = new Pool<GameObject>(Create, onRelease: Park, alive: g => g != null);
        }

        /// <summary>How many copies this pool has made (out or free).</summary>
        public int Created => _pool.Created;
        public int Free => _pool.Free;

        /// <summary>A copy, awake, under `parent` (the scene root if null).</summary>
        public GameObject Get(Transform parent = null)
        {
            var g = _pool.Get();
            g.transform.SetParent(parent, false);
            return g;
        }

        /// <summary>A copy, awake, at `position` turned `rotation`, under `parent` (the scene root if null).</summary>
        public GameObject Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            var g = _pool.Get();
            var t = g.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            return g;
        }

        public void Release(GameObject copy) => _pool.Release(copy);

        /// <summary>Make copies until `count` exist, `perFrame` a frame.</summary>
        public async UniTask WarmAsync(int count, int perFrame, CancellationToken ct = default)
        {
            perFrame = Math.Max(1, perFrame);
            while (_pool.Created < count)
            {
                _pool.Reserve(Math.Min(count, _pool.Created + perFrame));
                if (_pool.Created < count) await UniTask.Yield(ct);
            }
        }

        GameObject Create()
        {
            var g = Object.Instantiate(_template, Root, false);
            g.name = _template.name;
            _made.Add(g);
            return g;
        }

        void Park(GameObject g) => g.transform.SetParent(Root, false);

        /// <summary>Where free copies sleep: off, so they're inactive in the hierarchy. Remade after a scene change.</summary>
        Transform Root
        {
            get
            {
                if (_root != null) return _root;
                var go = new GameObject($"[Pool] {_name}");
                go.SetActive(false);
                _root = go.transform;
                return _root;
            }
        }

        /// <summary>Destroy every copy, out or free (the scope releases the prefab after).</summary>
        public void Dispose()
        {
            foreach (var g in _made) if (g != null) Object.Destroy(g);
            _made.Clear();
            _pool.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
        }
    }
}
