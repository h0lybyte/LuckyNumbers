using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// How a rogue flies (its pilot, RogueBrain, asks for a velocity via Steering): like a fighter, not a turret. It
    /// has a main engine, so the heading is part of every manoeuvre: it turns toward the velocity it wants and
    /// throttles up as the nose comes round.
    ///
    /// - Drift: the nose aims a little past the wanted velocity, by the sideways part of the miss, so a slide through
    ///   a turn gets corrected rather than carried. (Only sideways: wanting to slow down is the throttle's job, not a
    ///   reason to turn round.)
    /// - Throttle: the speed it wants (Steering's, so arrival brakes in time), less while the nose is still off, never
    ///   below `MinShare` of its cruise speed (a fighter can't hover) or above its top speed.
    ///
    /// - Bank and pull: for a big turn it rolls until the goal is "overhead", then pulls the nose up into it (the way
    ///   fighters turn hard); a little rudder helps. Small corrections just nudge the nose and roll the wings level.
    /// - Rates ease in and out: pitch, yaw and roll each chase their command at a limited angular acceleration, so
    ///   turns start and stop smoothly instead of snapping.
    /// - Energy: a hard turn bleeds speed, and the speed itself changes at a limited rate.
    /// - Grip: the velocity swings round after the nose (a little slide through a turn) rather than at once.
    /// </summary>
    public struct RogueFlight
    {
        /// <summary>Degrees per second about the ship's right (pitch), up (yaw) and forward (roll) axes.</summary>
        public Vector3 Spin;
        public float Speed;

        /// <summary>How hard it's turning (0..1), for the jets and the speed it bleeds.</summary>
        public float Turning { get; private set; }

        /// <summary>The slowest it flies, as a share of its cruise speed.</summary>
        public const float MinShare = 0.4f;

        /// <summary>How hard it can change speed (m/s²): what Steering's arrival brakes against.</summary>
        public static float Accel(float topSpeed) => math.max(5f, topSpeed) * 0.8f;

        /// <summary>One tick toward `desiredVelocity`, for a ship cruising at `speed` with `topSpeed` and `turnRate` (°/s).</summary>
        public void Step(ref Quaternion rotation, ref Vector3 velocity, Vector3 desiredVelocity, float speed, float turnRate,
                         float topSpeed, float dt)
        {
            turnRate = math.max(10f, turnRate);
            var forward = rotation * Vector3.forward;
            var wantSpeed = desiredVelocity.magnitude;
            var heading = forward;
            if (wantSpeed > 1e-3f)
            {
                var along = desiredVelocity / wantSpeed;
                heading = desiredVelocity + Vector3.ProjectOnPlane(desiredVelocity - velocity, along) * 0.6f;
                heading = heading.sqrMagnitude > 1e-6f ? heading.normalized : along;
            }
            var local = Quaternion.Inverse(rotation) * heading;
            var off = Vector3.Angle(Vector3.forward, local);

            // Roll: bank so the goal sits overhead for a big turn; for a small one, wings level to the world's up.
            // (Unity's +z roll lifts the right wing, so the error is minus the angle round from the ship's up.)
            float rollError;
            if (off > 14f && new Vector2(local.x, local.y).sqrMagnitude > 1e-4f)
                rollError = -math.degrees(math.atan2(local.x, local.y));
            else
            {
                var up = Quaternion.Inverse(rotation) * Vector3.up;
                rollError = -math.degrees(math.atan2(up.x, math.max(0.05f, up.y)));
            }
            // How well the bank lines the turn up: the pull comes in as the roll gets there.
            var lined = math.saturate(math.cos(math.radians(math.min(90f, math.abs(rollError)))) * 1.2f);
            // Pitch: pull up toward it (+x pitches the nose down); pushing down is weaker, as in a real fighter.
            var pitchError = -math.degrees(math.atan2(local.y, math.max(0.001f, local.z)));
            if (local.z < 0f) pitchError = -180f;   // behind: a full pull (the bank has put it overhead)
            if (pitchError > 0f) pitchError *= 0.5f;
            var yawError = math.degrees(math.atan2(local.x, math.max(0.001f, math.abs(local.z))));

            var command = new Vector3(
                math.clamp(pitchError * 3f * (off > 14f ? lined : 1f), -turnRate, turnRate),
                math.clamp(yawError * 2f, -turnRate * 0.35f, turnRate * 0.35f),
                math.clamp(rollError * 3f, -turnRate * 2.4f, turnRate * 2.4f));
            var accel = turnRate * 4f * dt;
            Spin = new Vector3(
                Mathf.MoveTowards(Spin.x, command.x, accel),
                Mathf.MoveTowards(Spin.y, command.y, accel),
                Mathf.MoveTowards(Spin.z, command.z, accel * 2.4f));
            rotation = (rotation * Quaternion.Euler(Spin * dt)).normalized;

            // Energy: hard turns cost speed; speed changes at a limited rate.
            Turning = math.saturate(math.abs(Spin.x) / turnRate + math.abs(Spin.y) / (turnRate * 0.35f) * 0.3f);
            var aligned = math.saturate(Vector3.Dot(rotation * Vector3.forward, heading));
            var target = math.clamp(wantSpeed, speed * MinShare, topSpeed) * (1f - 0.3f * Turning) * math.lerp(0.8f, 1f, aligned);
            Speed = Mathf.MoveTowards(Speed, target, Accel(topSpeed) * dt);
            // Grip: the velocity swings round after the nose.
            velocity = Vector3.Lerp(velocity, rotation * Vector3.forward * Speed, 1f - math.exp(-2.2f * dt));
        }
    }
}
