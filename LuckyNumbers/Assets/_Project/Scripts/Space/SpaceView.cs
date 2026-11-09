using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Space
{
    /// <summary>Camera, skybox and a sphere template. SpacePresenter spawns one body per entity.</summary>
    [View]
    public sealed class SpaceView : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] MeshRenderer planetTemplate;
        [SerializeField] MeshRenderer starTemplate;
        [SerializeField] MeshRenderer galaxyTemplate;
        [SerializeField] MeshRenderer disk;  // flat quad in the orbital plane, shown while a system forms
        [SerializeField] UIDocument ui; // optional: drags don't start on its interactive elements
        Material _sky;

        public Camera Camera => viewCamera;
        public MeshRenderer Disk => disk;

        /// <summary>Per-scene copy of the skybox material (editing the asset at runtime would persist in the editor).</summary>
        public Material SkyboxMaterial
        {
            get
            {
                if (_sky == null && RenderSettings.skybox != null)
                    RenderSettings.skybox = _sky = new Material(RenderSettings.skybox);
                return _sky;
            }
        }

        /// <summary>New body (inactive template cloned, own material instance).</summary>
        public MeshRenderer SpawnBody(string id, BodyKind kind)
        {
            var template = kind switch { BodyKind.Star => starTemplate, BodyKind.Galaxy => galaxyTemplate, _ => planetTemplate };
            var r = Instantiate(template, transform);
            r.name = id;
            r.gameObject.SetActive(true);
            return r;
        }

        /// <summary>True if <paramref name="screen"/> (pixels, origin bottom-left) is over an element that takes the pointer.</summary>
        public bool IsPointerOverUI(Vector2 screen)
        {
            var root = ui != null ? ui.rootVisualElement : null;
            if (root?.panel == null) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = root.panel.Pick(p);
            return picked != null && picked != root;
        }
    }
}
