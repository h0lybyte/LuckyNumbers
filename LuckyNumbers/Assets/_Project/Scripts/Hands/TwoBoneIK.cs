using UnityEngine;

namespace LuckyNumbers.Hands
{
    /// <summary>
    /// Analytic two-bone IK (upper -> lower -> end) in world space: law of cosines for the elbow, then aim the
    /// chain at the target, then twist about the shoulder->target axis so the elbow bends toward the pole.
    /// Optional shoulder shrug for targets just out of reach. Blends with the animated pose by weight.
    /// Finger shapes are separate (HandPoses).
    /// </summary>
    public static class TwoBoneIK
    {
        /// <returns>Distance from the end bone to the target after solving (metres; 0 = exact).</returns>
        public static float Solve(Arm arm, Vector3 target, float weight, float maxReach, float shrugDegrees)
        {
            if (weight <= 0f || arm.Upper == null || arm.Lower == null || arm.Hand == null) return 0f;
            var upperLocal = arm.Upper.localRotation;
            var lowerLocal = arm.Lower.localRotation;
            var shoulderLocal = arm.Shoulder != null ? arm.Shoulder.localRotation : Quaternion.identity;

            // Shrug: lift the shoulder toward targets beyond the arm's reach.
            var reach = arm.Reach * maxReach;
            if (arm.Shoulder != null && shrugDegrees > 0f)
            {
                var over = Vector3.Distance(arm.Upper.position, target) - reach;
                if (over > 0f)
                {
                    var full = Quaternion.FromToRotation(arm.Upper.position - arm.Shoulder.position, target - arm.Shoulder.position);
                    full.ToAngleAxis(out var angle, out var axis);
                    var lift = Mathf.Min(angle, shrugDegrees * Mathf.Clamp01(over / (arm.Reach * 0.25f)));
                    arm.Shoulder.rotation = Quaternion.AngleAxis(lift, axis) * arm.Shoulder.rotation;
                }
            }

            Vector3 a = arm.Upper.position, b = arm.Lower.position, c = arm.Hand.position;
            var pole = arm.Pole != null ? arm.Pole.position : b + (b - (a + c) * 0.5f);
            float lab = (b - a).magnitude, lcb = (c - b).magnitude;
            var lat = Mathf.Clamp((target - a).magnitude, 1e-4f, (lab + lcb) * maxReach);

            float ac_ab_0 = Angle(c - a, b - a), ba_bc_0 = Angle(a - b, c - b), ac_at_0 = Angle(c - a, target - a);
            var ac_ab_1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
            var ba_bc_1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

            var axis0 = Vector3.Cross(c - a, b - a);
            if (axis0.sqrMagnitude < 1e-10f) axis0 = Vector3.Cross(c - a, pole - a);   // arm straight: bend toward the pole
            axis0.Normalize();
            var axis1 = Vector3.Cross(c - a, target - a);
            axis1 = axis1.sqrMagnitude < 1e-10f ? axis0 : axis1.normalized;

            var r0 = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, axis0);
            var r1 = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, axis0);
            var r2 = Quaternion.AngleAxis(ac_at_0 * Mathf.Rad2Deg, axis1);
            var upperWorld = arm.Upper.rotation;
            var lowerWorld = arm.Lower.rotation;
            arm.Upper.rotation = r2 * r0 * upperWorld;
            arm.Lower.rotation = r2 * r0 * r1 * lowerWorld;

            // Elbow toward the pole: twist about shoulder->target.
            var dir = (target - a).normalized;
            var elbow = Vector3.ProjectOnPlane(arm.Lower.position - a, dir);
            var toPole = Vector3.ProjectOnPlane(pole - a, dir);
            if (elbow.sqrMagnitude > 1e-10f && toPole.sqrMagnitude > 1e-10f)
                arm.Upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(elbow, toPole, dir), dir) * arm.Upper.rotation;

            if (weight < 1f)
            {
                arm.Upper.localRotation = Quaternion.Slerp(upperLocal, arm.Upper.localRotation, weight);
                arm.Lower.localRotation = Quaternion.Slerp(lowerLocal, arm.Lower.localRotation, weight);
                if (arm.Shoulder != null) arm.Shoulder.localRotation = Quaternion.Slerp(shoulderLocal, arm.Shoulder.localRotation, weight);
            }
            return Vector3.Distance(arm.Hand.position, target);
        }

        /// <summary>
        /// Turn the hand so its palm faces `palmFacing` with the fingers along `fingersAlong` (blended). The
        /// twist about the forearm (turning the palm over) is shared with the forearm bone, like a twist bone
        /// would: all of it at the wrist pinches the skin there like a sweet wrapper.
        /// </summary>
        public static void OrientHand(Arm arm, Vector3 palmFacing, Vector3 fingersAlong, float weight, float forearmShare = 0.5f)
        {
            if (weight <= 0f || arm.Hand == null || arm.FingerAxis == Vector3.zero) return;
            var current = Quaternion.LookRotation(arm.Hand.TransformDirection(arm.FingerAxis), arm.Hand.TransformDirection(arm.PalmAxis));
            var wanted = Quaternion.LookRotation(Vector3.ProjectOnPlane(fingersAlong, palmFacing).normalized, palmFacing);
            var target = Quaternion.Slerp(arm.Hand.rotation, wanted * Quaternion.Inverse(current) * arm.Hand.rotation, weight);
            if (arm.Lower != null && forearmShare > 0f)
            {
                // The change, in world space, split into swing and twist about the forearm axis (elbow -> wrist).
                var axis = (arm.Hand.position - arm.Lower.position).normalized;
                var twist = Twist(target * Quaternion.Inverse(arm.Hand.rotation), axis);
                arm.Lower.rotation = Quaternion.Slerp(Quaternion.identity, twist, forearmShare) * arm.Lower.rotation;   // wrist stays put
            }
            arm.Hand.rotation = target;
        }

        /// <summary>The twist part of a rotation about an axis (swing-twist decomposition).</summary>
        public static Quaternion Twist(Quaternion q, Vector3 axis)
        {
            var along = Vector3.Project(new Vector3(q.x, q.y, q.z), axis);
            var t = new Quaternion(along.x, along.y, along.z, q.w);
            var length = Mathf.Sqrt(t.x * t.x + t.y * t.y + t.z * t.z + t.w * t.w);
            return length < 1e-6f ? Quaternion.identity : new Quaternion(t.x / length, t.y / length, t.z / length, t.w / length);
        }

        static float Angle(Vector3 x, Vector3 y) => Mathf.Acos(Mathf.Clamp(Vector3.Dot(x.normalized, y.normalized), -1f, 1f));
    }
}
