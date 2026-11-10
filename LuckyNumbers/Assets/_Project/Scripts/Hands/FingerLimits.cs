// Joint-limit math ported from OpenIK (https://github.com/WouterApts/openik-unity, MIT License,
// Copyright (c) 2026 Wouter Apts; full text in OpenIK-LICENSE.md beside this file): HingeIKJoint's hinge angle
// extraction and clamp, BallSocketIKJoint's swing/twist split and clamps (elliptical swing cone, twist range).
// Adapted: static helpers on plain rotations (no components or solver), and finger limits for our hand rig.

using UnityEngine;

namespace LuckyNumbers.Hands
{
    /// <summary>
    /// Keeps finger joints in a hand's natural range: middle and end joints only bend one way (a hinge), knuckle roots
    /// curl and splay but don't spin (a ball and socket with almost no twist), the thumb's root moves most. Rotations
    /// are deviations from the joint's straightened bind pose, in its local space.
    /// </summary>
    public static class FingerLimits
    {
        /// <summary>Degrees, from the straightened bind pose: curl toward the palm, splay either way, twist about the bone.</summary>
        public readonly struct Limit
        {
            public readonly float CurlMin, CurlMax, Spread, Twist;
            public Limit(float curlMin, float curlMax, float spread, float twist)
            {
                CurlMin = curlMin; CurlMax = curlMax; Spread = spread; Twist = twist;
            }
        }

        // Knuckle roots: curl to 85, splay 20 either way, no spin; then two hinges. The thumb's root (its CMC) curls a
        // little back and well forward, splays and turns to oppose the fingers; its other joints are hinges.
        static readonly Limit[] Finger = { new(0f, 85f, 20f, 5f), new(0f, 100f, 0f, 0f), new(0f, 75f, 0f, 0f) };
        static readonly Limit[] Thumb = { new(-10f, 60f, 35f, 25f), new(0f, 70f, 0f, 0f), new(0f, 80f, 0f, 0f) };

        public static Limit For(string finger, int joint)
        {
            var table = finger == "thumb" ? Thumb : Finger;
            return table[Mathf.Clamp(joint, 0, table.Length - 1)];
        }

        /// <summary>
        /// Clamp a joint's deviation from its bind pose into its limit. `curlAxis` and `spreadAxis` (with `spreadSign`)
        /// are the joint's local axes (HandRigMap's); `boneAxis` points along the bone (twist is measured about it).
        /// </summary>
        public static Quaternion Clamp(Quaternion deviation, Limit limit, Vector3 curlAxis, Vector3 spreadAxis, float spreadSign, Vector3 boneAxis)
        {
            if (limit.Spread <= 0f && limit.Twist <= 0f)
                return ClampHinge(deviation, curlAxis.normalized, limit.CurlMin, limit.CurlMax);   // a hinge: curl only

            // Ball and socket: split off the twist about the bone and clamp it; clamp the swing's curl and splay
            // separately (a cone that's lopsided: far more curl than back-bend).
            var toBone = Quaternion.FromToRotation(Vector3.forward, boneAxis.normalized);
            var inFrame = Quaternion.Inverse(toBone) * deviation * toBone;   // the bone along +Z
            var twist = ClampTwist(ExtractTwistAroundForwardVector(inFrame), limit.Twist);
            var swing = toBone * ExtractSwingAroundForwardVector(inFrame) * Quaternion.Inverse(toBone);
            var curl = Mathf.Clamp(ExtractHingeAngle(swing, curlAxis.normalized), limit.CurlMin, limit.CurlMax);
            var spread = spreadAxis.sqrMagnitude > 1e-8f
                ? Mathf.Clamp(ExtractHingeAngle(swing, spreadAxis.normalized) * spreadSign, -limit.Spread, limit.Spread)
                : 0f;
            var clampedSwing = Quaternion.AngleAxis(spread * spreadSign, spreadAxis.normalized) * Quaternion.AngleAxis(curl, curlAxis.normalized);
            return clampedSwing * (toBone * twist * Quaternion.Inverse(toBone));
        }

        // ---- ported from OpenIK HingeIKJoint ----

        /// <summary>Keeps only the rotation around the hinge axis and clamps its angle to the allowed range.</summary>
        public static Quaternion ClampHinge(Quaternion deviation, Vector3 localHingeAxis, float minAngle, float maxAngle)
        {
            var angleDeg = ExtractHingeAngle(deviation, localHingeAxis);
            return Quaternion.AngleAxis(Mathf.Clamp(angleDeg, minAngle, maxAngle), localHingeAxis);
        }

        /// <summary>Measures the signed angle around the hinge axis (-180..180), ignoring rotation around other axes.</summary>
        public static float ExtractHingeAngle(Quaternion deviation, Vector3 localHingeAxis)
        {
            var dot = deviation.x * localHingeAxis.x + deviation.y * localHingeAxis.y + deviation.z * localHingeAxis.z;
            var hingeOnly = new Quaternion(localHingeAxis.x * dot, localHingeAxis.y * dot, localHingeAxis.z * dot, deviation.w);
            hingeOnly.Normalize();
            if (hingeOnly.w < 0f)   // the same angle for both forms of the same rotation
            {
                hingeOnly.x = -hingeOnly.x; hingeOnly.y = -hingeOnly.y; hingeOnly.z = -hingeOnly.z; hingeOnly.w = -hingeOnly.w;
            }
            var sinHalf = Mathf.Sqrt(hingeOnly.x * hingeOnly.x + hingeOnly.y * hingeOnly.y + hingeOnly.z * hingeOnly.z);
            var sign = hingeOnly.x * localHingeAxis.x + hingeOnly.y * localHingeAxis.y + hingeOnly.z * localHingeAxis.z >= 0f ? 1f : -1f;
            return 2f * Mathf.Atan2(sinHalf * sign, hingeOnly.w) * Mathf.Rad2Deg;
        }

        // ---- ported from OpenIK BallSocketIKJoint ----

        /// <summary>Clamps a twist around the forward axis to the allowed range (degrees either way).</summary>
        public static Quaternion ClampTwist(Quaternion twist, float twistHalfAngle)
        {
            var signedDeg = Mathf.DeltaAngle(0f, 2f * Mathf.Atan2(twist.z, twist.w) * Mathf.Rad2Deg);
            var clamped = Mathf.Clamp(signedDeg, -twistHalfAngle, twistHalfAngle);
            return Mathf.Approximately(clamped, signedDeg) ? twist : Quaternion.AngleAxis(clamped, Vector3.forward);
        }

        /// <summary>Clamps a swing (z = 0) to an elliptical cone set by the sines of half the pitch and yaw limits.</summary>
        public static Quaternion ClampSwing(Quaternion swing, float pitchHalfSin, float yawHalfSin)
        {
            const float epsilon = 1e-4f;
            float a = yawHalfSin, b = pitchHalfSin;
            if (a < epsilon && b < epsilon) return Quaternion.identity;
            if (a < epsilon)
            {
                var cy = Mathf.Clamp(swing.y, -b, b);
                var s2 = 1f - cy * cy;
                return new Quaternion(0f, cy, 0f, s2 > 0f ? Mathf.Sqrt(s2) : 0f);
            }
            if (b < epsilon)
            {
                var cx = Mathf.Clamp(swing.x, -a, a);
                var s2 = 1f - cx * cx;
                return new Quaternion(cx, 0f, 0f, s2 > 0f ? Mathf.Sqrt(s2) : 0f);
            }
            float ex = swing.x / a, ey = swing.y / b;
            if (ex * ex + ey * ey <= 1f) return swing;
            var closest = ClosestPointOnEllipse(swing.x, swing.y, a, b);
            var sNew2 = 1f - closest.x * closest.x - closest.y * closest.y;
            return new Quaternion(closest.x, closest.y, 0f, sNew2 > 0f ? Mathf.Sqrt(sNew2) : 0f);
        }

        /// <summary>The swing part of a rotation: the part that moves the forward (Z) axis (z = 0).</summary>
        public static Quaternion ExtractSwingAroundForwardVector(Quaternion q)
        {
            float w = q.w, z = q.z;
            var s = Mathf.Sqrt(w * w + z * z);
            if (s < 1e-6f) return q;
            var invS = 1f / s;
            return new Quaternion((w * q.x - q.y * z) * invS, (w * q.y + q.x * z) * invS, 0f, s);
        }

        /// <summary>The twist part of a rotation: the rotation around the forward (Z) axis.</summary>
        public static Quaternion ExtractTwistAroundForwardVector(Quaternion q)
        {
            float w = q.w, z = q.z;
            var s = Mathf.Sqrt(w * w + z * z);
            if (s < 1e-6f) return Quaternion.identity;
            var invS = 1f / s;
            return new Quaternion(0f, 0f, z * invS, w * invS);
        }

        // Closest point to (px, py) on an axis-aligned ellipse with half-axes a (X) and b (Y), by Newton's method.
        static Vector2 ClosestPointOnEllipse(float px, float py, float a, float b)
        {
            var t = Mathf.Atan2(a * py, b * px);
            var a2MinusB2 = a * a - b * b;
            for (var i = 0; i < 8; i++)
            {
                float sinT = Mathf.Sin(t), cosT = Mathf.Cos(t);
                var f = a2MinusB2 * sinT * cosT - px * a * sinT + py * b * cosT;
                var fPrime = a2MinusB2 * (cosT * cosT - sinT * sinT) - px * a * cosT - py * b * sinT;
                if (Mathf.Abs(fPrime) < 1e-10f) break;
                var dt = f / fPrime;
                t -= dt;
                if (Mathf.Abs(dt) < 1e-6f) break;
            }
            return new Vector2(a * Mathf.Cos(t), b * Mathf.Sin(t));
        }
    }
}
