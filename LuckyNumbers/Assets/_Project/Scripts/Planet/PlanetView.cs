using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Planet
{
    /// <summary>Crash-site overlay: full-screen fade and a controls hint.</summary>
    [View]
    public sealed partial class PlanetView : MonoBehaviour
    {
        [SerializeField] UIDocument document;
        [SerializeField] MeshRenderer sun;   // star sphere far away in the sky

        [Ui("flash")] VisualElement _flash;
        [Ui("hint")] Label _hint;

        public MeshRenderer Sun => sun;

        public void Bind() => BindUi(document.rootVisualElement);

        public void Fade(float alpha) => _flash.style.opacity = Mathf.Clamp01(alpha);

        public void Hint(string text, float alpha)
        {
            _hint.text = text;
            _hint.style.opacity = Mathf.Clamp01(alpha);
        }
    }
}
