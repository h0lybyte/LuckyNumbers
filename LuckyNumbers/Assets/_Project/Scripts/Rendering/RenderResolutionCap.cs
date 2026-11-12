using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LuckyNumbers.Rendering
{
    /// <summary>
    /// Caps the internal 3D render resolution (URP render scale): 1280x720 unless the player's render quality
    /// setting picks another height (SetMaxHeight; Core/SettingsPresenter). The window / canvas can be any size; the
    /// scene renders at most that and
    /// URP upscales to the screen. UI (Screen Space - Overlay) stays native res.
    /// Bootstraps itself before the first scene loads — no scene setup needed.
    /// </summary>
    public sealed class RenderResolutionCap : MonoBehaviour
    {
        public static int MaxWidth { get; private set; } = 1280;
        public static int MaxHeight { get; private set; } = 720;
        static int _version;   // bumped by SetMaxHeight: re-apply even when the screen hasn't changed size
        int _applied;

        /// <summary>Cap the render target at this height (and 16:9 of it wide). Applies next frame.</summary>
        public static void SetMaxHeight(int height)
        {
            height = Mathf.Clamp(height, 240, 2160);
            if (height == MaxHeight) return;
            MaxHeight = height;
            MaxWidth = Mathf.RoundToInt(height * 16f / 9f);
            _version++;
        }

        UniversalRenderPipelineAsset _asset;
        float _baseScale;
        int _lastW, _lastH;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject(nameof(RenderResolutionCap));
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            go.AddComponent<RenderResolutionCap>();
        }

        void LateUpdate()
        {
            // Quality level changes can swap the active pipeline asset.
            var current = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (current != _asset)
            {
                Restore();
                _asset = current;
                _baseScale = _asset != null ? _asset.renderScale : 1f;
                _lastW = _lastH = 0;
            }

            if (_asset == null || (Screen.width == _lastW && Screen.height == _lastH && _applied == _version))
                return;

            _applied = _version;

            _lastW = Screen.width;
            _lastH = Screen.height;
            _asset.renderScale = ScaleFor(_lastW, _lastH, _baseScale);
        }

        /// <summary>Largest scale that keeps the render target within MaxWidth x MaxHeight,
        /// never above the asset's own configured scale (e.g. Mobile = 0.8).</summary>
        public static float ScaleFor(int screenW, int screenH, float baseScale)
        {
            if (screenW <= 0 || screenH <= 0) return baseScale;
            float cap = Mathf.Min((float)MaxWidth / screenW, (float)MaxHeight / screenH);
            return Mathf.Clamp(Mathf.Min(baseScale, cap), 0.1f, 2f);
        }

        // Runtime edits to the pipeline asset persist to disk in the Editor,
        // so put the original value back when play mode ends.
        void OnDestroy() => Restore();
        void OnApplicationQuit() => Restore();

        void Restore()
        {
            if (_asset != null) _asset.renderScale = _baseScale;
        }
    }
}
