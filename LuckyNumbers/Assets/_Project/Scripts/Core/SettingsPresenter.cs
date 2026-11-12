using System;
using LuckyNumbers.Model;
using LuckyNumbers.Rendering;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The player's settings (GameSettings state; proto settings.proto). Loads them from PlayerPrefs before the first
    /// scene's presenters start, saves each change, and applies the ones that aren't read where they're used: the
    /// render cap (RenderResolutionCap). Look speed and invert are read by InputBridge; show_fps by the page.
    /// </summary>
    [Presenter]
    public sealed class SettingsPresenter : IInitializable, IDisposable
    {
        public const float MinSensitivity = 0.25f, MaxSensitivity = 3f;
        const string Key = "ln.settings.";

        readonly GameSettings _state;
        readonly CompositeDisposable _disposables = new();

        [Inject]
        public SettingsPresenter(GameSettings state) => _state = state;

        public void Initialize()
        {
            _state.LookSensitivity.Value = Mathf.Clamp(PlayerPrefs.GetFloat(Key + "look", 1f), MinSensitivity, MaxSensitivity);
            _state.InvertY.Value = PlayerPrefs.GetInt(Key + "invert_y", 0) != 0;
            var q = (RenderQuality)PlayerPrefs.GetInt(Key + "quality", (int)RenderQuality.Medium);
            _state.Quality.Value = q is RenderQuality.Low or RenderQuality.Medium or RenderQuality.High ? q : RenderQuality.Medium;
            _state.ShowFps.Value = PlayerPrefs.GetInt(Key + "show_fps", 0) != 0;
            Debug.Log($"[Settings] look x{_state.LookSensitivity.Value:0.00}, invert {_state.InvertY.Value}, " +
                      $"quality {_state.Quality.Value}, fps {_state.ShowFps.Value}");

            // Skip(1): the loaded values aren't changes; each later one is written straight away (a tab can close
            // any time on the web).
            // The slider sends every step of a drag: save once it settles.
            _state.LookSensitivity.Skip(1).Debounce(TimeSpan.FromSeconds(0.4)).Subscribe(v => Save("look", () => PlayerPrefs.SetFloat(Key + "look", v))).AddTo(_disposables);
            _state.InvertY.Skip(1).Subscribe(v => Save("invert_y", () => PlayerPrefs.SetInt(Key + "invert_y", v ? 1 : 0))).AddTo(_disposables);
            _state.ShowFps.Skip(1).Subscribe(v => Save("show_fps", () => PlayerPrefs.SetInt(Key + "show_fps", v ? 1 : 0))).AddTo(_disposables);
            _state.Quality.Subscribe(v => RenderResolutionCap.SetMaxHeight(MaxHeight(v))).AddTo(_disposables);
            _state.Quality.Skip(1).Subscribe(v => Save("quality", () => PlayerPrefs.SetInt(Key + "quality", (int)v))).AddTo(_disposables);
        }

        /// <summary>Back to the defaults (each change saves as usual).</summary>
        public static void Reset(GameSettings s)
        {
            s.LookSensitivity.Value = 1f;
            s.InvertY.Value = false;
            s.Quality.Value = RenderQuality.Medium;
            s.ShowFps.Value = false;
        }

        /// <summary>The render target's height cap for a quality (16:9 sets the width).</summary>
        public static int MaxHeight(RenderQuality q) => q switch
        {
            RenderQuality.Low => 540,
            RenderQuality.High => 1080,
            _ => 720,
        };

        static void Save(string what, Action write)
        {
            write();
            PlayerPrefs.Save();
            Debug.Log($"[Settings] saved {what}");
        }

        public void Dispose() => _disposables.Dispose();
    }
}
