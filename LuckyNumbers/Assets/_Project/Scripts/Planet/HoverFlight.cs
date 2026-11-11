using LuckyNumbers.Core;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The planet's hover flight (LandingPresenter.HoverMove: PlanetScene.landing.hover and the ship's speed and
    /// agility) with no ground, gravity, fuel or crashes: for flying in space (the Flight School's deck, outside the
    /// station). The caller keeps the ship out of whatever is solid after each Step.
    /// </summary>
    public sealed class HoverFlight
    {
        public Vector3 Position, Velocity;
        public Quaternion Rotation = Quaternion.identity;
        Vector3 _spin, _jetSpin;

        static FlightLanding L => GameContent.Planet.Landing;
        static FlightHover H => L.Hover;

        /// <summary>Top speed (no boost) for a ship with this speed multiplier.</summary>
        public static float TopSpeed(float speed) => H.MaxSpeed * speed;

        public void Stop() => Velocity = _spin = _jetSpin = Vector3.zero;

        /// <summary>
        /// One frame of flying from the player's input: aim (mouse while the pointer is locked or held), thrust in the
        /// ship's frame eased by the assist, and the jets on `ship` to match. `level`: with no steering, it levels
        /// itself (as low over the ground).
        /// </summary>
        public void Step(PlayerInput input, float dt, float speed, float agility, bool level, ShipView ship)
        {
            var pressed = input.PointerPressed.Value;
            if (pressed && Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            var steering = Cursor.lockState == CursorLockMode.Locked || pressed;
            var look = steering ? new Vector2(input.Look.Value.X, input.Look.Value.Y) : Vector2.zero;
            var move = input.Move.Value;
            var roll = input.Roll.Value;
            var sp = math.max(0.1f, speed);
            var ag = math.max(0.1f, agility);
            var boost = input.Sprinting.Value ? math.max(1f, H.Boost) : 1f;

            // Aim.
            var settle = 1f - math.exp(-math.max(0.5f, H.TurnSmoothing) * dt);
            var turn = H.TurnRate * ag;
            var want = new Vector3(math.clamp(-look.y * L.TurnDegreesPerPixel / dt, -turn, turn),
                                   math.clamp(look.x * L.TurnDegreesPerPixel / dt, -turn, turn),
                                   -roll * H.RollRate * ag);
            _spin = Vector3.Lerp(_spin, want, settle);
            Rotation = Rotation * Quaternion.Euler(_spin * dt);
            var turning = math.abs(look.x) + math.abs(look.y) > 0.5f || math.abs(roll) > 0.1f;
            if (!turning && level)
            {
                var nose = Vector3.ProjectOnPlane(Rotation * Vector3.forward, Vector3.up);
                if (nose.sqrMagnitude > 1e-4f)
                    Rotation = Quaternion.RotateTowards(Rotation, Quaternion.LookRotation(nose, Vector3.up), H.LevelRate * ag * dt);
            }

            // Travel: the velocity the keys ask for, in the ship's frame; the assist eases towards it.
            var forward = move.Y > 0.1f ? H.MaxSpeed * sp * boost * move.Y : move.Y < -0.1f ? H.ReverseSpeed * sp * move.Y : 0f;
            var side = math.abs(move.X) > 0.1f ? H.StrafeSpeed * sp * move.X : 0f;
            var vertical = input.Lifting.Value ? H.ClimbSpeed * sp * boost : input.Descending.Value ? -H.DescendSpeed * sp : 0f;
            var target = input.Braking.Value ? Vector3.zero : Rotation * new Vector3(side, vertical, forward);
            var change = target - Velocity;
            var pushing = target.sqrMagnitude > 0.01f && Vector3.Dot(change, target) > 0f;
            var rate = (input.Braking.Value ? H.HardBrake : pushing ? H.Accel : H.Brake) * ag * dt;
            if (change.magnitude > rate) change = change.normalized * rate;
            Velocity += change;
            Position += Velocity * dt;

            // Jets, as on the planet: this frame's change of velocity, plus holding the ship up and its speed.
            ship.SetHeat(0f, 0.6f + 0.9f * (Velocity.magnitude / math.max(1f, H.MaxSpeed * sp)) * boost + (pushing ? 0.5f : 0.2f));
            var toShip = Quaternion.Inverse(Rotation);
            var ask = toShip * (change / math.max(rate, 1e-4f));
            var hold = toShip * (Vector3.up * 0.3f + Velocity / math.max(1f, H.MaxSpeed * sp) * 0.5f);
            ship.SetThrust(ask + hold, JetTurn(dt, H.TurnRate * ag), boost);
        }

        /// <summary>The jets' turn (as LandingPresenter.JetTurn): kick to start and stop a turn, a little burn during it.</summary>
        Vector3 JetTurn(float dt, float rate)
        {
            rate = math.max(1f, rate);
            var change = (_spin - _jetSpin) / math.max(dt, 1e-4f);
            _jetSpin = _spin;
            var t = change / (rate * 4f) + _spin / rate * 0.4f;
            return new Vector3(math.clamp(t.x, -1f, 1f), math.clamp(t.y, -1f, 1f), math.clamp(t.z, -1f, 1f));
        }
    }
}
