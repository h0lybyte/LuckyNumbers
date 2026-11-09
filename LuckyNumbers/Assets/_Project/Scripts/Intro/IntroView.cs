using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Intro
{
    /// <summary>Intro overlay: dice panel fade, dialogue box (typewriter), full-screen flash/fade.</summary>
    [View]
    public sealed partial class IntroView : MonoBehaviour
    {
        [SerializeField] UIDocument document;

        [Ui("dice-panel")] VisualElement _dicePanel;
        [Ui("dialogue")] VisualElement _dialogue;
        [Ui("speaker")] Label _speaker;
        [Ui("line")] Label _line;
        [Ui("flash")] VisualElement _flash;

        public void Bind() => BindUi(document.rootVisualElement);

        public void DiceVisible(bool visible) => _dicePanel.style.opacity = visible ? 1f : 0f;

        /// <summary>Full-screen colour over everything (white flash on impact, black fade after).</summary>
        public void Flash(Color color, float alpha)
        {
            _flash.style.backgroundColor = color;
            _flash.style.opacity = Mathf.Clamp01(alpha);
        }

        public void Dialogue(string speaker, string text, int visibleChars)
        {
            _dialogue.EnableInClassList("ln-hidden", text == null);
            if (text == null) return;
            _speaker.text = speaker;
            _line.text = text.Substring(0, Mathf.Clamp(visibleChars, 0, text.Length));
        }
    }
}
