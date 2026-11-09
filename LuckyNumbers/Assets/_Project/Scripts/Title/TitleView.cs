using UnityEngine;
using UnityEngine.UI;

namespace LuckyNumbers.Title
{
    /// <summary>Scene references for the title. No logic: TitlePresenter drives it.</summary>
    [View]
    public sealed class TitleView : MonoBehaviour
    {
        [SerializeField] MeshRenderer starfield;
        [SerializeField] MeshRenderer planet;
        [SerializeField] Text title;
        [SerializeField] Text prompt;

        public Material StarfieldMaterial => starfield.material;
        public Material PlanetMaterial => planet.material;
        public Transform Planet => planet.transform;

        public void SetTitle(string text) => title.text = text;
        public void SetPrompt(string text) => prompt.text = text;

        public void SetPromptAlpha(float alpha)
        {
            var c = prompt.color;
            prompt.color = new Color(c.r, c.g, c.b, alpha);
        }
    }
}
