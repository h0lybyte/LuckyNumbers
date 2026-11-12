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
        [Ui("settings-reset")] Button _settingsReset;
        [Ui("settings-rows")] VisualElement _settingsRows;
        Slider _sensitivity;
        Label _sensitivityValue;
        Button _invert, _fps;
        Button[] _quality = Array.Empty<Button>();
        string _on = "On", _off = "Off";
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
            // No media queries in USS: when the corner dice panel would reach the centred menu row (wide but short
            // windows, small iframes, portrait phones), it joins the column under the menu instead. Measured, not a
            // fixed width, so it holds whatever size the dice panel is.
            root.RegisterCallback<GeometryChangedEvent>(_ => Relayout());
            _menu.RegisterCallback<GeometryChangedEvent>(_ => Relayout());
            _dicePanel.RegisterCallback<GeometryChangedEvent>(_ => Relayout());
            _bound = true;
        }

        /// <summary>Clearance between the corner dice panel and the menu row (panel units).</summary>
        const float CornerGap = 24f;
        const float CornerInset = 28f;   // .ln-dice-panel left

        void Relayout()
        {
            var root = document.rootVisualElement;
            var width = root.layout.width;
            var menu = _menu.layout.width;
            var dice = _dicePanel.layout.width;
            if (float.IsNaN(width) || float.IsNaN(menu) || float.IsNaN(dice) || width <= 0f || menu <= 0f || dice <= 0f) return;
            // The menu is centred: it starts at (width - menu) / 2; the corner panel ends at inset + its width.
            var narrow = (width - menu) * 0.5f < CornerInset + dice + CornerGap;
            if (root.ClassListContains("ln-narrow") != narrow) root.EnableInClassList("ln-narrow", narrow);
        }

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

        /// <summary>
        /// Build the Settings panel: a look-speed slider, invert and frame-rate toggles, a render-quality choice, then
        /// Defaults and Back. Each control reports through its callback; ShowSettingsValues shows the values.
        /// </summary>
        public void SetSettings(Model.SettingsPanel labels, float minSensitivity, float maxSensitivity,
                                Action<float> onSensitivity, Action onInvert, Action<int> onQuality, Action onFps,
                                Action onReset, Action onBack)
        {
            _settingsTitle.text = labels.Title;
            _settingsBody.text = labels.Body;
            _settingsBody.EnableInClassList("ln-hidden", string.IsNullOrEmpty(labels.Body));
            _settingsBack.text = labels.BackLabel;
            _settingsBack.clicked += onBack;
            _settingsReset.text = labels.ResetLabel;
            _settingsReset.clicked += onReset;
            if (!string.IsNullOrEmpty(labels.OnLabel)) _on = labels.OnLabel;
            if (!string.IsNullOrEmpty(labels.OffLabel)) _off = labels.OffLabel;
            _settingsRows.Clear();

            var look = Row(labels.SensitivityLabel);
            _sensitivity = new Slider(minSensitivity, maxSensitivity);
            _sensitivity.AddToClassList("ln-settings-slider");
            _sensitivity.RegisterValueChangedCallback(e => onSensitivity(e.newValue));
            look.Add(_sensitivity);
            _sensitivityValue = new Label { pickingMode = PickingMode.Ignore };
            _sensitivityValue.AddToClassList("ln-settings-value");
            look.Add(_sensitivityValue);

            _invert = Toggle(Row(labels.InvertLabel), onInvert);

            var quality = Row(labels.QualityLabel);
            var choices = new VisualElement { pickingMode = PickingMode.Ignore };
            choices.AddToClassList("ln-settings-choices");
            quality.Add(choices);
            _quality = new Button[labels.QualityNames.Count];
            for (var i = 0; i < _quality.Length; i++)
            {
                var index = i;
                var b = new Button(() => onQuality(index)) { text = labels.QualityNames[i] };
                b.AddToClassList("ln-button");
                b.AddToClassList("ln-button--small");
                b.AddToClassList("ln-settings-choice");
                choices.Add(b);
                _quality[i] = b;
            }

            _fps = Toggle(Row(labels.FpsLabel), onFps);

            VisualElement Row(string label)
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("ln-settings-row");
                var name = new Label(label) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("ln-settings-label");
                row.Add(name);
                _settingsRows.Add(row);
                return row;
            }

            static Button Toggle(VisualElement row, Action onClick)
            {
                var b = new Button(onClick);
                b.AddToClassList("ln-button");
                b.AddToClassList("ln-button--small");
                b.AddToClassList("ln-settings-toggle");
                row.Add(b);
                return b;
            }
        }

        /// <summary>Show the current values (quality: index into the panel's quality names).</summary>
        public void ShowSettingsValues(float sensitivity, bool invert, int quality, bool fps)
        {
            if (_sensitivity == null) return;
            _sensitivity.SetValueWithoutNotify(sensitivity);
            _sensitivityValue.text = $"{sensitivity:0.00}x";
            SetToggle(_invert, invert);
            SetToggle(_fps, fps);
            for (var i = 0; i < _quality.Length; i++) _quality[i].EnableInClassList("ln-selected", i == quality);

            void SetToggle(Button b, bool on)
            {
                b.text = on ? _on : _off;
                b.EnableInClassList("ln-selected", on);
            }
        }

        public void ShowSettings(bool visible)
        {
            _settings.EnableInClassList("ln-hidden", !visible);
            _menu.EnableInClassList("ln-hidden", visible);
            _title.EnableInClassList("ln-hidden", visible);        // the panel is tall: nothing behind it
            _prompt.EnableInClassList("ln-hidden", visible);
            _dicePanel.EnableInClassList("ln-hidden", visible);
            if (visible) _settingsBack.Focus();                    // keyboard / gamepad start on the panel
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
