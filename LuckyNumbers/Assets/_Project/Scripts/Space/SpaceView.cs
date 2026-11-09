using UnityEngine;
using UnityEngine.EventSystems;

namespace LuckyNumbers.Space
{
    /// <summary>Camera, skybox and a sphere template. SpacePresenter spawns one body per entity.</summary>
    [View]
    public sealed class SpaceView : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] MeshRenderer bodyTemplate;
        Material _sky;

        public Camera Camera => viewCamera;

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
        public MeshRenderer SpawnBody(string id)
        {
            var r = Instantiate(bodyTemplate, transform);
            r.name = id;
            r.gameObject.SetActive(true);
            return r;
        }

        public bool IsPointerOverUI() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
