using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The barrel roll (Shift+Q / Shift+E, a fresh press; PlanetScene.landing.hover barrel_roll_*): one full roll that
    /// way, eased in and out, carrying the ship barrel_roll_distance sideways the same way. The sideways move is
    /// applied to the position, not the velocity, so the flight assist (which eases velocity back to what the keys ask
    /// for) can't cancel it; at the end the ship keeps barrel_roll_dodge m/s of sideways drift for the assist to ease
    /// out. "Sideways" is the ship's right, level with the ground, fixed when the roll starts. Shared by the planet
    /// (LandingPresenter), the Flight School and outside the station (HoverFlight).
    ///
    /// The roll itself is only the hull's: Offset turns the model about its nose, while the flight's rotation (what
    /// steering, thrust and the chase camera use) stays as it was. Rolling that rotation instead spun the camera with
    /// the ship and turned mouse yaw into pitch halfway round, so you came out of a roll pointing somewhere else.
    /// </summary>
    public sealed class BarrelRoll
    {
        float _t = -1f, _done, _dir, _ready, _angle;
        Vector3 _side;
        bool _wasHeld;

        static FlightHover H => GameContent.Planet.Landing.Hover;

        public bool Active => _t >= 0f;
        /// <summary>-1 left, +1 right (the last roll's).</summary>
        public float Direction => _dir;

        /// <summary>The hull's roll about its nose right now (identity when not rolling): pose the model with rotation * Offset.</summary>
        public Quaternion Offset => Active ? Quaternion.Euler(0f, 0f, _angle) : Quaternion.identity;

        /// <summary>
        /// Call every frame with the roll axis (Q -1 .. E +1) and Shift: a fresh press with Shift held (not rolling,
        /// cooled down, `allowed`) starts a roll. True when one starts.
        /// </summary>
        public bool TryStart(float roll, bool shift, Quaternion rotation, bool allowed = true)
        {
            var held = math.abs(roll) > 0.5f;
            var start = held && !_wasHeld && shift && !Active && Time.time >= _ready && allowed && H.BarrelRollSeconds > 0f;
            _wasHeld = held;
            if (!start) return false;
            _t = 0f;
            _done = 0f;
            _angle = 0f;
            _dir = math.sign(roll);
            var right = Vector3.ProjectOnPlane(rotation * Vector3.right, Vector3.up);
            if (right.sqrMagnitude < 1e-4f) right = rotation * Vector3.right;   // nose straight up or down
            _side = right.normalized * _dir;
            return true;
        }

        /// <summary>This frame of the roll (if one's going): turns the hull (Offset) and moves `position`; at its end, the drift.</summary>
        public void Step(float dt, ref Vector3 position, ref Vector3 velocity)
        {
            if (!Active) return;
            _t += dt;
            var p = math.smoothstep(0f, 1f, math.saturate(_t / H.BarrelRollSeconds));
            var step = p - _done;
            _done = p;
            // The same way Q/E would roll (Q: the left wing goes down).
            _angle = -_dir * p * 360f;
            position += _side * (step * math.max(0f, H.BarrelRollDistance));
            if (p < 1f) return;
            _t = -1f;
            _ready = Time.time + math.max(0f, H.BarrelRollCooldown);
            velocity += _side * math.max(0f, H.BarrelRollDodge);
        }

        public void Cancel()
        {
            _t = -1f;
            _angle = 0f;
        }
    }
}
