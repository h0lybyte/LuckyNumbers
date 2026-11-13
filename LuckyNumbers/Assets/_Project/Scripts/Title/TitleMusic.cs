using UnityEngine;

namespace LuckyNumbers.Title
{
    /// <summary>
    /// The title's music: one looped 2D track under the menu, character creation and the hangar (all in this scene).
    /// Fades in on start and out when Play leaves (TitlePresenter.LeaveAsync). On the Web build the browser holds the
    /// audio until the page's first click or key press, then Unity starts it (Web Audio; no worker needed).
    /// The scene has no other listener, so it carries the AudioListener too. Built by SceneBuilder.BuildTitle.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class TitleMusic : MonoBehaviour
    {
        [SerializeField] AudioClip clip;
        [SerializeField, Range(0f, 1f)] float volume = 0.6f;
        [SerializeField] float fadeInSeconds = 2f;

        AudioSource _source;
        float _target, _rate;

        void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.clip = clip;
            _source.loop = true;
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = 0f;
            _target = volume;
            _rate = volume / Mathf.Max(0.01f, fadeInSeconds);
            if (clip != null) _source.Play();
        }

        /// <summary>Fade to silence over <paramref name="seconds"/> (the scene change cuts whatever is left).</summary>
        public void FadeOut(float seconds)
        {
            _target = 0f;
            _rate = Mathf.Max(_source.volume, 0.01f) / Mathf.Max(0.01f, seconds);
        }

        void Update()
        {
            _source.volume = Mathf.MoveTowards(_source.volume, _target, _rate * Time.unscaledDeltaTime);
        }
    }
}
