using System.Collections.Generic;
using LuckyNumbers.Hands;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// First-person arms, built up each frame in layers:
    ///  1. clips: a base layer for both arms (crossfades, e.g. relax / run) and one masked layer per arm
    ///     (one-shots like a throw) over it
    ///  2. life: breathing, stride swing (pumped when running), arms rising in the air, wrists drifting, tremor
    ///  3. IK: hands reach world points (touch, grab, carry); Reaching tells the body how far to lean in
    ///  4. finger poses (content/hands/poses/*), then fidget showing through as the pose allows
    /// Rendered by the viewmodel camera, so they never clip into walls.
    /// </summary>
    [View]
    public sealed class ArmsView : MonoBehaviour
    {
        [SerializeField] Transform pivot;          // under the camera; sway rotates/moves this
        [SerializeField] Animator animator;
        [SerializeField] AnimationClip[] clips;    // from arms_rig.fbx, set by the scene builder

        PlayableGraph _graph;
        AnimationLayerMixerPlayable _layers;
        readonly ClipLayer[] _clipLayers = new ClipLayer[3];   // 0 both arms, 1 + side: that arm only
        readonly float[] _sideWeight = new float[2];

        HandRig _rig;
        Arm[] _arms = System.Array.Empty<Arm>();
        HandPoser[] _posers = System.Array.Empty<HandPoser>();
        readonly Dictionary<Transform, Quaternion> _bind = new();
        // Per bone: the pose before our offset, and what we wrote. If the Animator didn't touch a bone this
        // frame (not keyed by the clip), its rotation still equals what we wrote: reuse the base, don't compound.
        readonly Dictionary<Transform, (Quaternion baseRot, Quaternion written)> _offsets = new();
        Vector3 _restPosition;
        Quaternion _restRotation;

        float _breathPhase, _breath, _swing, _fidget, _stride, _moving, _sprint, _air, _airRaise, _pump = 1f, _tremor;
        readonly Vector3[] _target = new Vector3[2], _palmFacing = new Vector3[2], _fingersAlong = new Vector3[2];
        readonly float[] _weight = new float[2];
        public readonly float[] LastError = new float[2];

        public int ArmCount => _arms.Length;
        /// <summary>World offset the hands' targets are past a comfortable reach (averaged; negative along = closer).</summary>
        public Vector3 Reaching { get; private set; }
        public bool IsReaching { get; private set; }
        public Vector3 ShoulderPosition(int side) => _arms[side].Upper.position;
        public float Reach(int side) => _arms[side].Reach;
        public string CurrentPose(int side) => _posers.Length > side ? _posers[side].Current : null;
        /// <summary>
        /// Where something `radius` metres round rests on the palm, in the hand bone's own space: the middle of the
        /// palm, lifted off the skin by its radius. Constant for the hand, so a held thing parented there rides
        /// with it exactly, whatever the animation and IK do this frame.
        /// </summary>
        public Vector3 PalmRest(int side, float radius)
        {
            var a = _arms[side];
            var world = a.Hand.position + a.Hand.TransformDirection(a.FingerAxis) * 0.075f
                      + a.Hand.TransformDirection(a.PalmAxis) * (0.02f + radius);
            return a.Hand.InverseTransformPoint(world);
        }
        public Transform HandBone(int side) => _arms[side].Hand;

        void Awake()
        {
            _restPosition = pivot.localPosition;
            _restRotation = pivot.localRotation;
            foreach (var t in animator.GetComponentsInChildren<Transform>(true)) _bind[t] = t.localRotation;   // bind pose, before any clip
            _graph = PlayableGraph.Create("Arms");
            _layers = AnimationLayerMixerPlayable.Create(_graph, _clipLayers.Length);
            for (var i = 0; i < _clipLayers.Length; i++)
            {
                _clipLayers[i] = new ClipLayer(_graph);
                _graph.Connect(_clipLayers[i].Mixer, 0, _layers, i);
                _layers.SetInputWeight(i, i == 0 ? 1f : 0f);
            }
            AnimationPlayableOutput.Create(_graph, "Arms", animator).SetSourcePlayable(_layers);
            _graph.Play();
        }

        /// <summary>Resolve the bone map (false + an error log if bones are missing); measure the fingers; mask the arm layers.</summary>
        public bool Init(HandRig rig)
        {
            _rig = rig;
            // Measure in the bind pose (symmetric, unlike whatever the clip shows now); the next frame re-poses.
            foreach (var (bone, rotation) in _bind) bone.localRotation = rotation;
            _arms = HandRigMap.Resolve(rig, animator.transform, out var missing);
            if (missing.Count > 0) { Debug.LogError($"[Hands] rig map: missing bones: {string.Join(", ", missing)}"); _arms = System.Array.Empty<Arm>(); return false; }
            _posers = new HandPoser[_arms.Length];
            for (var s = 0; s < _arms.Length; s++)
            {
                var arm = _arms[s];
                var bent = HandRigMap.MeasureFingers(arm);
                Debug.Log($"[Hands] {arm.Side} fingers straightened from the bind pose (most bent finger {bent:0} deg)");
                _posers[s] = new HandPoser(arm, rig.Poses);
                if (s < 2) _layers.SetLayerMaskFromAvatarMask((uint)(1 + s), SideMask(arm));
            }
            var fingers = 0;
            foreach (var a in _arms) fingers += a.Fingers.Length;
            Debug.Log($"[Hands] rig map OK: {_arms.Length} arms, {fingers} fingers, {rig.Poses.Count} poses, reach {_arms[0].Reach:0.###} m " +
                      $"(upper {_arms[0].UpperLength:0.###} + lower {_arms[0].LowerLength:0.###})");
            return true;
        }

        /// <summary>Everything from the shoulder down (plus the arm's IK helpers): what that arm's layer drives.</summary>
        AvatarMask SideMask(Arm arm)
        {
            var all = animator.GetComponentsInChildren<Transform>(true);
            var mask = new AvatarMask { transformCount = all.Length };
            for (var i = 0; i < all.Length; i++)
            {
                var path = "";
                for (var t = all[i]; t != animator.transform; t = t.parent) path = path.Length == 0 ? t.name : $"{t.name}/{path}";
                mask.SetTransformPath(i, path);
                mask.SetTransformActive(i, all[i].IsChildOf(arm.Shoulder) || all[i] == arm.Target || all[i] == arm.Pole);
            }
            return mask;
        }

        AnimationClip Clip(string name, int side = -1)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (side >= 0 && _rig != null && side < _rig.Sides.Count) name = name.Replace("{side}", _rig.Sides[side]);
            foreach (var c in clips)
                if (c.name == name || c.name.EndsWith("|" + name)) return c;
            Debug.LogWarning($"[Arms] no clip {name}");
            return null;
        }

        /// <summary>Base layer, both arms: crossfade to a clip played back and forth (they aren't authored to loop).</summary>
        public void Play(string clip, float fade = 0.3f)
        {
            var c = Clip(clip);
            if (c != null && _clipLayers[0].Play(_graph, c, fade, once: false)) Debug.Log($"[Arms] {c.name}");
        }

        /// <summary>One arm plays a clip once over the base layer, then hands back. Returns its length (0 = none).</summary>
        public float PlayOnce(int side, string clip, float fade = 0.12f)
        {
            var c = Clip(clip, side);
            if (c == null || side > 1) return 0f;
            _clipLayers[1 + side].Play(_graph, c, fade, once: true);
            Debug.Log($"[Arms] {c.name} ({_rig.Sides[side]} arm)");
            return c.length;
        }

        /// <summary>Strengths (degrees) of the procedural life.</summary>
        public void SetLife(float breath, float swing, float fidget) { _breath = breath; _swing = swing; _fidget = fidget; }

        /// <summary>Running pumps the swing (x walk); arms rise this much (degrees) when fully airborne.</summary>
        public void SetFeel(float sprintPump, float airRaise) { _pump = sprintPump; _airRaise = airRaise; }

        /// <summary>Stride phase (radians), how much we're walking, running and airborne (0..1), hand tremor (degrees).</summary>
        public void SetMotion(float stride, float moving, float sprint, float air, float tremor)
        {
            _stride = stride; _moving = moving; _sprint = sprint; _air = air; _tremor = tremor;
        }

        /// <summary>Ask a hand (wrist) to reach a world point this frame; optionally turn the palm to face a direction.</summary>
        public void ReachTo(int side, Vector3 target, float weight, Vector3 palmFacing = default, Vector3 fingersAlong = default)
        {
            _target[side] = target;
            _weight[side] = Mathf.Clamp01(weight);
            _palmFacing[side] = palmFacing;
            _fingersAlong[side] = fingersAlong;
        }

        /// <summary>Blend a hand toward a finger pose (content/hands/poses/*; null = the clip's own fingers).</summary>
        public void SetPose(int side, string pose, float weight = 1f)
        {
            if (side < _posers.Length && _posers[side].Current != pose) _posers[side].Set(pose, weight);
        }

        /// <summary>Put a thing in a hand: it rides the palm and renders with the arms (no clipping).</summary>
        public void Hold(int side, Transform item)
        {
            item.SetParent(_arms[side].Hand, true);
            SetLayer(item, gameObject.layer);
        }

        public static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t) SetLayer(c, layer);
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
            var dt = Time.deltaTime;
            _clipLayers[0].Update(dt);
            for (var s = 0; s < 2; s++)
            {
                var layer = _clipLayers[1 + s];
                layer.Update(dt);
                // An arm's layer fades in for its one-shot and back out as the clip ends.
                var want = layer.Playing ? 1f : 0f;
                _sideWeight[s] = Mathf.MoveTowards(_sideWeight[s], want, dt / Mathf.Max(0.05f, layer.Fade));
                _layers.SetInputWeight(1 + s, _sideWeight[s]);
            }
        }

        void Offset(Transform bone, Quaternion delta)
        {
            var current = bone.localRotation;
            var baseRot = _offsets.TryGetValue(bone, out var o) && current == o.written ? o.baseRot : current;
            var written = baseRot * delta;
            bone.localRotation = written;
            _offsets[bone] = (baseRot, written);
        }

        /// <summary>A world-space rotation as a local delta for Offset.</summary>
        static Quaternion World(Transform bone, Quaternion world) => Quaternion.Inverse(bone.rotation) * world * bone.rotation;

        void LateUpdate()   // after the Animator has posed the bones
        {
            if (_arms.Length == 0) return;
            var t = Time.time;
            var dt = Time.deltaTime;
            // Breathe faster when running. Accumulate the phase: sin(t * freq) jumps when freq eases (sprint -> idle).
            _breathPhase = (_breathPhase + dt * 2f * Mathf.PI * (0.22f + 0.2f * _sprint)) % (2f * Mathf.PI);
            var breathe = Mathf.Sin(_breathPhase);
            for (var s = 0; s < _arms.Length && s < 2; s++)
            {
                var arm = _arms[s];
                var outward = s == 0 ? -1f : 1f;   // L, R
                var swing = Mathf.Sin(_stride + s * Mathf.PI) * _moving * _swing * Mathf.Lerp(1f, _pump, _sprint);   // opposite arms
                var shake = _tremor * (Mathf.PerlinNoise(t * 5f, s * 4.3f) - 0.5f) * 2f;
                // Airborne: arms come up and out for balance.
                var air = Quaternion.AngleAxis(-_air * _airRaise, pivot.right) * Quaternion.AngleAxis(outward * _air * _airRaise * 0.6f, pivot.up);
                Offset(arm.Upper, World(arm.Upper, air) * Quaternion.Euler(breathe * _breath + swing + shake * 0.3f, 0f, 0f));
                Offset(arm.Hand, Quaternion.Euler((Mathf.PerlinNoise(t * 0.4f, s * 3.1f) - 0.5f) * _breath * 2f + shake,
                                                  (Mathf.PerlinNoise(s * 5.7f, t * 0.35f) - 0.5f) * _breath * 2f + shake * 0.7f,
                                                  -outward * Mathf.Sin(t * 0.6f + s) * _breath));
            }

            // How far past a comfortable reach the hands' targets are (signed: negative = closer). The body leans by
            // it (FirstPersonPresenter moves the head): moving the arms rig itself forward shows the open ends of
            // the arm mesh, which sit just behind the camera.
            var reaching = Vector3.zero;
            var reachers = 0;
            for (var s = 0; s < _arms.Length && s < 2; s++)
            {
                if (_weight[s] <= 0f) continue;
                var to = _target[s] - _arms[s].Upper.position;
                reaching += to.normalized * ((to.magnitude - _arms[s].Reach * 0.92f) * _weight[s]);
                reachers++;
            }
            Reaching = reachers > 0 ? reaching / reachers : Vector3.zero;
            IsReaching = reachers > 0;

            for (var s = 0; s < _arms.Length && s < 2; s++)
            {
                var arm = _arms[s];
                if (_weight[s] > 0f)
                {
                    LastError[s] = TwoBoneIK.Solve(arm, _target[s], _weight[s], _rig.MaxReach, _rig.ShrugDegrees);
                    if (_palmFacing[s] != Vector3.zero) TwoBoneIK.OrientHand(arm, _palmFacing[s], _fingersAlong[s], _weight[s]);
                }
                else LastError[s] = 0f;

                _posers[s].Apply(dt, _rig.PoseSpeed);
                var fidget = _fidget * _posers[s].Fidget;
                for (var i = 0; i < arm.Fingers.Length; i++)
                {
                    var f = arm.Fingers[i];
                    if (f.CurlAxes == null) continue;
                    for (var j = 0; j < f.Joints.Length; j++)
                    {
                        var curl = (Mathf.PerlinNoise(t * 0.5f + s * 7.3f, i * 1.618f + j * 0.37f) - 0.35f) * fidget;
                        Offset(f.Joints[j], Quaternion.AngleAxis(curl, f.CurlAxes[j]));
                    }
                }
            }
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }

        /// <summary>A few clip slots crossfading into each other; times are driven here (ping-pong or once).</summary>
        sealed class ClipLayer
        {
            const int Slots = 3;
            public readonly AnimationMixerPlayable Mixer;
            readonly AnimationClipPlayable[] _slot = new AnimationClipPlayable[Slots];
            readonly float[] _weight = new float[Slots], _start = new float[Slots];
            readonly bool[] _once = new bool[Slots];
            int _current = -1;
            public float Fade { get; private set; } = 0.2f;
            /// <summary>Still playing a one-shot (false once it ends: the layer above should fade out).</summary>
            public bool Playing => _current >= 0 && _once[_current] &&
                                   Time.time - _start[_current] < _slot[_current].GetAnimationClip().length - Fade;

            public ClipLayer(PlayableGraph graph) => Mixer = AnimationMixerPlayable.Create(graph, Slots);

            /// <returns>False if that clip was already playing (looping).</returns>
            public bool Play(PlayableGraph graph, AnimationClip clip, float fade, bool once)
            {
                if (!once && _current >= 0 && !_once[_current] && _slot[_current].GetAnimationClip() == clip) return false;
                var s = _current == 0 ? 1 : 0;   // the quietest slot that isn't the current one
                for (var i = 0; i < Slots; i++) if (i != _current && _weight[i] < _weight[s]) s = i;
                if (_slot[s].IsValid()) { graph.Disconnect(Mixer, s); _slot[s].Destroy(); }
                _slot[s] = AnimationClipPlayable.Create(graph, clip);
                _slot[s].SetSpeed(0);   // time set by hand in Update
                graph.Connect(_slot[s], 0, Mixer, s);
                _weight[s] = _current < 0 ? 1f : 0f;
                _start[s] = Time.time;
                _once[s] = once;
                _current = s;
                Fade = Mathf.Max(0.01f, fade);
                return true;
            }

            public void Update(float dt)
            {
                var total = 0f;
                for (var i = 0; i < Slots; i++)
                {
                    if (!_slot[i].IsValid()) continue;
                    _weight[i] = Mathf.MoveTowards(_weight[i], i == _current ? 1f : 0f, dt / Fade);
                    total += _weight[i];
                    var length = _slot[i].GetAnimationClip().length;
                    var time = Time.time - _start[i];
                    _slot[i].SetTime(_once[i] ? Mathf.Min(time, length) : Mathf.PingPong(time, length));
                }
                for (var i = 0; i < Slots; i++)
                    if (_slot[i].IsValid()) Mixer.SetInputWeight(i, total > 0f ? _weight[i] / total : 0f);
            }
        }
    }
}
