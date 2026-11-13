using System;
using LuckyNumbers.Model;
using MessagePipe;
using LuckyNumbers.Rendering;
using R3;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The player's settings (GameSettings state; proto settings.proto). Loads them from LocalStore before the first
    /// scene's presenters start, saves each change, and applies the ones that aren't read where they're used: the
    /// render cap (RenderResolutionCap). Look speed and invert are read by InputBridge; show_fps by the page.
    /// The page's Settings panel (web/src/settings.tsx) sends SettingsChanged (the whole set: clamped and checked
    /// here), SettingsReset (Defaults) and SettingsToggled (open / close: GameSettings.open, which the title's
    /// Settings button also sets; InputBridge holds the game's input while it's open).
    /// </summary>
    [Presenter]
    public sealed class SettingsPresenter : IInitializable, IDisposable
    {
        public const float MinSensitivity = 0.25f, MaxSensitivity = 3f;
        const string Key = "ln.settings.";

        readonly GameSettings _state;
        readonly ISubscriber<SettingsChanged> _changed;
        readonly ISubscriber<SettingsReset> _reset;
        readonly ISubscriber<SettingsToggled> _toggled;
        readonly CompositeDisposable _disposables = new();

        [Inject]
        public SettingsPresenter(GameSettings state, ISubscriber<SettingsChanged> changed, ISubscriber<SettingsReset> reset,
                                 ISubscriber<SettingsToggled> toggled)
        {
            _state = state;
            _changed = changed;
            _reset = reset;
            _toggled = toggled;
        }

        public void Initialize()
        {
            _state.LookSensitivity.Value = Mathf.Clamp(LocalStore.GetFloat(Key + "look", 1f), MinSensitivity, MaxSensitivity);
            _state.InvertY.Value = LocalStore.GetInt(Key + "invert_y", 0) != 0;
            var q = (RenderQuality)LocalStore.GetInt(Key + "quality", (int)DefaultQuality);
            _state.Quality.Value = q is RenderQuality.Low or RenderQuality.Medium or RenderQuality.High ? q : DefaultQuality;
            _state.ShowFps.Value = LocalStore.GetInt(Key + "show_fps", 0) != 0;
            _state.Open.Value = false;
            Debug.Log($"[Settings] look x{_state.LookSensitivity.Value:0.00}, invert {_state.InvertY.Value}, " +
                      $"quality {_state.Quality.Value}, fps {_state.ShowFps.Value}");

            // Skip(1): the loaded values aren't changes; each later one is written straight away (a tab can close
            // any time on the web).
            // The slider sends every step of a drag: save once it settles.
            _state.LookSensitivity.Skip(1).Debounce(TimeSpan.FromSeconds(0.4)).Subscribe(v => Save("look", () => LocalStore.SetFloat(Key + "look", v))).AddTo(_disposables);
            _state.InvertY.Skip(1).Subscribe(v => Save("invert_y", () => LocalStore.SetInt(Key + "invert_y", v ? 1 : 0))).AddTo(_disposables);
            _state.ShowFps.Skip(1).Subscribe(v => Save("show_fps", () => LocalStore.SetInt(Key + "show_fps", v ? 1 : 0))).AddTo(_disposables);
            _state.Quality.Subscribe(v => RenderResolutionCap.SetMaxHeight(MaxHeight(v))).AddTo(_disposables);
            _state.Quality.Skip(1).Subscribe(v => Save("quality", () => LocalStore.SetInt(Key + "quality", (int)v))).AddTo(_disposables);

            // The page's panel.
            _changed.Subscribe(e => Apply(e)).AddTo(_disposables);
            _reset.Subscribe(_ => Reset(_state)).AddTo(_disposables);
            _toggled.Subscribe(e => _state.Open.Value = e.Open).AddTo(_disposables);
        }

        /// <summary>The page's whole set: look speed clamped (0.05 steps), an unknown quality ignored.</summary>
        void Apply(SettingsChanged e)
        {
            if (float.IsFinite(e.LookSensitivity))
                _state.LookSensitivity.Value = Mathf.Clamp(Mathf.Round(e.LookSensitivity * 20f) / 20f, MinSensitivity, MaxSensitivity);
            _state.InvertY.Value = e.InvertY;
            if (e.Quality is RenderQuality.Low or RenderQuality.Medium or RenderQuality.High) _state.Quality.Value = e.Quality;
            _state.ShowFps.Value = e.ShowFps;
        }

        /// <summary>Back to the defaults (each change saves as usual).</summary>
        public static void Reset(GameSettings s)
        {
            s.LookSensitivity.Value = 1f;
            s.InvertY.Value = false;
            s.Quality.Value = DefaultQuality;
            s.ShowFps.Value = false;
        }

        /// <summary>Until the player picks one: Medium (720p), or Low (540p) on the low-end tier (LowEnd).</summary>
        public static RenderQuality DefaultQuality => LowEnd.Active ? RenderQuality.Low : RenderQuality.Medium;

        /// <summary>The render target's height cap for a quality (16:9 sets the width).</summary>
        public static int MaxHeight(RenderQuality q) => q switch
        {
            RenderQuality.Low => 540,
            RenderQuality.High => 1080,
            _ => 720,
        };

        static void Save(string what, Action write)
        {
            write();   // LocalStore writes at once
            Debug.Log($"[Settings] saved {what}");
        }

        public void Dispose() => _disposables.Dispose();
    }
}
