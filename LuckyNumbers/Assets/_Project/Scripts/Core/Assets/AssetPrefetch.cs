using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Fetch ahead: download the bundles behind every asset with a label (a scene's name, see AddressablesSetup.Rules)
    /// into the browser cache, loading nothing and holding nothing. Unlike a scene load held at
    /// allowSceneActivation = false, this never blocks Unity's async queue, so loads elsewhere carry on.
    /// </summary>
    public static class AssetPrefetch
    {
        /// <summary>Download what `label` needs; `progress` gets 0..1. A label nothing has finishes at once.</summary>
        public static async UniTask RunAsync(string label, Action<float> progress, CancellationToken ct = default)
        {
            var where = Addressables.LoadResourceLocationsAsync(label);   // unknown label: succeeds, empty
            try
            {
                while (!where.IsDone) await UniTask.Yield(ct);
                if (where.Status != AsyncOperationStatus.Succeeded || where.Result == null || where.Result.Count == 0)
                {
                    progress?.Invoke(1f);
                    return;
                }
                await Download(where.Result, label, progress, ct);
            }
            finally
            {
                if (where.IsValid()) Addressables.Release(where);
            }
        }

        static async UniTask Download(IList<IResourceLocation> locations, string label, Action<float> progress,
                                      CancellationToken ct)
        {
            var download = Addressables.DownloadDependenciesAsync(locations, false);
            try
            {
                while (!download.IsDone)
                {
                    progress?.Invoke(download.PercentComplete);
                    await UniTask.Yield(ct);
                }
                if (download.Status != AsyncOperationStatus.Succeeded)
                    Debug.LogWarning($"[Assets] prefetch '{label}' failed ({download.OperationException?.Message}); " +
                                     "its assets will download when loaded");
                progress?.Invoke(1f);
            }
            finally
            {
                if (download.IsValid()) Addressables.Release(download);
            }
        }
    }
}
