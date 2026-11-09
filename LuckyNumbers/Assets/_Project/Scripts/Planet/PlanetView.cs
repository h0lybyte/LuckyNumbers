using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Planet
{
    /// <summary>Crash-site overlay: full-screen fade, a controls hint and the dialogue box (typewriter).</summary>
    [View]
    public sealed partial class PlanetView : MonoBehaviour
    {
        [SerializeField] UIDocument document;
        [SerializeField] MeshRenderer sun;   // star sphere far away in the sky

        [Ui("flash")] VisualElement _flash;
        [Ui("hint")] Label _hint;
        [Ui("map-hint")] Label _mapHint;
        [Ui("crosshair")] VisualElement _crosshair;
        [Ui("dialogue")] VisualElement _dialogue;
        [Ui("speaker")] Label _speaker;
        [Ui("line")] Label _line;

        public MeshRenderer Sun => sun;

        public void Bind() => BindUi(document.rootVisualElement);

        public void Fade(float alpha) => _flash.style.opacity = Mathf.Clamp01(alpha);

        public void Dialogue(string speaker, string text, int visibleChars)
        {
            _dialogue.EnableInClassList("ln-hidden", text == null);
            if (text == null) return;
            _speaker.text = speaker;
            _line.text = text.Substring(0, Mathf.Clamp(visibleChars, 0, text.Length));
        }

        /// <summary>Map controls line (null = map closed: hide it, show the crosshair).</summary>
        public void MapHint(string text)
        {
            _mapHint.text = text ?? "";
            _mapHint.EnableInClassList("ln-hidden", text == null);
            _crosshair.EnableInClassList("ln-hidden", text != null);
            _hint.EnableInClassList("ln-hidden", text != null);
        }

        public void Hint(string text, float alpha)
        {
            _hint.text = text;
            _hint.style.opacity = Mathf.Clamp01(alpha);
        }
    }
}
