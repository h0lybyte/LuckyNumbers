using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Getting round the station in open space (the rogues' pathfinding). Space is empty but for the station's hull
    /// (axis-aligned blocks), so two things are enough:
    ///
    /// - Waypoint: if the straight line to a goal is clear, fly at it; if the hull is in the way, the best detour
    ///   point on a box round the whole station (its 8 corners and 6 faces, pushed out by the clearance) that the
    ///   ship can see from where it is, scored by the length of the two legs. Called every frame, so as the ship
    ///   rounds the hull the detour moves on and the goal comes back into view.
    /// - Avoid: a steering push away from the hull just ahead (a ray down the velocity, `lookahead` seconds long),
    ///   sliding along the face it would hit rather than straight back, stronger the closer it is.
    ///
    /// Blocked also answers line of sight (a rogue doesn't fire through the station).
    /// </summary>
    public sealed class SpaceNav
    {
        readonly List<Bounds> _boxes = new();
        readonly Vector3[] _candidates = new Vector3[14];
        Bounds _all;

        public SpaceNav(IReadOnlyList<Bounds> boxes)
        {
            if (boxes == null) return;
            for (var i = 0; i < boxes.Count; i++)
            {
                _boxes.Add(boxes[i]);
                if (i == 0) _all = boxes[i];
                else _all.Encapsulate(boxes[i]);
            }
        }

        public bool Empty => _boxes.Count == 0;

        /// <summary>Does the segment a→b pass through any block (each grown by `margin`)?</summary>
        public bool Blocked(Vector3 a, Vector3 b, float margin)
        {
            var d = b - a;
            foreach (var box in _boxes)
            {
                var e = box.extents + Vector3.one * margin;
                if (Slab(a, d, box.center - e, box.center + e, out var t, out _) && t <= 1f) return true;
            }
            return false;
        }

        /// <summary>Where to steer for `goal` from `from`: the goal itself if the way is clear, else a detour point.</summary>
        public Vector3 Waypoint(Vector3 from, Vector3 goal, float margin)
        {
            if (Empty || !Blocked(from, goal, margin * 0.5f)) return goal;
            var c = _all.center;
            var e = _all.extents + Vector3.one * (margin * 1.6f);
            var n = 0;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
                _candidates[n++] = c + new Vector3(x * e.x, y * e.y, z * e.z);
            _candidates[n++] = c + Vector3.right * e.x;
            _candidates[n++] = c + Vector3.left * e.x;
            _candidates[n++] = c + Vector3.up * e.y;
            _candidates[n++] = c + Vector3.down * e.y;
            _candidates[n++] = c + Vector3.forward * e.z;
            _candidates[n++] = c + Vector3.back * e.z;
            var best = goal;
            var bestCost = float.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var w = _candidates[i];
                if (Blocked(from, w, margin * 0.5f)) continue;
                var leg = Vector3.Distance(w, goal);
                var cost = Vector3.Distance(from, w) + leg + (Blocked(w, goal, margin * 0.5f) ? leg * 2f : 0f);
                if (cost < bestCost) { bestCost = cost; best = w; }
            }
            // Nothing visible (deep in a corner of the hull): straight out from the station's middle.
            if (bestCost == float.MaxValue) best = from + (from - c).normalized * (margin * 4f);
            return best;
        }

        /// <summary>
        /// A steering push (0 if nothing's ahead) keeping a ship of `radius` off the hull: the face it would hit within
        /// `lookahead` seconds at `velocity`, mostly along the face (keep going round), partly out from it.
        /// </summary>
        public Vector3 Avoid(Vector3 position, Vector3 velocity, float radius, float lookahead)
        {
            var speed = velocity.magnitude;
            if (Empty || speed < 0.1f) return Vector3.zero;
            var dir = velocity / speed;
            var reach = speed * math.max(0.2f, lookahead) + radius;
            var push = Vector3.zero;
            var strongest = 0f;
            foreach (var box in _boxes)
            {
                var e = box.extents + Vector3.one * radius;
                var lo = box.center - e;
                var hi = box.center + e;
                Vector3 normal;
                float strength;
                if (Inside(position, lo, hi))
                {
                    normal = Outward(position, box.center, e);
                    strength = 1.5f;
                }
                else if (Slab(position, dir * reach, lo, hi, out var t, out normal) && t <= 1f) strength = 1f - t;
                else continue;
                if (strength <= strongest) continue;
                strongest = strength;
                var along = Vector3.ProjectOnPlane(dir, normal);
                if (along.sqrMagnitude < 1e-4f) along = Vector3.Cross(normal, Vector3.up);
                if (along.sqrMagnitude < 1e-4f) along = Vector3.Cross(normal, Vector3.right);
                push = (along.normalized + normal * 0.7f) * strength;
            }
            return push;
        }

        static bool Inside(Vector3 p, Vector3 lo, Vector3 hi) =>
            p.x > lo.x && p.x < hi.x && p.y > lo.y && p.y < hi.y && p.z > lo.z && p.z < hi.z;

        /// <summary>The face normal nearest `p` of a box (centre `c`, half size `e`).</summary>
        static Vector3 Outward(Vector3 p, Vector3 c, Vector3 e)
        {
            var d = p - c;
            var gap = new Vector3(e.x - math.abs(d.x), e.y - math.abs(d.y), e.z - math.abs(d.z));
            if (gap.x <= gap.y && gap.x <= gap.z) return new Vector3(math.sign(d.x), 0f, 0f);
            if (gap.y <= gap.z) return new Vector3(0f, math.sign(d.y), 0f);
            return new Vector3(0f, 0f, math.sign(d.z));
        }

        /// <summary>Ray a + t·d (t ≥ 0) against a box: where it enters (t, in units of d) and that face's normal.</summary>
        static bool Slab(Vector3 a, Vector3 d, Vector3 lo, Vector3 hi, out float tEnter, out Vector3 normal)
        {
            tEnter = 0f;
            var tExit = float.MaxValue;
            normal = Vector3.zero;
            for (var axis = 0; axis < 3; axis++)
            {
                var o = a[axis];
                var v = d[axis];
                if (math.abs(v) < 1e-6f)
                {
                    if (o < lo[axis] || o > hi[axis]) return false;
                    continue;
                }
                var t1 = (lo[axis] - o) / v;
                var t2 = (hi[axis] - o) / v;
                var sign = -1f;
                if (t1 > t2) { (t1, t2) = (t2, t1); sign = 1f; }
                if (t1 > tEnter)
                {
                    tEnter = t1;
                    normal = Vector3.zero;
                    normal[axis] = sign;
                }
                tExit = math.min(tExit, t2);
                if (tEnter > tExit) return false;
            }
            return true;
        }
    }
}
