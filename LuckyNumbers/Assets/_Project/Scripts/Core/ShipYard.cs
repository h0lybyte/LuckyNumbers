using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using VContainer;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Fits ships' hulls from Addressables (Prefabs/Ships, the Ships group): a ShipView shell with a ModelAddress gets
    /// a copy of that prefab under its pivot. Fit is shared per ship (call it as often as you like). Registered Scoped
    /// in RootLifetimeScope: one per scene, the prefabs released (its AssetScope) when the scene goes, the copies with it.
    /// </summary>
    public sealed class ShipYard
    {
        readonly AssetScope _assets;
        readonly Dictionary<ShipView, UniTask> _fits = new();

        [Inject]
        public ShipYard(AssetScope assets) => _assets = assets;

        /// <summary>The ship's hull, loaded and fitted (done at once if it has none to load or already has it).</summary>
        public UniTask Fit(ShipView ship, CancellationToken ct = default)
        {
            if (ship == null || ship.Fitted) return UniTask.CompletedTask;
            if (!_fits.TryGetValue(ship, out var task))
                _fits[ship] = task = Share(FitAsync(ship, ct));
            return task;
        }

        async UniTask FitAsync(ShipView ship, CancellationToken ct)
        {
            var prefab = await _assets.LoadAsync(new AssetKey<GameObject>(ship.ModelAddress), ct);
            if (ship == null) return;   // the scene went while loading
            var hull = Object.Instantiate(prefab, ship.Pivot, false);
            hull.name = prefab.name;
            ship.Fit(hull);
        }

        static UniTask Share(UniTask task)
        {
            var shared = new UniTaskCompletionSource();
            Relay(task, shared).Forget();
            return shared.Task;
        }

        static async UniTaskVoid Relay(UniTask task, UniTaskCompletionSource shared)
        {
            try { await task; shared.TrySetResult(); }
            catch (System.OperationCanceledException) { shared.TrySetCanceled(); }
            catch (System.Exception e) { shared.TrySetException(e); }
        }
    }
}
