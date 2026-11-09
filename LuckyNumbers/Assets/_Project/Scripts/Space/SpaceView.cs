using UnityEngine;
using UnityEngine.EventSystems;

namespace LuckyNumbers.Space
{
    /// <summary>Camera, starfield and a body template. SpacePresenter spawns one quad per entity.</summary>
    [View]
    public sealed class SpaceView : MonoBehaviour
    {
        [SerializeField] Camera viewCamera;
        [SerializeField] MeshRenderer starfield;
        [SerializeField] MeshRenderer bodyTemplate;

        public Camera Camera => viewCamera;
        public Material StarfieldMaterial => starfield.material;

        /// <summary>New body quad (inactive template cloned, own material instance).</summary>
        public MeshRenderer SpawnBody(string id, int order)
        {
            var r = Instantiate(bodyTemplate, transform);
            r.name = id;
            r.gameObject.SetActive(true);
            r.transform.localPosition = new Vector3(0, 0, -0.01f * order); // later = in front
            return r;
        }

        public bool IsPointerOverUI() => EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }
}
