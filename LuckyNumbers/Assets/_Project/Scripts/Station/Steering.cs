using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Steering behaviours (Craig Reynolds, "Steering Behaviors for Autonomous Characters", red3d.com/cwr/steer): each
    /// turns a goal into a desired velocity, which the flight model (RogueFlight) then flies within its own thrust and
    /// turn limits. So a scout, a raider and a gunship share the same pilot and fly differently only by their numbers.
    ///
    /// - Arrive: toward a goal, no faster than it can still brake to a stop: v = min(vmax, sqrt(2 a (d - r))), plus
    ///   the goal's own velocity, so a moving slot (beside you) is matched on arrival instead of overshot.
    /// - Lead: where a target will be after a short, capped look ahead (a sudden turn doesn't throw it far off).
    /// - Orbit: round a centre, tangential speed plus a radial correction back to the radius. A turn of radius R at
    ///   speed v needs v^2/R of sideways push, and a ship turning at w can give v·w, so it flies no faster than w·R.
    /// - Avoid: where two ships will be closest (relative position and velocity); if that's inside the safe gap, a
    ///   push apart, stronger the closer and the sooner.
    /// - Command: the acceleration that closes on a desired velocity over `tau` seconds, clamped to what it has.
    /// </summary>
    public static class Steering
    {
        /// <summary>a = clamp((v_desired - v) / tau, a_max).</summary>
        public static Vector3 Command(Vector3 desired, Vector3 velocity, float tau, float maxAccel) =>
            Vector3.ClampMagnitude((desired - velocity) / math.max(1e-3f, tau), math.max(0f, maxAccel));

        /// <summary>The fastest it can be going `distance` m out and still stop `stop` m short, braking at `brake`.</summary>
        public static float ArriveSpeed(float distance, float stop, float brake, float maxSpeed) =>
            math.min(maxSpeed, math.sqrt(2f * math.max(0f, brake) * math.max(0f, distance - stop)));

        /// <summary>Desired velocity to arrive at `goal` (moving at `goalVelocity`), settling within `stop` m.</summary>
        public static Vector3 Arrive(Vector3 position, Vector3 goal, Vector3 goalVelocity, float maxSpeed, float brake, float stop)
        {
            var to = goal - position;
            var d = to.magnitude;
            var v = d > 1e-4f ? to / d * ArriveSpeed(d, stop, brake, maxSpeed) : Vector3.zero;
            return v + goalVelocity;
        }

        /// <summary>Where a target will be when a ship flying at `speed` gets there, looking at most `maxLookahead` s on.</summary>
        public static Vector3 Lead(Vector3 position, float speed, Vector3 target, Vector3 targetVelocity, float maxLookahead)
        {
            var t = math.min(math.max(0f, maxLookahead), Vector3.Distance(position, target) / math.max(1f, speed));
            return target + targetVelocity * t;
        }

        /// <summary>The fastest a ship turning at `turnRate` degrees/s can fly a circle of `radius`: v^2/R = v·w, so v = w·R.</summary>
        public static float OrbitSpeed(float speed, float radius, float turnRate) =>
            math.min(speed, math.radians(math.max(1f, turnRate)) * math.max(0f, radius));

        /// <summary>
        /// Desired velocity to circle `center` (moving at `centerVelocity`) at `radius` in the plane normal to `axis`
        /// (one way or the other: `side` ±1). The radial and out-of-plane errors close at `gain` per second.
        /// </summary>
        public static Vector3 Orbit(Vector3 position, Vector3 center, Vector3 centerVelocity, Vector3 axis, float radius,
                                    float speed, float turnRate, float gain, int side)
        {
            axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
            var off = position - center;
            var flat = Vector3.ProjectOnPlane(off, axis);
            var dist = flat.magnitude;
            var outward = dist > 1e-3f ? flat / dist : Perpendicular(axis);
            var tangent = Vector3.Cross(axis, outward) * (side < 0 ? -1f : 1f);
            var v = OrbitSpeed(speed, radius, turnRate);
            var radial = math.clamp((radius - dist) * gain, -v, v);
            var height = math.clamp(-Vector3.Dot(off, axis) * gain, -v, v);
            return tangent * v + outward * radial + axis * height + centerVelocity;
        }

        /// <summary>When (0..horizon s) two ships are closest, and the gap between them then (mine minus theirs).</summary>
        public static float ClosestApproach(Vector3 offset, Vector3 relativeVelocity, float horizon, out Vector3 gap)
        {
            var vv = relativeVelocity.sqrMagnitude;
            var t = vv > 1e-6f ? math.clamp(-Vector3.Dot(offset, relativeVelocity) / vv, 0f, math.max(0f, horizon)) : 0f;
            gap = offset + relativeVelocity * t;
            return t;
        }

        /// <summary>
        /// A push away from another ship (0 if they won't come within `safe` m in the next `horizon` s): along the gap
        /// at closest approach, up to 1 for a dead-on hit right now. `offset` is me minus it, `relativeVelocity` mine
        /// minus its. Head on (no gap to push along), both sides pick opposite sideways pushes, so they part.
        /// </summary>
        public static Vector3 AvoidShip(Vector3 offset, Vector3 relativeVelocity, float safe, float horizon)
        {
            var t = ClosestApproach(offset, relativeVelocity, horizon, out var gap);
            var sep = gap.magnitude;
            if (sep >= safe) return Vector3.zero;
            Vector3 dir;
            if (sep > 1e-3f) dir = gap / sep;
            else
            {
                dir = Vector3.Cross(relativeVelocity, Vector3.up);
                if (dir.sqrMagnitude < 1e-6f) dir = Vector3.Cross(relativeVelocity, Vector3.right);
                if (dir.sqrMagnitude < 1e-6f) dir = offset.sqrMagnitude > 1e-6f ? offset : Vector3.up;
                dir.Normalize();
            }
            var urgency = (1f - sep / safe) * (1f - t / math.max(1e-3f, horizon));
            return dir * urgency;
        }

        static Vector3 Perpendicular(Vector3 axis)
        {
            var p = Vector3.Cross(axis, Vector3.forward);
            if (p.sqrMagnitude < 1e-6f) p = Vector3.Cross(axis, Vector3.right);
            return p.normalized;
        }
    }
}
