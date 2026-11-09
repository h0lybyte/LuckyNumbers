using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Hands
{
    /// <summary>One arm of a first-person rig, resolved from a HandRig map (content/hands/*).</summary>
    public sealed class Arm
    {
        public string Side;
        public Transform Shoulder, Upper, Lower, Hand, Target, Pole;
        public Finger[] Fingers;
        public float UpperLength, LowerLength;
        public float Reach => UpperLength + LowerLength;
        /// <summary>Palm frame in the hand bone's space (measured from the bind pose): fingers point along
        /// FingerAxis, the palm faces PalmAxis.</summary>
        public Vector3 FingerAxis, PalmAxis;
    }

    public sealed class Finger
    {
        public string Name;
        public Transform Palm;
        public Transform[] Joints;
        public Quaternion[] BindJoints;   // the straightened finger (MeasureFingers): poses curl from here
        /// <summary>Per joint (its local space): rotating +degrees about CurlAxes[j] bends toward the palm;
        /// about SpreadAxis (knuckle) by +degrees * SpreadSign fans away from the middle finger.</summary>
        public Vector3[] CurlAxes;
        public Vector3 SpreadAxis;
        public float SpreadSign;
    }

    /// <summary>Resolves bone names (with "{side}") to transforms, measures lengths and the palm frame.</summary>
    public static class HandRigMap
    {
        public static Arm[] Resolve(HandRig rig, Transform root, out List<string> missing)
        {
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) byName.TryAdd(t.name, t);
            var miss = new List<string>();
            Transform Get(string pattern, string side)
            {
                if (string.IsNullOrEmpty(pattern)) return null;   // optional bone left out
                var name = pattern.Replace("{side}", side);
                if (byName.TryGetValue(name, out var t)) return t;
                miss.Add(name);
                return null;
            }

            var arms = new Arm[rig.Sides.Count];
            for (var s = 0; s < rig.Sides.Count; s++)
            {
                var side = rig.Sides[s];
                var a = new Arm
                {
                    Side = side,
                    Shoulder = Get(rig.Arm.Shoulder, side),
                    Upper = Get(rig.Arm.Upper, side),
                    Lower = Get(rig.Arm.Lower, side),
                    Hand = Get(rig.Arm.Hand, side),
                    Target = Get(rig.Arm.Target, side),
                    Pole = Get(rig.Arm.Pole, side),
                };
                var fingers = new List<Finger>();
                foreach (var f in rig.Fingers)
                {
                    var joints = new Transform[f.Joints.Count];
                    for (var j = 0; j < joints.Length; j++) joints[j] = Get(f.Joints[j], side);
                    fingers.Add(new Finger
                    {
                        Name = f.Name,
                        Palm = Get(f.Palm, side),
                        Joints = joints,
                        BindJoints = System.Array.ConvertAll(joints, j => j != null ? j.localRotation : Quaternion.identity),
                    });
                }
                a.Fingers = fingers.ToArray();
                if (a.Upper != null && a.Lower != null && a.Hand != null)
                {
                    a.UpperLength = Vector3.Distance(a.Upper.position, a.Lower.position);
                    a.LowerLength = Vector3.Distance(a.Lower.position, a.Hand.position);
                }
                arms[s] = a;
            }
            // Palm side from anatomy, not the pose: with fingers along `along` and the index side `across`, a left
            // palm faces -(along x across) and a right palm +(along x across). Sides are listed left hand first.
            var normals = new Vector3[arms.Length];
            for (var s = 0; s < arms.Length; s++) normals[s] = PalmNormal(arms[s]) * (s == 0 ? -1f : 1f);
            for (var s = 0; s < arms.Length; s++)
            {
                if (normals[s] == Vector3.zero) continue;
                var a = arms[s];
                a.FingerAxis = a.Hand.InverseTransformDirection(FingerDirection(a));
                a.PalmAxis = a.Hand.InverseTransformDirection(normals[s]);
            }
            missing = miss;
            return arms;
        }

        /// <summary>
        /// Straighten each finger (every joint in line with the bone before it: the palm bone for the knuckle,
        /// unless the finger has none, like the thumb) and keep that as the reference pose curls start from; then
        /// the curl and spread axes per joint. Call with the fingers in their bind pose; returns the most a
        /// finger was bent in it (degrees).
        /// </summary>
        public static float MeasureFingers(Arm a)
        {
            if (a.Hand == null || a.PalmAxis == Vector3.zero) return 0f;
            var bent = 0f;
            foreach (var f in a.Fingers)
            {
                var bend = 0f;
                for (var j = f.Palm != null ? 0 : 1; j < f.Joints.Length; j++)
                {
                    var joint = f.Joints[j];
                    var next = j + 1 < f.Joints.Length ? f.Joints[j + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                    if (next == null) continue;
                    var before = joint.position - (j == 0 ? f.Palm.position : f.Joints[j - 1].position);
                    var rotate = Quaternion.FromToRotation(next.position - joint.position, before);
                    rotate.ToAngleAxis(out var angle, out _);
                    bend += angle;
                    joint.rotation = rotate * joint.rotation;
                }
                for (var j = 0; j < f.Joints.Length; j++) f.BindJoints[j] = f.Joints[j].localRotation;
                bent = Mathf.Max(bent, bend);
            }
            var palm = a.Hand.TransformDirection(a.PalmAxis);
            Transform middle = null;
            foreach (var f in a.Fingers) if (f.Name == "middle" && f.Joints.Length > 0) middle = f.Joints[0];
            foreach (var f in a.Fingers)
            {
                f.CurlAxes = new Vector3[f.Joints.Length];
                for (var j = 0; j < f.Joints.Length; j++)
                {
                    var joint = f.Joints[j];
                    if (joint == null) continue;
                    // Bone direction: toward the next joint (or the tip bone; else continue the previous bone).
                    var next = j + 1 < f.Joints.Length ? f.Joints[j + 1] : joint.childCount > 0 ? joint.GetChild(0) : null;
                    var along = next != null ? next.position - joint.position : joint.position - joint.parent.position;
                    // Rotating `along` about (along x palm) moves it toward the palm.
                    f.CurlAxes[j] = joint.InverseTransformDirection(Vector3.Cross(along, palm).normalized);
                    if (j > 0) continue;
                    f.SpreadAxis = joint.InverseTransformDirection(palm);
                    var away = middle != null ? Vector3.ProjectOnPlane(joint.position - middle.position, palm) : Vector3.zero;
                    f.SpreadSign = away.sqrMagnitude < 1e-8f ? 0f : Mathf.Sign(Vector3.Dot(Vector3.Cross(palm, along), away));
                }
            }
            return bent;
        }

        static Transform Knuckle(Arm a, string finger)
        {
            foreach (var f in a.Fingers) if (f.Name == finger) return f.Joints.Length > 0 ? f.Joints[0] : null;
            return null;
        }

        static Vector3 FingerDirection(Arm a) => (Knuckle(a, "middle").position - a.Hand.position).normalized;

        /// <summary>Palm plane normal from wrist, middle, index and pinky knuckles (sign not yet decided).</summary>
        static Vector3 PalmNormal(Arm a)
        {
            Transform middle = Knuckle(a, "middle"), index = Knuckle(a, "index"), pinky = Knuckle(a, "pinky");
            if (a.Hand == null || middle == null || index == null || pinky == null) return Vector3.zero;
            return Vector3.Cross(FingerDirection(a), (index.position - pinky.position).normalized).normalized;
        }
    }
}
