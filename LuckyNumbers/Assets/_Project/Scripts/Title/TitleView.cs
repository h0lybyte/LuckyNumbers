using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace LuckyNumbers.Title
{
    /// <summary>Title UI (UI/Screens/Title.uxml). No logic: TitlePresenter drives it. [Ui] fields are bound by Roslyn.</summary>
    [View]
    public sealed partial class TitleView : MonoBehaviour
    {
        [SerializeField] UIDocument document;

        [Ui("title")] Label _title;
        [Ui("prompt")] Label _prompt;
        [Ui("menu")] VisualElement _menu;
        [Ui("settings")] VisualElement _settings;
        [Ui("settings-title")] Label _settingsTitle;
        [Ui("settings-body")] Label _settingsBody;
        [Ui("settings-back")] Button _settingsBack;
        bool _bound;

        /// <summary>Looks up the UXML elements (generated BindUi); call before using the view.</summary>
        public void Bind()
        {
            if (_bound) return;
            document.rootVisualElement.pickingMode = PickingMode.Ignore; // only buttons/panels catch the pointer
            BindUi(document.rootVisualElement);
            _bound = true;
        }

        public void SetTitle(string text) => _title.text = text;
        public void SetPrompt(string text) => _prompt.text = text;
        public void SetPromptAlpha(float alpha) => _prompt.style.opacity = alpha;

        public void AddMenuButton(string label, Action onClick)
        {
            var button = new Button(onClick) { text = label };
            button.AddToClassList("ln-button");
            _menu.Add(button);
        }

        public void SetSettings(string heading, string body, string back, Action onBack)
        {
            _settingsTitle.text = heading;
            _settingsBody.text = body;
            _settingsBack.text = back;
            _settingsBack.clicked += onBack;
        }

        public void ShowSettings(bool visible)
        {
            _settings.EnableInClassList("ln-hidden", !visible);
            _menu.EnableInClassList("ln-hidden", visible);
        }
    }
}
