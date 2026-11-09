using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// First-person arms: plays an idle clip (ping-pong, so it never snaps) and adds life on top in LateUpdate:
    /// breathing, a walking swing against the stride, wrists drifting and fingers fidgeting. Takes a sway offset.
    /// </summary>
    [View]
    public sealed class ArmsView : MonoBehaviour
    {
        [SerializeField] Transform pivot;          // under the camera; sway rotates/moves this
        [SerializeField] Animator animator;
        [SerializeField] AnimationClip[] clips;    // from arms_rig.fbx, set by the scene builder
        PlayableGraph _graph;
        AnimationClipPlayable _playable;
        AnimationClip _clip;
        Vector3 _restPosition;
        Quaternion _restRotation;
        readonly Transform[] _upper = new Transform[2], _hand = new Transform[2];
        readonly System.Collections.Generic.List<(Transform bone, int side, float seed)> _fingers = new();
        float _breath, _swing, _fidget, _stride, _moving;
        // Per bone: the pose before our offset, and what we wrote. If the Animator didn't touch a bone this
        // frame (not keyed by the clip), its rotation still equals what we wrote: reuse the base, don't compound.
        readonly System.Collections.Generic.Dictionary<Transform, (Quaternion baseRot, Quaternion written)> _offsets = new();

        void Offset(Transform bone, Quaternion delta)
        {
            var current = bone.localRotation;
            var baseRot = _offsets.TryGetValue(bone, out var o) && current == o.written ? o.baseRot : current;
            var written = baseRot * delta;
            bone.localRotation = written;
            _offsets[bone] = (baseRot, written);
        }

        void Awake()
        {
            _restPosition = pivot.localPosition;
            _restRotation = pivot.localRotation;
            var sides = new[] { "L", "R" };
            foreach (var t in animator.GetComponentsInChildren<Transform>())
            {
                for (var s = 0; s < 2; s++)
                {
                    if (t.name == $"upper_arm.{sides[s]}") _upper[s] = t;
                    if (t.name == $"hand.{sides[s]}") _hand[s] = t;
                    if (t.name.EndsWith($".{sides[s]}") && (t.name.StartsWith("f_") || t.name.StartsWith("thumb")) && !t.name.Contains("_end"))
                        _fingers.Add((t, s, _fingers.Count * 1.618f));
                }
            }
        }

        /// <summary>Strengths (degrees) of the procedural life.</summary>
        public void SetLife(float breath, float swing, float fidget) { _breath = breath; _swing = swing; _fidget = fidget; }

        /// <summary>Stride phase (radians) and how much we're walking (0..1), for the arm swing.</summary>
        public void SetMotion(float stride, float moving) { _stride = stride; _moving = moving; }

        public void Play(string clipName)
        {
            foreach (var c in clips)
                if (c.name == clipName || c.name.EndsWith("|" + clipName)) _clip = c;
            if (_clip == null) { Debug.LogWarning($"[Arms] no clip {clipName}"); return; }
            if (_graph.IsValid()) _graph.Destroy();
            _playable = AnimationPlayableUtilities.PlayClip(animator, _clip, out _graph);
            Debug.Log($"[Arms] {_clip.name}");
        }

        public void SetVisible(bool visible) => pivot.gameObject.SetActive(visible);

        /// <summary>Offset from the rest pose (camera space): bob/sway.</summary>
        public void Pose(Vector3 offset, Quaternion tilt)
        {
            pivot.localPosition = _restPosition + offset;
            pivot.localRotation = tilt * _restRotation;
        }

        void Update()
        {
            // Ping-pong: the clips aren't authored to loop, so playing back and forth never snaps.
            if (_clip != null && _playable.IsValid()) _playable.SetTime(Mathf.PingPong(Time.time, _clip.length));
        }

        void LateUpdate()   // after the Animator has posed the bones
        {
            var t = Time.time;
            var breathe = Mathf.Sin(t * 2f * Mathf.PI * 0.22f);
            for (var s = 0; s < 2; s++)
            {
                var sign = s == 0 ? 1f : -1f;
                var swing = Mathf.Sin(_stride + s * Mathf.PI) * _moving * _swing;   // opposite to each other
                if (_upper[s] != null) Offset(_upper[s], Quaternion.Euler(breathe * _breath + swing, 0f, 0f));
                if (_hand[s] != null)
                    Offset(_hand[s], Quaternion.Euler((Mathf.PerlinNoise(t * 0.4f, s * 3.1f) - 0.5f) * _breath * 2f,
                                                      (Mathf.PerlinNoise(s * 5.7f, t * 0.35f) - 0.5f) * _breath * 2f,
                                                      sign * Mathf.Sin(t * 0.6f + s) * _breath));
            }
            foreach (var (bone, side, seed) in _fingers)
            {
                var curl = (Mathf.PerlinNoise(t * 0.5f + side * 7.3f, seed) - 0.35f) * _fidget;   // mostly curling in a little
                Offset(bone, Quaternion.Euler(curl, 0f, 0f));
            }
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
