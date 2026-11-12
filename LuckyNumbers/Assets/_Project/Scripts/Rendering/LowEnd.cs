using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LuckyNumbers.Rendering
{
    /// <summary>
    /// The low-end tier, for phones and small laptops: no post-processing (the lamp and engine bloom), no HDR colour
    /// buffer (nothing to tone-map without post), and the Low render quality (540p) as the default until the player picks
    /// one in Settings (SettingsPresenter). Everything else looks the same.
    /// On when the page's URL has ?ln-low, or the browser reports 4 GB of memory or less (navigator.deviceMemory), or a
    /// phone / tablet is on the WebGL 2 fallback. ?ln-low=0 forces it off. The console says which:
    ///   [LowEnd] on (device memory 4 GB): no post-processing, no HDR, 540p by default
    /// </summary>
    public sealed class LowEnd : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern float LN_DeviceMemory();
#else
        static float LN_DeviceMemory() => 0f;
#endif

        static bool? _active;
        static string _why;

        /// <summary>The low-end tier is on (decided once, at start).</summary>
        public static bool Active
        {
            get
            {
                if (_active == null) Decide();
                return _active.Value;
            }
        }

        UniversalRenderPipelineAsset _asset;
        bool _baseHdr;

        static void Decide()
        {
            var url = Application.absoluteURL ?? "";
            var i = url.IndexOf("ln-low", System.StringComparison.Ordinal);
            if (i >= 0)
            {
                var off = url.Length > i + 7 && url[i + 6] == '=' && url[i + 7] == '0';
                _active = !off;
                _why = off ? "?ln-low=0" : "?ln-low";
                return;
            }
            var memory = LN_DeviceMemory();
            if (memory > 0f && memory <= 4f)
            {
                _active = true;
                _why = $"device memory {memory:0.##} GB";
                return;
            }
            if (Application.isMobilePlatform && SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3)
            {
                _active = true;
                _why = "mobile on WebGL 2";
                return;
            }
            _active = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (!Active)
            {
                if (_why != null) Debug.Log($"[LowEnd] off ({_why})");
                return;
            }
            Debug.Log($"[LowEnd] on ({_why}): no post-processing, no HDR, 540p by default");
            var go = new GameObject(nameof(LowEnd)) { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            go.AddComponent<LowEnd>();
        }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += Begin;
        void OnDisable() => RenderPipelineManager.beginCameraRendering -= Begin;

        /// <summary>Before each camera: the pipeline asset without HDR (a quality change can swap it), the camera without post.</summary>
        void Begin(ScriptableRenderContext _, Camera camera)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset != _asset)
            {
                Restore();
                _asset = asset;
                if (_asset != null)
                {
                    _baseHdr = _asset.supportsHDR;
                    _asset.supportsHDR = false;
                }
            }
            if (camera.TryGetComponent<UniversalAdditionalCameraData>(out var data) && data.renderPostProcessing)
                data.renderPostProcessing = false;
        }

        // Runtime edits to the pipeline asset persist to disk in the Editor: put HDR back when play mode ends.
        void OnDestroy() => Restore();

        void Restore()
        {
            if (_asset != null) _asset.supportsHDR = _baseHdr;
        }
    }
}
