using LuckyNumbers.Hands;
using LuckyNumbers.Model;
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

        // IK (content/hands/rig.txtpb): resolved arms, per-side reach requests, last solve error.
        Arm[] _rigArms = System.Array.Empty<Arm>();
        HandRig _rig;
        readonly System.Collections.Generic.Dictionary<Transform, Quaternion> _bind = new();
        readonly Vector3[] _target = new Vector3[2], _palmFacing = new Vector3[2], _fingersAlong = new Vector3[2];
        readonly float[] _weight = new float[2], _flatten = new float[2];
        public readonly float[] LastError = new float[2];
        const float MaxLean = 0.22f;  // metres the arms rig may lean toward a touch (more shows the arm mesh's cut ends)

        /// <summary>Resolve the bone map; false (and an error log) if any bone is missing.</summary>
        public bool Init(HandRig rig)
        {
            _rig = rig;
            _rigArms = HandRigMap.Resolve(rig, animator.transform, out var missing);
            foreach (var a in _rigArms)
                foreach (var f in a.Fingers)
                    for (var j = 0; j < f.Joints.Length; j++)
                        if (f.Joints[j] != null && _bind.TryGetValue(f.Joints[j], out var q)) f.BindJoints[j] = q;
            if (missing.Count > 0) { Debug.LogError($"[Hands] rig map: missing bones: {string.Join(", ", missing)}"); return false; }
            var fingers = 0;
            foreach (var a in _rigArms) fingers += a.Fingers.Length;
            Debug.Log($"[Hands] rig map OK: {_rigArms.Length} arms, {fingers} fingers, reach {_rigArms[0].Reach:0.###} m " +
                      $"(upper {_rigArms[0].UpperLength:0.###} + lower {_rigArms[0].LowerLength:0.###})");
            return true;
        }

        public int ArmCount => _rigArms.Length;
        public Vector3 ShoulderPosition(int side) => _rigArms[side].Upper.position;
        public float Reach(int side) => _rigArms[side].Reach;

        /// <summary>Ask a hand (wrist) to reach a world point this frame; optionally lay the palm on a surface.</summary>
        public void ReachTo(int side, Vector3 target, float weight, Vector3 palmFacing = default, Vector3 fingersAlong = default, float flatten = 0f)
        {
            _target[side] = target;
            _weight[side] = Mathf.Clamp01(weight);
            _palmFacing[side] = palmFacing;
            _fingersAlong[side] = fingersAlong;
            _flatten[side] = flatten;
        }

        void Awake()
        {
            _restPosition = pivot.localPosition;
            _restRotation = pivot.localRotation;
            foreach (var t in animator.GetComponentsInChildren<Transform>(true)) _bind[t] = t.localRotation;   // bind pose, before any clip
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
                var curl = (Mathf.PerlinNoise(t * 0.5f + side * 7.3f, seed) - 0.35f) * _fidget * (1f - _flatten[side]);
                Offset(bone, Quaternion.Euler(curl, 0f, 0f));
            }

            // Lean in: for targets past plain arm's reach, move the whole rig toward them (up to MaxLean).
            var lean = Vector3.zero;
            var leaning = 0;
            for (var s = 0; s < _rigArms.Length && s < 2; s++)
            {
                if (_weight[s] <= 0f) continue;
                var arm = _rigArms[s];
                var to = _target[s] - arm.Upper.position;
                var excess = to.magnitude - arm.Reach * 0.92f;
                if (excess > 0f) { lean += to.normalized * (Mathf.Min(excess, MaxLean) * _weight[s]); leaning++; }
            }
            if (leaning > 0) pivot.position += lean / leaning;

            // IK last, on top of clip + life.
            for (var s = 0; s < _rigArms.Length && s < 2; s++)
            {
                if (_weight[s] <= 0f) { LastError[s] = 0f; continue; }
                var arm = _rigArms[s];
                LastError[s] = TwoBoneIK.Solve(arm, _target[s], _weight[s], _rig.MaxReach, _rig.ShrugDegrees);
                if (_palmFacing[s] != Vector3.zero) TwoBoneIK.OrientHand(arm, _palmFacing[s], _fingersAlong[s], _weight[s]);
                TwoBoneIK.Flatten(arm, _flatten[s] * _weight[s]);
            }
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
