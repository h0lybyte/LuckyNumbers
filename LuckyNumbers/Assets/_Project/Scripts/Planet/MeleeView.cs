using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A melee weapon you can pick up (its Rigidbody makes it carryable). Points are in this transform's space: the
    /// blade or head runs from `grip` (middle of the handle) toward `tip` (+Y as exported); `edge` is the side that
    /// cuts or strikes. The scene builder measures them from the model; tune them here.
    /// FirstPersonPresenter holds it in the right fist (IK on the handle) and swings it on click: a slash from the
    /// right, across to the left. The blade sweeps a capsule through the world while it strikes, pushing and logging
    /// what it hits (once per thing per swing).
    /// </summary>
    public sealed class MeleeView : MonoBehaviour
    {
        [Header("Shape (local)")]
        [SerializeField] Vector3 grip;               // where the fist closes on the handle
        [SerializeField] Vector3 tip;                // end of the blade / middle of the head
        [SerializeField] Vector3 edge = Vector3.right;   // the cutting / striking side (a direction)

        [Header("Hold and swing (camera space)")]
        [SerializeField] Vector3 holdOffset = new(0.2f, -0.21f, 0.36f);   // the fist, at rest
        [SerializeField] float swingSeconds = 0.42f;
        [SerializeField] float radius = 0.05f;        // of the swept blade
        [SerializeField] float impulse = 2.5f;        // push on what it hits (N s)

        public Vector3 Grip => grip;
        [Header("Baked grips (weapons.json, tools/ln.py bake-grasps)")]
        [SerializeField] LuckyNumbers.Hands.GraspData graspLeft;
        [SerializeField] LuckyNumbers.Hands.GraspData graspRight;
        public LuckyNumbers.Hands.GraspData BakedGrip(int side) => side == 0 ? graspLeft : graspRight;
        public void InitGrips(LuckyNumbers.Hands.GraspData left, LuckyNumbers.Hands.GraspData right) { graspLeft = left; graspRight = right; }

        public bool Swinging => _t >= 0f;

        float _t = -1f;   // swing progress 0..1 (-1: not swinging)
        Transform _ignore;
        readonly HashSet<Collider> _hit = new();
        readonly Collider[] _overlap = new Collider[16];
        Vector3 _lastTip;

        /// <summary>Editor setup (SceneBuilder): measured shape and how it swings.</summary>
        public void Init(Vector3 grip, Vector3 tip, Vector3 edge, float swingSeconds, float radius, float impulse)
        {
            this.grip = grip;
            this.tip = tip;
            this.edge = edge;
            this.swingSeconds = swingSeconds;
            this.radius = radius;
            this.impulse = impulse;
        }

        /// <summary>Held by someone (whose colliders the blade ignores), or dropped (null).</summary>
        public void Take(Transform holder)
        {
            _ignore = holder;
            _t = -1f;
        }

        public bool TrySwing()
        {
            if (_t >= 0f) return false;
            _t = 0f;
            _hit.Clear();
            _lastTip = transform.TransformPoint(tip);
            Debug.Log($"[Melee] {name} swing");
            return true;
        }

        // Swing keys (camera space): where the fist is, which way the blade points, which way the edge faces.
        // Rest: blade up and forward, edge facing left (the way it will cut). Wind up out to the right, cut across
        // to the low left, recover.
        static readonly (float t, Vector3 fist, Vector3 blade, Vector3 edge)[] Keys =
        {
            (0.00f, new Vector3(0f, 0f, 0f),        new Vector3(0.05f, 0.85f, 0.5f), new Vector3(-1f, 0f, 0.15f)),
            (0.28f, new Vector3(0.12f, 0.09f, -0.08f), new Vector3(0.75f, 0.55f, 0.3f), new Vector3(-0.4f, 0f, 0.9f)),
            (0.55f, new Vector3(-0.3f, -0.05f, 0.1f), new Vector3(-0.8f, 0.05f, 0.6f), new Vector3(-0.55f, -0.2f, -0.8f)),
            (1.00f, new Vector3(0f, 0f, 0f),        new Vector3(0.05f, 0.85f, 0.5f), new Vector3(-1f, 0f, 0.15f)),
        };
        const float StrikeFrom = 0.28f, StrikeTo = 0.6f;

        /// <summary>Where the fist goes and how the weapon turns (camera space), now: at rest or mid-swing.</summary>
        public void ViewPose(out Vector3 fist, out Quaternion rotation)
        {
            var t = _t < 0f ? 0f : _t;
            var i = 0;
            while (i < Keys.Length - 2 && t > Keys[i + 1].t) i++;
            var (t0, f0, b0, e0) = Keys[i];
            var (t1, f1, b1, e1) = Keys[i + 1];
            var u = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(t0, t1, t));
            fist = holdOffset + Vector3.Lerp(f0, f1, u);
            var blade = Vector3.Slerp(b0.normalized, b1.normalized, u);
            var facing = Vector3.ProjectOnPlane(Vector3.Slerp(e0.normalized, e1.normalized, u), blade);
            var along = (tip - grip).normalized;
            var localEdge = Vector3.ProjectOnPlane(edge, along);
            rotation = Quaternion.LookRotation(facing, blade) * Quaternion.Inverse(Quaternion.LookRotation(localEdge, along));
        }

        /// <summary>The fist round the handle (world): the handle runs through it thumb-side toward the tip, knuckles toward the edge.</summary>
        public void FistOn(out Vector3 wrist, out Vector3 palmFacing, out Vector3 fingersAlong)
        {
            var up = transform.TransformDirection((tip - grip).normalized);
            fingersAlong = Vector3.ProjectOnPlane(transform.TransformDirection(edge), up).normalized;
            palmFacing = -Vector3.Cross(up, fingersAlong).normalized;   // right hand: palm on the handle's left
            // The handle sits under the curled fingers: a little past the palm's middle and in from its surface.
            wrist = transform.TransformPoint(grip) - fingersAlong * 0.06f - palmFacing * 0.035f;
        }

        void LateUpdate()   // after the holder posed it this frame
        {
            if (_t < 0f) return;
            _t += Time.deltaTime / Mathf.Max(0.1f, swingSeconds);
            var tipNow = transform.TransformPoint(tip);
            if (_t >= StrikeFrom && _t <= StrikeTo)
            {
                var from = transform.TransformPoint(Vector3.Lerp(grip, tip, 0.35f));
                var n = Physics.OverlapCapsuleNonAlloc(from, tipNow, radius, _overlap, ~0, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < n; i++)
                {
                    var c = _overlap[i];
                    if (c.transform.IsChildOf(transform) || (_ignore != null && c.transform.IsChildOf(_ignore)) || !_hit.Add(c)) continue;
                    var sweep = tipNow - _lastTip;
                    if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic && sweep.sqrMagnitude > 1e-8f)
                        c.attachedRigidbody.AddForceAtPosition(sweep.normalized * impulse, c.ClosestPoint(tipNow), ForceMode.Impulse);
                    Debug.Log($"[Melee] {name} hit {c.name}");
                }
            }
            _lastTip = tipNow;
            if (_t >= 1f) _t = -1f;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green; Gizmos.DrawWireSphere(transform.TransformPoint(grip), 0.02f);
            Gizmos.color = Color.cyan; Gizmos.DrawLine(transform.TransformPoint(grip), transform.TransformPoint(tip));
            Gizmos.color = Color.red;
            var mid = transform.TransformPoint(Vector3.Lerp(grip, tip, 0.7f));
            Gizmos.DrawLine(mid, mid + transform.TransformDirection(edge.normalized) * 0.08f);
        }
    }
}
