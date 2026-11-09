using System;
using UnityEngine;
using UnityEngine.UI;

namespace LuckyNumbers.Title
{
    /// <summary>Title UI references. No logic: TitlePresenter drives it.</summary>
    [View]
    public sealed class TitleView : MonoBehaviour
    {
        [SerializeField] Text title;
        [SerializeField] Text prompt;
        [SerializeField] RectTransform menu;
        [SerializeField] Button buttonTemplate;
        [SerializeField] GameObject settingsPanel;
        [SerializeField] Text settingsTitle;
        [SerializeField] Text settingsBody;
        [SerializeField] Button settingsBack;

        public void SetTitle(string text) => title.text = text;
        public void SetPrompt(string text) => prompt.text = text;

        public void SetPromptAlpha(float alpha)
        {
            var c = prompt.color;
            prompt.color = new Color(c.r, c.g, c.b, alpha);
        }

        public void AddMenuButton(string label, Action onClick)
        {
            var b = Instantiate(buttonTemplate, menu);
            b.gameObject.SetActive(true);
            b.GetComponentInChildren<Text>().text = label;
            b.onClick.AddListener(() => onClick());
        }

        public void SetSettings(string heading, string body, string back, Action onBack)
        {
            settingsTitle.text = heading;
            settingsBody.text = body;
            settingsBack.GetComponentInChildren<Text>().text = back;
            settingsBack.onClick.AddListener(() => onBack());
        }

        public void ShowSettings(bool visible)
        {
            settingsPanel.SetActive(visible);
            menu.gameObject.SetActive(!visible);
        }
    }
}
