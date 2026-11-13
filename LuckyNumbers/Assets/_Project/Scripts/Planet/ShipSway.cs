using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// How the hull moves on top of the flight (FlightFeel): it banks into turns and strafes, and hovering or slow it
    /// bobs and wobbles a little, so a ship at rest still looks alive. Looks only: the flight, its aim (roll only, and
    /// a fraction of a degree of wobble when slow) and its hitbox stay on the steady ship. No allocations.
    /// </summary>
    public sealed class ShipSway
    {
        float _bank, _time, _calm;

        /// <summary>The hull's offset (world) and tilt (on the right of its rotation, before a barrel roll) this frame.</summary>
        public Vector3 Offset { get; private set; }
        public Quaternion Tilt { get; private set; } = Quaternion.identity;

        public void Reset()
        {
            _bank = _calm = 0f;
            Offset = Vector3.zero;
            Tilt = Quaternion.identity;
        }

        /// <summary>
        /// One frame: `rotation` and `velocity` of the flight, its `spin` (degrees/s, y yaw right), the ship's strafe
        /// and top speeds (m/s), `length` (m) and `settle` 0..1 (how much bob is allowed: low over the ground, less).
        /// </summary>
        public void Step(Quaternion rotation, Vector3 velocity, Vector3 spin, float strafeSpeed, float topSpeed,
                         float length, float settle, float dt, FlightFeel f)
        {
            if (f == null) { Reset(); return; }
            var local = Quaternion.Inverse(rotation) * velocity;
            var max = math.max(0f, f.MaxBank);
            var want = math.clamp(-spin.y * f.BankPerYaw - local.x / math.max(1f, strafeSpeed) * f.StrafeBank, -max, max);
            _bank = Mathf.Lerp(_bank, want, 1f - math.exp(-math.max(0.5f, f.BankRate) * dt));

            var slow = 1f - math.saturate(velocity.magnitude / math.max(1f, topSpeed * math.max(0.01f, f.BobSpeed)));
            _calm = Mathf.Lerp(_calm, slow * math.saturate(settle), 1f - math.exp(-2f * dt));
            _time += dt;
            var w = math.PI * 2f / math.max(0.5f, f.BobSeconds);
            var t = _time * w;
            var bob = length * math.max(0f, f.Bob) * _calm;
            Offset = rotation * new Vector3(math.sin(t * 0.53f + 1.3f) * 0.35f * bob, math.sin(t) * bob, 0f);
            var deg = math.max(0f, f.BobDegrees) * _calm;
            Tilt = Quaternion.Euler(math.sin(t * 0.71f + 0.4f) * deg * 0.5f, 0f, _bank + math.sin(t * 0.89f + 2.1f) * deg);
        }
    }
}
