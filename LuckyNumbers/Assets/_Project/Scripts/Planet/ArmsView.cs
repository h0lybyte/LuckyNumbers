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
    ///  5. grasp (SetGrasp): fingers curl from straight until they touch what the hand holds (its colliders on this
    ///     layer), so they wrap its real shape instead of a fixed pose's angles. Solved live until it settles, then
    ///     baked (per held thing and hand) and replayed: the hand sits on the same spot of the thing every frame, so
    ///     the fingers' answer doesn't change, and replaying it costs no physics queries
    /// Rendered by the viewmodel camera, so they never clip into walls. Their layer collides with nothing else
    /// (held things' grasp colliders live on it), so a held gun never shoves the player.
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
        readonly bool[] _grasp = new bool[2];
        // Per hand: how far (m) the wrist is backed off the held thing along its palm, so finger roots aren't inside it.
        readonly float[] _palmPush = new float[2];
        const float MaxPalmPush = 0.03f;
        /// <summary>Per hand, after the last grasp: how many fingers ended on the held thing's surface (of how many).</summary>
        public readonly (int touching, int fingers)[] GraspContacts = new (int, int)[2];
        /// <summary>Per hand: fingers that ended inside what it holds in the last grasp (should stay 0), and the most since reset.</summary>
        public readonly int[] GraspClipping = new int[2], GraspClippingMax = new int[2];
        /// <summary>Per hand: which fingers ended inside (names, for the log).</summary>
        public readonly string[] GraspClippingNames = { "", "" };

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
            // The viewmodel layer only meets itself: a held gun's grasp collider must not block the player or the world.
            for (var i = 0; i < 32; i++) Physics.IgnoreLayerCollision(gameObject.layer, i, i != gameObject.layer);
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

        /// <summary>Close a hand's fingers onto what it holds (colliders on this layer) instead of its pose's angles.</summary>
        /// <param name="key">What's held (e.g. its name): a grasp settled for it before is replayed at once.</param>
        /// <param name="preset">A grip baked ahead of time (weapons.json): replayed from the first frame, if it's for this rig.</param>
        public void SetGrasp(int side, bool on, string key = null, GraspData preset = null)
        {
            if (side >= 2) return;
            _grasp[side] = on;
            _palmPush[side] = 0f;
            _stable[side] = 0;
            _onTarget[side] = 0;
            _graspKey[side] = on && key != null ? $"{key}/{side}/{(_trigger[side] ? "trigger" : "")}" : null;
            if (_graspKey[side] != null && !Baked.ContainsKey(_graspKey[side]) && preset != null && UsePresets && side < _arms.Length)
            {
                var fromPreset = FromData(_arms[side], preset);
                if (fromPreset != null)
                {
                    Baked[_graspKey[side]] = fromPreset;
                    Debug.Log($"[Hands] {(side == 0 ? "left" : "right")} hand: using the baked grip of {key} from weapons.json");
                }
                else Debug.LogWarning($"[Hands] baked grip for {key} doesn't fit this rig (re-run tools/ln.py bake-grasps): solving it live");
            }
            _baked[side] = _graspKey[side] != null && Baked.TryGetValue(_graspKey[side], out var b) ? b : null;
            if (_baked[side] != null) _palmPush[side] = _baked[side].Push;
        }

        /// <summary>This hand's settled grip (null while it's still solving): for tools/ln.py bake-grasps.</summary>
        public GraspData BakedGrip(int side)
        {
            if (side >= 2 || _baked[side] == null || side >= _arms.Length) return null;
            var b = _baked[side];
            var data = new GraspData { push = b.Push, touching = b.Contacts.Item1, fingers = b.Contacts.Item2 };
            var list = new List<float>();
            foreach (var finger in b.Joints)
                foreach (var q in finger) { list.Add(R(q.x)); list.Add(R(q.y)); list.Add(R(q.z)); list.Add(R(q.w)); }
            data.joints = list.Count / 4;
            data.rotations = list.ToArray();
            data.push = R(data.push);
            return data;
        }

        static float R(float v) => Mathf.Round(v * 10000f) / 10000f;   // 4 decimals: under a tenth of a degree

        static int JointCount(Arm arm)
        {
            var n = 0;
            foreach (var f in arm.Fingers) n += f.Joints.Length;
            return n;
        }

        static BakedGrasp FromData(Arm arm, GraspData d)
        {
            if (!d.Valid(JointCount(arm))) return null;
            var baked = new BakedGrasp { Joints = new Quaternion[arm.Fingers.Length][], Push = d.push, Contacts = (d.touching, d.fingers) };
            var k = 0;
            for (var i = 0; i < arm.Fingers.Length; i++)
            {
                baked.Joints[i] = new Quaternion[arm.Fingers[i].Joints.Length];
                for (var j = 0; j < baked.Joints[i].Length; j++, k += 4)
                    baked.Joints[i][j] = new Quaternion(d.rotations[k], d.rotations[k + 1], d.rotations[k + 2], d.rotations[k + 3]).normalized;
            }
            return baked;
        }

        /// <summary>Off: ignore grips baked ahead of time and solve every one live (the range's bake run).</summary>
        public static bool UsePresets = true;

        /// <summary>Forget every baked grasp (they're solved again on the next pickup).</summary>
        public static void ClearBakedGrasps() => Baked.Clear();

        sealed class BakedGrasp
        {
            public Quaternion[][] Joints;   // per finger, per joint: local rotation
            public float Push;
            public (int, int) Contacts;
        }
        static readonly Dictionary<string, BakedGrasp> Baked = new();
        readonly string[] _graspKey = new string[2];
        readonly BakedGrasp[] _baked = new BakedGrasp[2];
        readonly int[] _stable = new int[2], _onTarget = new int[2];
        readonly float[] _lastPush = new float[2];
        const int SettleFrames = 15;
        readonly List<Quaternion>[] _previous = { new(), new() };   // per hand: last frame's finger rotations

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

            // Physics keeps its own copy of where colliders are, refreshed on its step, not when a transform moves. The
            // held gun was just posed (recoil, sway, bob): without this the grasp below wraps where it was a physics
            // step ago, and the fingers clip the real one whenever it moves.
            if (_grasp[0] || _grasp[1]) Physics.SyncTransforms();
            _placed.Clear();   // finger bones already settled this frame (both hands): later fingers keep clear of them
            for (var s = 0; s < _arms.Length && s < 2; s++)
            {
                var arm = _arms[s];
                if (_weight[s] > 0f)
                {
                    // Grasping: backed off along the palm by however much keeps the finger roots out (see below).
                    var target = _target[s];
                    if (s < 2 && _grasp[s] && _palmFacing[s] != Vector3.zero) target -= _palmFacing[s].normalized * _palmPush[s];
                    LastError[s] = TwoBoneIK.Solve(arm, target, _weight[s], _rig.MaxReach, _rig.ShrugDegrees);
                    if (_palmFacing[s] != Vector3.zero) TwoBoneIK.OrientHand(arm, _palmFacing[s], _fingersAlong[s], _weight[s]);
                }
                else LastError[s] = 0f;

                _posers[s].Apply(dt, _rig.PoseSpeed);
                if (s < 2 && _grasp[s])
                {
                    // A gripping palm holds still: the idle clip ripples the palm bones the fingers hang from, which
                    // moved a stored grip's fingertips off the thing (and made the live answer drift frame to frame).
                    foreach (var f in arm.Fingers)
                        if (f.Palm != null && _bind.TryGetValue(f.Palm, out var rest)) f.Palm.localRotation = rest;
                }
                if (s < 2 && _grasp[s] && _baked[s] != null)
                {
                    // Replay: the settled answer, no physics. (Its bones still keep the other hand's fingers clear.)
                    var baked = _baked[s];
                    for (var i = 0; i < arm.Fingers.Length && i < baked.Joints.Length; i++)
                    {
                        var f = arm.Fingers[i];
                        for (var j = 0; j < f.Joints.Length && j < baked.Joints[i].Length; j++) f.Joints[j].localRotation = baked.Joints[i][j];
                        Place(f, Mathf.Min(f.Joints.Length, _angles.Length));
                    }
                    GraspContacts[s] = baked.Contacts;
                    // Spot check (a few queries) once the hand is really on its spot: fully weighted and on target for
                    // a moment. While it's still coming up with the thing (the pickup), the stored grip just plays.
                    _onTarget[s] = _weight[s] >= 0.999f && LastError[s] < 0.002f ? _onTarget[s] + 1 : 0;
                    if (_onTarget[s] >= 10 && Time.frameCount % 10 == 0)
                    {
                        var inside = 0;
                        var which = "";
                        foreach (var f in arm.Fingers)
                            if (f.CurlAxes != null && f.Joints.Length > 0 && Hits(f, 0, Mathf.Min(f.Joints.Length, _angles.Length), 1 << gameObject.layer, fingers: false, tolerance: SettledTolerance) is var hb && hb >= 0)
                            {
                                inside++;
                                which += $"{(which.Length > 0 ? ", " : "")}{f.Name} bone {hb}";
                            }
                        GraspClipping[s] = inside;
                        GraspClippingMax[s] = Mathf.Max(GraspClippingMax[s], inside);
                        if (inside > 0)
                        {
                            // The hand has shifted on the thing (near the shoulder the arm's solve wanders a little):
                            // the stored answer no longer fits. Solve live again from here; it re-bakes once settled.
                            _baked[s] = null;
                            _stable[s] = 0;
                            Debug.Log($"[Hands] {(s == 0 ? "left" : "right")} grasp of {_graspKey[s]?.Split('/')[0]} no longer fits ({which}; " +
                                      $"IK error {LastError[s] * 1000f:0.0} mm, palm off {_palmPush[s] * 100f:0.0} cm): solving again");
                        }
                    }
                    continue;
                }
                if (s < 2 && _grasp[s])
                {
                    GraspContacts[s] = Grasp(arm, _trigger[s]);
                    var clipping = 0;
                    var names = "";
                    foreach (var f in arm.Fingers)
                        if (f.CurlAxes != null && f.Joints.Length > 0 && Hits(f, 0, Mathf.Min(f.Joints.Length, _angles.Length), 1 << gameObject.layer, fingers: false, tolerance: SettledTolerance) is var k && k >= 0)
                        {
                            clipping++;
                            names += $"{(names.Length > 0 ? ", " : "")}{f.Name} (bone {k})";
                        }
                    GraspClippingNames[s] = names;
                    // A finger's root inside means the palm itself sits in the thing: back the hand off a little each
                    // frame until clear. Next frame's IK uses it.
                    // Only ever grows while held (reset on pickup): easing back in made the roots dip in and out, a flicker.
                    if (clipping > 0) _palmPush[s] = Mathf.Min(MaxPalmPush, _palmPush[s] + 0.002f);
                    Settle(s, arm, clipping);
                    GraspClipping[s] = clipping;
                    GraspClippingMax[s] = Mathf.Max(GraspClippingMax[s], clipping);
                    continue;
                }   // fingers on the surface: no fidget
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

        // Grasp: joints curl together in these ratios (knuckle, middle, tip), each up to its limit (FingerLimits).
        static readonly float[] CurlRatio = { 1f, 1.15f, 0.85f };
        const float CurlStep = 8f, FingerRadius = 0.008f;
        const int RefineSteps = 6;   // halvings between the last clear step and the one that touched
        readonly float[] _angles = new float[4], _trial = new float[4];
        readonly bool[] _trigger = new bool[2];

        /// <summary>A gun hand: its index finger rests on the trigger (curls less) instead of wrapping the grip.</summary>
        public void SetTriggerFinger(int side, bool on)
        {
            if (side < 2) _trigger[side] = on;
        }

        /// <summary>
        /// Each finger from straight (its bind pose): all its joints curl together until a bone would go into what the
        /// hand holds; the joints up to that bone stop there and the rest keep curling (a finger wraps from the knuckle
        /// out). Returns how many fingers ended touching.
        /// </summary>
        (int, int) Grasp(Arm arm, bool trigger)
        {
            var mask = 1 << gameObject.layer;
            var touching = 0;
            var fingers = 0;
            foreach (var f in arm.Fingers)
            {
                if (f.CurlAxes == null || f.Joints.Length == 0) continue;
                fingers++;
                var n = Mathf.Min(f.Joints.Length, _angles.Length);
                // The trigger finger stops about half way (resting on the trigger, off the grip).
                var cap = trigger && f.Name.Contains("index") ? 0.5f : 1f;
                for (var j = 0; j < n; j++) { _angles[j] = 0f; f.Joints[j].localRotation = f.BindJoints[j]; }
                var first = 0;   // joints before this one are locked
                if (Hits(f, 0, n, mask) >= 0)
                {
                    // In it even straight (its root against the thing; a thumb behind a pistol grip): find the clear
                    // turn of its root nearest straight (curled on, splayed), then curl the rest from there. Never bent
                    // backward: joints barely go past straight, and a thumb bent back looks broken. No clear turn: it
                    // stays straight, and the hand backs off its palm until the root is out (below).
                    var best = float.MaxValue;
                    var bestRot = f.BindJoints[0];
                    var root = FingerLimits.For(f.Name, 0);
                    for (var curl = root.CurlMin; curl <= root.CurlMax; curl += 5f)   // (the thumb's root may go 10 back)
                        for (var spread = -root.Spread; spread <= root.Spread + 0.01f; spread += Mathf.Max(5f, root.Spread / 5f))
                        {
                            var cost = Mathf.Abs(curl) + Mathf.Abs(spread) * 1.5f;
                            if (cost >= best) continue;
                            var rot = f.BindJoints[0] * Quaternion.AngleAxis(spread * f.SpreadSign, f.SpreadAxis) * Quaternion.AngleAxis(curl, f.CurlAxes[0]);
                            f.Joints[0].localRotation = rot;
                            if (Hits(f, 0, n, mask) < 0) { best = cost; bestRot = rot; }
                        }
                    f.Joints[0].localRotation = bestRot;
                    if (best == float.MaxValue) { Place(f, n); touching++; continue; }   // no way out: leave it straight
                    first = 1;
                }
                var touched = false;
                for (var guard = 0; guard < 40 && first < n; guard++)
                {
                    var moved = false;
                    for (var j = first; j < n; j++)
                    {
                        var limit = FingerLimits.For(f.Name, j).CurlMax * cap;
                        _trial[j] = Mathf.Min(limit, _angles[j] + CurlStep * CurlRatio[Mathf.Min(j, CurlRatio.Length - 1)]);
                        moved |= _trial[j] > _angles[j];
                    }
                    if (!moved) break;   // curled all the way without meeting anything
                    Curl(f, first, n, 1f);
                    var hit = Hits(f, first, n, mask);
                    if (hit < 0) { for (var j = first; j < n; j++) _angles[j] = _trial[j]; continue; }
                    // Touched within this step: find the contact between the last clear angles and these (binary search),
                    // settle there; the joints up to the bone that touched stay, the rest go on curling.
                    float lo = 0f, hi = 1f;
                    for (var r = 0; r < RefineSteps; r++)
                    {
                        var mid = (lo + hi) * 0.5f;
                        Curl(f, first, n, mid);
                        var h = Hits(f, first, n, mask);
                        if (h < 0) lo = mid; else { hi = mid; hit = h; }
                    }
                    Curl(f, first, n, lo);
                    for (var j = first; j < n; j++) _angles[j] = Mathf.Lerp(_angles[j], _trial[j], lo);
                    touched = true;
                    first = hit + 1;
                }
                if (touched) touching++;
                Constrain(f, n, mask);
                Place(f, n);
            }
            return (touching, fingers);
        }

        /// <summary>
        /// Bake the live grasp once it has settled: the hand on its target, nothing inside, the palm no longer backing
        /// off and the fingers' angles steady (under a degree a frame) for SettleFrames frames.
        /// </summary>
        void Settle(int s, Arm arm, int clipping)
        {
            if (_graspKey[s] == null) return;
            var steady = clipping == 0 && LastError[s] < 0.002f && Mathf.Approximately(_palmPush[s], _lastPush[s]);
            _lastPush[s] = _palmPush[s];
            var k = 0;
            foreach (var f in arm.Fingers)
                foreach (var joint in f.Joints)
                {
                    var previous = _previous[s];
                    if (k < previous.Count && Quaternion.Angle(previous[k], joint.localRotation) > 1f) steady = false;
                    if (k < previous.Count) previous[k] = joint.localRotation; else previous.Add(joint.localRotation);
                    k++;
                }
            _stable[s] = steady ? _stable[s] + 1 : 0;
            if (_stable[s] < SettleFrames) return;
            var baked = new BakedGrasp { Joints = new Quaternion[arm.Fingers.Length][], Push = _palmPush[s], Contacts = GraspContacts[s] };
            for (var i = 0; i < arm.Fingers.Length; i++)
            {
                var f = arm.Fingers[i];
                baked.Joints[i] = new Quaternion[f.Joints.Length];
                for (var j = 0; j < f.Joints.Length; j++) baked.Joints[i][j] = f.Joints[j].localRotation;
            }
            Baked[_graspKey[s]] = baked;
            _baked[s] = baked;
            Debug.Log($"[Hands] baked the {(s == 0 ? "left" : "right")} hand's grasp of {_graspKey[s].Split('/')[0]} " +
                      $"(fingers on it {baked.Contacts.Item1}/{baked.Contacts.Item2}, palm backed off {baked.Push * 100f:0.0} cm)");
        }

        readonly Quaternion[] _unclamped = new Quaternion[4];

        /// <summary>
        /// A safety net: each of a finger's joints clamped into its natural range (FingerLimits, ported from OpenIK),
        /// kept only if that doesn't put the finger into what it holds. The grasp's own angles are already in range
        /// (it only tries legal curls and splays); clamping them anyway nudges them by its rounding, which pushed
        /// thumbs back in, so a clamp that touches is undone.
        /// </summary>
        void Constrain(Finger f, int n, int mask)
        {
            for (var j = 0; j < n; j++)
            {
                var joint = f.Joints[j];
                _unclamped[j] = joint.localRotation;
                var deviation = Quaternion.Inverse(f.BindJoints[j]) * joint.localRotation;
                var bone = j + 1 < f.Joints.Length ? f.Joints[j + 1].localPosition : joint.localPosition;
                var spreadAxis = j == 0 ? f.SpreadAxis : Vector3.zero;
                joint.localRotation = f.BindJoints[j] * FingerLimits.Clamp(deviation, FingerLimits.For(f.Name, j), f.CurlAxes[j], spreadAxis, f.SpreadSign, bone);
            }
            if (Hits(f, 0, n, mask) >= 0)
                for (var j = 0; j < n; j++) f.Joints[j].localRotation = _unclamped[j];
        }

        /// <summary>A finger's joints from `first` posed `u` of the way from the settled angles to the trial ones.</summary>
        void Curl(Finger f, int first, int n, float u)
        {
            for (var j = first; j < n; j++)
                f.Joints[j].localRotation = f.BindJoints[j] * Quaternion.AngleAxis(Mathf.Lerp(_angles[j], _trial[j], u), f.CurlAxes[j]);
        }

        /// <summary>The first of a finger's bones (from `from`) that is in something on this layer, or -1.</summary>
        /// <param name="tolerance">Metres a bone may sink in and still count as clear: checks of a settled grip use one,
        /// since its fingers rest exactly on the surface and the slightest rounding tips them in.</param>
        int Hits(Finger f, int from, int n, int mask, bool fingers = true, float tolerance = 0f)
        {
            for (var k = from; k < n; k++)
            {
                if (!Bone(f, k, out var a, out var b)) continue;
                if (Physics.CheckCapsule(a, b, FingerRadius - tolerance, mask, QueryTriggerInteraction.Ignore)) return k;
                if (k == 0 || !fingers) continue;   // roots: neighbouring knuckles sit this close anyway
                foreach (var (c, d) in _placed)
                    if (SegmentDistance(a, b, c, d) < FingerGap) return k;
            }
            return -1;
        }

        readonly List<(Vector3, Vector3)> _placed = new();
        const float FingerGap = 0.013f;
        const float SettledTolerance = 0.0015f;   // 1.5 mm: resting on the surface, not in it   // between two fingers' bone centrelines: about a finger's thickness

        /// <summary>A finger's bone k as a segment (the last runs on past its joint to the fingertip).</summary>
        static bool Bone(Finger f, int k, out Vector3 a, out Vector3 b)
        {
            a = f.Joints[k].position;
            if (k + 1 < f.Joints.Length) b = f.Joints[k + 1].position;
            else if (k > 0) b = a + (a - f.Joints[k - 1].position) * 0.8f;
            else { b = a; return false; }
            return true;
        }

        /// <summary>A settled finger's bones (past the root) become obstacles for the fingers after it.</summary>
        void Place(Finger f, int n)
        {
            for (var k = 1; k < n; k++)
                if (Bone(f, k, out var a, out var b)) _placed.Add((a, b));
        }

        /// <summary>Closest distance between segments ab and cd.</summary>
        static float SegmentDistance(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 u = b - a, v = d - c, w = a - c;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
            var den = uu * vv - uv * uv;
            var sN = den < 1e-9f ? 0f : Mathf.Clamp01((uv * vw - vv * uw) / den);
            var tN = vv < 1e-9f ? 0f : Mathf.Clamp01((uv * sN + vw) / vv);
            sN = uu < 1e-9f ? 0f : Mathf.Clamp01((uv * tN - uw) / uu);
            return Vector3.Distance(a + u * sN, c + v * tN);
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
