using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A modelled monster's skeleton and clips (Art/Characters/&lt;model&gt;, set by the scene builder on a template
    /// PlanetView keeps off; MonsterView copies it per monster). Plays one clip at a time, cross-faded, through a
    /// PlayableGraph: looping (idle, walks: wrapped here, whatever the import's loop flag) or once (holding the last
    /// frame). The bake strips root motion, so clips play in place and the presenter moves the body; `speeds` (the
    /// bake's aberration.json) are each clip's stride in m/s, so a walk plays at the rate the body moves.
    /// `vat`: the same clips baked to vertex animation textures (Editor/VatBaker); with it, MonsterView leaves this
    /// template unused in play and MonsterCrowd draws the monster instanced. This graph stays the fallback (and the bake's
    /// source: the baker poses the rig through <see cref="Pose"/>).
    /// </summary>
    public sealed class MonsterRig : MonoBehaviour
    {
        [SerializeField] Animator animator;
        [SerializeField] AnimationClip[] clips = System.Array.Empty<AnimationClip>();
        [SerializeField] float[] speeds = System.Array.Empty<float>();   // per clip, m/s (0: in place)
        [SerializeField] MonsterVat vat;

        public MonsterVat Vat => vat;
        public void SetVat(MonsterVat v) => vat = v;

        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;
        AnimationClipPlayable[] _playables;
        float[] _weights;
        int _current = -1;
        bool _once;
        float _fade = 0.2f;

        public void Init(Animator a, AnimationClip[] c, float[] s)
        {
            animator = a;
            clips = c;
            speeds = s;
        }

        int Index(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return -1;
            for (var i = 0; i < clips.Length; i++) if (clips[i] != null && Short(clips[i].name) == clip) return i;
            return -1;
        }

        /// <summary>A take's clip name without its object: Blender's FBX takes are "Armature|walk_fast".</summary>
        public static string Short(string take) => take[(take.LastIndexOf('|') + 1)..];

        public bool Has(string clip) => Index(clip) >= 0;

        /// <summary>The clip itself (null if it has none): the VAT bake reads its loop flag.</summary>
        public AnimationClip Find(string clip)
        {
            var i = Index(clip);
            return i < 0 ? null : clips[i];
        }

        /// <summary>Every clip's short name, in order.</summary>
        public System.Collections.Generic.IEnumerable<string> ClipNames
        {
            get { foreach (var c in clips) if (c != null) yield return Short(c.name); }
        }

        /// <summary>The clip's length in seconds (0 if it has none).</summary>
        public float Length(string clip)
        {
            var i = Index(clip);
            return i < 0 ? 0f : clips[i].length;
        }

        /// <summary>The clip's stride speed, m/s (0: in place or none).</summary>
        public float Stride(string clip)
        {
            var i = Index(clip);
            return i < 0 || i >= speeds.Length ? 0f : speeds[i];
        }

        /// <summary>
        /// Play `clip` (fading over `fade` s), at `rate` x. `once`: from its start, holding the last frame; else it
        /// loops, and asking for the clip already playing only changes its rate.
        /// </summary>
        public void Play(string clip, float rate = 1f, bool once = false, float fade = 0.2f)
        {
            var i = Index(clip);
            if (i < 0 || !Build()) return;
            if (i == _current && !once) { _playables[i].SetSpeed(rate); return; }
            _current = i;
            _once = once;
            _fade = Mathf.Max(0.01f, fade);
            var p = _playables[i];
            if (once || _weights[i] < 0.01f) p.SetTime(0d);
            p.SetSpeed(rate);
        }

        /// <summary>Edit mode (previews): `clip` at `time` s, through the same graph the game plays.</summary>
        public void Pose(string clip, float time)
        {
            var i = Index(clip);
            if (i < 0 || !Build()) return;
            for (var k = 0; k < _weights.Length; k++) { _weights[k] = k == i ? 1f : 0f; _mixer.SetInputWeight(k, _weights[k]); }
            _playables[i].SetTime(time);
            _graph.Evaluate(0f);
        }

        bool Build()
        {
            if (_graph.IsValid()) return true;
            if (animator == null || clips.Length == 0) return false;
            animator.applyRootMotion = false;
            _graph = PlayableGraph.Create($"Monster {name}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _mixer = AnimationMixerPlayable.Create(_graph, clips.Length);
            _playables = new AnimationClipPlayable[clips.Length];
            _weights = new float[clips.Length];
            for (var i = 0; i < clips.Length; i++)
            {
                _playables[i] = AnimationClipPlayable.Create(_graph, clips[i]);
                _playables[i].SetApplyFootIK(false);
                _graph.Connect(_playables[i], 0, _mixer, i);
                _mixer.SetInputWeight(i, 0f);
            }
            AnimationPlayableOutput.Create(_graph, "Monster", animator).SetSourcePlayable(_mixer);
            _graph.Play();
            return true;
        }

        void Update()
        {
            if (!_graph.IsValid() || _current < 0) return;
            var step = Time.deltaTime / _fade;
            for (var i = 0; i < _weights.Length; i++)
            {
                var target = i == _current ? 1f : 0f;
                _weights[i] = Mathf.MoveTowards(_weights[i], target, step);
                _mixer.SetInputWeight(i, _weights[i]);
            }
            var p = _playables[_current];
            var length = clips[_current].length;
            var t = p.GetTime();
            if (length <= 0f) return;
            if (_once && t >= length) { p.SetTime(length); p.SetSpeed(0d); }   // holds the last frame
            else if (!_once && t >= length) p.SetTime(t % length);
        }

        /// <summary>Drop the graph (edit mode: nothing calls OnDestroy there).</summary>
        public void Release()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }

        void OnDestroy() => Release();
    }
}
