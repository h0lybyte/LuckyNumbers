using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The scene's weapons, copied from Prefabs/Weapons (Addressables, AssetKeys.Weapon) instead of baked into the
    /// scene. Copies come inactive and named "Weapon &lt;model&gt;", parked where the scenes used to park them, so the
    /// code that finds them there is unchanged: "Equipped weapons" (FirstPersonPresenter), "Range weapons" (RangeRoom),
    /// or loose at the scene root (StationPresenter's test gun). Registered Scoped in RootLifetimeScope: one per scene,
    /// its prefabs released (through the scene's AssetScope) when the scene goes, the copies with the scene.
    /// </summary>
    public sealed class WeaponRack
    {
        public const string EquippedRoot = "Equipped weapons";
        public const string RangeRoot = "Range weapons";

        readonly AssetScope _assets;
        readonly Dictionary<string, UniTask<Rigidbody>> _equipped = new();
        readonly HashSet<string> _unknown = new();
        UniTask<Transform>? _range;

        [Inject]
        public WeaponRack(AssetScope assets) => _assets = assets;

        /// <summary>Is there a prefab for `model` (an item's model name)? Warns once if not.</summary>
        public bool Has(string model)
        {
            if (!string.IsNullOrEmpty(model) && AssetKeys.Weapon.ContainsKey(model)) return true;
            if (!string.IsNullOrEmpty(model) && _unknown.Add(model))
                Debug.LogWarning($"[Weapons] no prefab for '{model}' (Prefabs/Weapons; rebuild with tools/ln.py scenes)");
            return false;
        }

        /// <summary>The equipped-gun copy of `model`, parked under "Equipped weapons" (one per model; null if none).</summary>
        public UniTask<Rigidbody> Equipped(string model, CancellationToken ct = default)
        {
            if (!Has(model)) return UniTask.FromResult<Rigidbody>(null);
            if (!_equipped.TryGetValue(model, out var task))
                _equipped[model] = task = Share(Park(model, Root(EquippedRoot), ct));
            return task;
        }

        /// <summary>Every range weapon (all but placeholder guns), by name, under "Range weapons"; loaded once.</summary>
        public UniTask<Transform> Range(CancellationToken ct = default)
        {
            _range ??= Share(LoadRange(ct));
            return _range.Value;
        }

        /// <summary>A copy of `model` on its own at the scene root (null if there's no prefab).</summary>
        public async UniTask<Rigidbody> Loose(string model, CancellationToken ct = default) =>
            Has(model) ? await Park(model, null, ct) : null;

        /// <summary>
        /// A task any number of callers can await at once, or poll Status on (Preserve only allows one awaiter at a time
        /// while pending: two Range() calls during a station rebuild threw "Already continuation registered").
        /// </summary>
        public static UniTask<T> Share<T>(UniTask<T> task)
        {
            var shared = new UniTaskCompletionSource<T>();
            Relay(task, shared).Forget();
            return shared.Task;
        }

        static async UniTaskVoid Relay<T>(UniTask<T> task, UniTaskCompletionSource<T> shared)
        {
            try { shared.TrySetResult(await task); }
            catch (System.OperationCanceledException) { shared.TrySetCanceled(); }
            catch (System.Exception e) { shared.TrySetException(e); }
        }

        async UniTask<Transform> LoadRange(CancellationToken ct)
        {
            var root = Root(RangeRoot);
            var names = new List<string>();
            foreach (var name in AssetKeys.Weapon.Keys)
                if (!name.StartsWith("PLACEHOLDER_")) names.Add(name);
            names.Sort(System.StringComparer.Ordinal);
            var copies = new UniTask<Rigidbody>[names.Count];
            for (var i = 0; i < names.Count; i++) copies[i] = Park(names[i], root, ct);
            await UniTask.WhenAll(copies);   // loads run side by side; children keep name order
            return root;
        }

        async UniTask<Rigidbody> Park(string model, Transform parent, CancellationToken ct)
        {
            var prefab = await _assets.LoadAsync(AssetKeys.Weapon[model], ct);
            var copy = Object.Instantiate(prefab, parent, false);   // the prefab is saved inactive: no Awake yet
            copy.name = $"Weapon {model}";
            copy.SetActive(false);
            if (parent != null) copy.transform.SetSiblingIndex(SiblingFor(parent, copy.name));
            return copy.GetComponent<Rigidbody>();
        }

        /// <summary>Where `name` goes among siblings already there, by name (loads finish in any order).</summary>
        static int SiblingFor(Transform parent, string name)
        {
            var i = 0;
            foreach (Transform t in parent)
                if (string.CompareOrdinal(t.name, name) < 0) i++;
            return i;
        }

        static Transform Root(string name)
        {
            var found = GameObject.Find(name);
            return found != null ? found.transform : new GameObject(name).transform;
        }
    }
}
