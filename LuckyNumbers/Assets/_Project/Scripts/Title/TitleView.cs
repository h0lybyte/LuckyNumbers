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
            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore; // only buttons/panels catch the pointer
            BindUi(root);
            // No media queries in USS: below NarrowWidth (panel units) the dice panel joins the column under the
            // menu instead of sitting in the corner, where it would cover the buttons (portrait phones, small iframes).
            root.RegisterCallback<GeometryChangedEvent>(e => root.EnableInClassList("ln-narrow", e.newRect.width < NarrowWidth));
            _bound = true;
        }

        /// <summary>Menu row (2 x 244) plus a dice panel (~290 incl. margin) on each side, so it stays centred.</summary>
        const float NarrowWidth = 1080f;

        public void SetTitle(string text) => _title.text = text;
        public void SetPrompt(string text) => _prompt.text = text;
        public void SetPromptAlpha(float alpha) => _prompt.style.opacity = alpha;
        /// <summary>Whole title UI (menu, dice, prompt) opacity, for the Play transition.</summary>
        public void SetUiAlpha(float alpha) => document.rootVisualElement.style.opacity = alpha;

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
