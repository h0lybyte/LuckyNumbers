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
        [Ui("credits")] VisualElement _credits;
        [Ui("credits-title")] Label _creditsTitle;
        [Ui("credits-intro")] Label _creditsIntro;
        [Ui("credits-list")] ScrollView _creditsList;
        [Ui("credits-back")] Button _creditsBack;
        [Ui("dice-panel")] VisualElement _dicePanel;
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

        /// <summary>Menu row (3 x 244) plus a dice panel (~290 incl. margin) on each side, so it stays centred.</summary>
        const float NarrowWidth = 1320f;

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

        /// <summary>Fill the Credits panel: a heading per section, then each credit (click to open its link).</summary>
        public void SetCredits(Model.CreditsConfig credits, Action onBack, Action<string> onLink)
        {
            _creditsTitle.text = credits.Title;
            _creditsIntro.text = credits.Intro;
            _creditsBack.text = credits.BackLabel;
            _creditsBack.clicked += onBack;
            _creditsList.Clear();
            foreach (var section in credits.Sections)
            {
                var heading = new Label(section.Name);
                heading.AddToClassList("ln-credit-section");
                _creditsList.Add(heading);
                foreach (var c in section.Credits)
                {
                    var url = c.Url;
                    var entry = new Button(() => { if (!string.IsNullOrEmpty(url)) onLink(url); });
                    entry.AddToClassList("ln-credit");
                    var head = $"{c.What}: <b>{c.Title}</b>" + (string.IsNullOrEmpty(c.Author) ? "" : $" by {c.Author}");
                    Add(entry, head, "ln-credit-head");
                    if (!string.IsNullOrEmpty(c.License)) Add(entry, c.License, "ln-credit-detail");
                    if (!string.IsNullOrEmpty(c.Notes)) Add(entry, c.Notes, "ln-credit-detail");
                    if (!string.IsNullOrEmpty(url)) Add(entry, url, "ln-credit-link");
                    _creditsList.Add(entry);
                }
            }

            static void Add(VisualElement parent, string text, string style)
            {
                var label = new Label(text) { enableRichText = true };
                label.AddToClassList(style);
                parent.Add(label);
            }
        }

        public void ShowCredits(bool visible)
        {
            _credits.EnableInClassList("ln-hidden", !visible);
            _menu.EnableInClassList("ln-hidden", visible);
            _title.EnableInClassList("ln-hidden", visible);       // the panel is tall: nothing behind it
            _prompt.EnableInClassList("ln-hidden", visible);
            _dicePanel.EnableInClassList("ln-hidden", visible);
            if (visible) _creditsList.scrollOffset = Vector2.zero;
        }
    }
}
