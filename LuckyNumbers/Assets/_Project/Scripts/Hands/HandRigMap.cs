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
        public Quaternion[] BindJoints;   // straight-ish bind pose, for flattening the hand on a surface
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
            // Palm frames need both hands (palms face roughly toward each other and down in the bind pose).
            for (var s = 0; s < arms.Length; s++) MeasurePalm(arms[s], arms[(s + 1) % arms.Length]);
            missing = miss;
            return arms;
        }

        static void MeasurePalm(Arm a, Arm other)
        {
            Transform F(string n) { foreach (var f in a.Fingers) if (f.Name == n) return f.Joints.Length > 0 ? f.Joints[0] : null; return null; }
            var middle = F("middle"); var index = F("index"); var pinky = F("pinky");
            if (a.Hand == null || middle == null || index == null || pinky == null) return;
            var along = (middle.position - a.Hand.position).normalized;
            var across = (index.position - pinky.position).normalized;
            var normal = Vector3.Cross(along, across).normalized;
            var inward = other.Hand != null ? (other.Hand.position - a.Hand.position).normalized : Vector3.zero;
            if (Vector3.Dot(normal, inward + Vector3.down) < 0f) normal = -normal;   // the palm side, not the back
            a.FingerAxis = a.Hand.InverseTransformDirection(along);
            a.PalmAxis = a.Hand.InverseTransformDirection(normal);
        }
    }
}
