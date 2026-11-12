using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// How a rogue flies (its pilot, RogueBrain, says where to and how fast): like a fighter, not a turret.
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

        public void Step(ref Quaternion rotation, ref Vector3 velocity, Vector3 desired, float want, float turnRate,
                         float topSpeed, float dt)
        {
            turnRate = math.max(10f, turnRate);
            var local = Quaternion.Inverse(rotation) * (desired.sqrMagnitude > 1e-6f ? desired.normalized : rotation * Vector3.forward);
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
            var target = want * (1f - 0.3f * Turning);
            Speed = Mathf.MoveTowards(Speed, target, math.max(5f, topSpeed) * 0.8f * dt);
            // Grip: the velocity swings round after the nose.
            velocity = Vector3.Lerp(velocity, rotation * Vector3.forward * Speed, 1f - math.exp(-2.2f * dt));
        }
    }
}
