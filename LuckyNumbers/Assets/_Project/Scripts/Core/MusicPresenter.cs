using System;
using System.IO;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.Networking;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>Music, app-wide (it carries on, fading, across scene changes).</summary>
    public interface IMusic
    {
        /// <summary>Play <paramref name="track"/> (a file under StreamingAssets), fading to <paramref name="volume"/>.</summary>
        void Play(string track, float volume, float fadeSeconds);
        /// <summary>Fade to silence (the track pauses once faded; Play resumes it).</summary>
        void Stop(float fadeSeconds);
    }

    /// <summary>
    /// Sets MusicState. Tracks are files under StreamingAssets, so they are never part of the Web build's .data
    /// download or wasm heap: on the Web build the page plays them (web/src/music.tsx streams the file through an
    /// &lt;audio&gt; element). Elsewhere (editor, desktop) this loads the same file into an AudioSource on its own
    /// DontDestroyOnLoad object, which also carries the app's AudioListener (no scene has one).
    /// </summary>
    [Presenter]
    public sealed class MusicPresenter : IMusic, ITickable, IDisposable
    {
        readonly MusicState _state;
        AudioSource _source;
        string _loaded;   // the track in _source (or loading)
        float _rate;      // volume per second, toward _state.Volume

        [Inject]
        public MusicPresenter(MusicState state) => _state = state;

        public void Play(string track, float volume, float fadeSeconds)
        {
            _state.Track.Value = track ?? "";
            Fade(Mathf.Clamp01(volume), fadeSeconds);
        }

        public void Stop(float fadeSeconds) => Fade(0f, fadeSeconds);

        void Fade(float volume, float seconds)
        {
            var from = _source != null ? _source.volume : 0f;
            _state.FadeSeconds.Value = Mathf.Max(0f, seconds);
            _state.Volume.Value = volume;
            _rate = Mathf.Abs(volume - from) / Mathf.Max(0.01f, seconds);
        }

        public void Tick()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;   // the page plays it (MusicState over the web bridge)
#else
            var track = _state.Track.Value;
            if (track != _loaded)
            {
                _loaded = track;
                if (_source != null) _source.Stop();
                if (!string.IsNullOrEmpty(track)) LoadAsync(track).Forget();
            }
            if (_source == null || _source.clip == null) return;
            _source.volume = Mathf.MoveTowards(_source.volume, _state.Volume.Value, _rate * Time.unscaledDeltaTime);
            if (_source.volume <= 0f && _state.Volume.Value <= 0f) { if (_source.isPlaying) _source.Pause(); }
            else if (!_source.isPlaying) _source.Play();
#endif
        }

        async UniTaskVoid LoadAsync(string track)
        {
            var url = new Uri(Path.Combine(Application.streamingAssetsPath, track)).AbsoluteUri;
            using var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
            try { await request.SendWebRequest(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Music] {track}: {e.Message}");
                return;
            }
            if (track != _loaded) return;   // replaced while loading
            if (_source == null)
            {
                var go = new GameObject("Music", typeof(AudioListener));
                Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                _source.loop = true;
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;
            }
            if (_source.clip != null) Object.Destroy(_source.clip);
            _source.clip = DownloadHandlerAudioClip.GetContent(request);
            _source.volume = 0f;
            _source.Play();
        }

        public void Dispose()
        {
            if (_source == null) return;
            if (_source.clip != null) Object.Destroy(_source.clip);
            Object.Destroy(_source.gameObject);
        }
    }
}
