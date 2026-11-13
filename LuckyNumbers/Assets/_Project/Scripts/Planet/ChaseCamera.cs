using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A chase camera's feel (PlanetScene.landing.feel, FlightFeel), shared by the planet, the Flight School and
    /// outside the station: it springs after the spot its owner picks (critically damped, so it eases off and on
    /// without a jerk and hangs back further the faster you go), aims a little into turns, lets its up trail the
    /// ship's, rumbles while the engines work hard and shakes with trauma (hits, crashes; the shake is its square).
    /// Each owner still picks the spot (and keeps it out of what's solid between Follow and Aim). No allocations.
    /// </summary>
    public sealed class ChaseCamera
    {
        Vector3 _pos, _vel, _up = Vector3.up, _lead;
        float _trauma, _rumble, _baseFov = -1f, _fovExtra;

        public Vector3 Position => _pos;

        /// <summary>Jump there (a new flight, a respawn): no spring, no lead, up as given.</summary>
        public void Snap(Vector3 position, Vector3 up)
        {
            _pos = position;
            _vel = _lead = Vector3.zero;
            _up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
        }

        /// <summary>A hit or crash: 0..1 more trauma (the shake is trauma squared, fading at the owner's decay).</summary>
        public void AddTrauma(float amount) => _trauma = math.saturate(_trauma + math.max(0f, amount));

        /// <summary>Spring after `want`; returns the new spot (the owner may push it out of a hull or the ground: Place).</summary>
        public Vector3 Follow(Vector3 want, float dt, FlightFeel f)
        {
            var t = f?.FollowSeconds ?? 0f;
            if (t > 0.01f) _pos = Vector3.SmoothDamp(_pos, want, ref _vel, t, Mathf.Infinity, dt);
            else _pos = Vector3.Lerp(_pos, want, 1f - math.exp(-6f * dt));
            return _pos;
        }

        /// <summary>Where it ended up after the owner kept it out of something solid.</summary>
        public void Place(Vector3 position) => _pos = position;

        /// <summary>
        /// Point `cam` at `target`, led into the turn (`spin`: the flight's degrees/s, x pitch nose down, y yaw right,
        /// against `turnRate`; in the ship's `frame`), its up easing after `up`. `rumble` 0..1 (boost, hard thrust)
        /// and the trauma (at most `shakeAngle` degrees and `shakeOffset` m, fading at `shakeDecay` a second) shake it.
        /// </summary>
        public void Aim(Transform cam, Vector3 target, Quaternion frame, Vector3 spin, float turnRate, Vector3 up,
                        float length, float rumble, float dt, FlightFeel f,
                        float shakeAngle = 0f, float shakeOffset = 0f, float shakeDecay = 1.4f)
        {
            // Lead: aim a little into the turn (eased), so you see where you're going.
            var rate = math.max(1f, turnRate);
            var leadWant = frame * new Vector3(math.clamp(spin.y / rate, -1f, 1f), math.clamp(-spin.x / rate, -1f, 1f), 0f)
                           * (length * math.max(0f, f?.LookAhead ?? 0f));
            _lead = Vector3.Lerp(_lead, leadWant, 1f - math.exp(-math.max(0.5f, f?.LookAheadRate ?? 4f) * dt));

            // Up trails the ship's a moment (so a roll reads on the hull before the view follows).
            var lag = f?.RollLag ?? 0f;
            _up = lag > 0f ? Vector3.Slerp(_up, up, 1f - math.exp(-lag * dt)) : up;

            var look = target + _lead - _pos;
            if (look.sqrMagnitude < 1e-4f) look = frame * Vector3.forward;
            var camUp = _up;
            if (math.abs(Vector3.Dot(look.normalized, camUp.normalized)) > 0.98f) camUp = frame * Vector3.up;
            var rotation = Quaternion.LookRotation(look, camUp);

            // Shake: trauma squared plus the engines' rumble, smooth noise (not a jitter).
            _trauma = math.max(0f, _trauma - math.max(0.1f, shakeDecay) * dt);
            _rumble = Mathf.Lerp(_rumble, math.saturate(rumble), 1f - math.exp(-(rumble > _rumble ? 6f : 3f) * dt));
            var shake = _trauma * _trauma;
            var hum = _rumble;
            var offset = Vector3.zero;
            if (shake > 1e-4f || hum > 1e-3f)
            {
                var t = Time.time * 18f;
                var h = Time.time * math.max(1f, f?.RumbleFrequency ?? 22f);
                var angle = math.max(0f, shakeAngle) * shake;
                var hAngle = math.max(0f, f?.RumbleAngle ?? 0f) * hum;
                rotation *= Quaternion.Euler(N(1.3f, t) * angle + N(21.3f, h) * hAngle,
                                             N(7.9f, t) * angle + N(27.1f, h) * hAngle,
                                             N(4.1f, t) * angle * 0.6f + N(23.9f, h) * hAngle * 0.5f);
                offset = rotation * new Vector3(N(11.7f, t) * shakeOffset * shake + N(31.7f, h) * length * math.max(0f, f?.RumbleOffset ?? 0f) * hum,
                                                N(15.2f, t) * shakeOffset * shake + N(35.2f, h) * length * math.max(0f, f?.RumbleOffset ?? 0f) * hum,
                                                0f);
            }
            cam.SetPositionAndRotation(_pos + offset, rotation);
        }

        /// <summary>
        /// Widen the view with speed (`speed01`: share of top speed, FlightFeel.speed_fov) plus `extra` degrees (boost),
        /// eased so it breathes rather than jumps. The camera's own field of view is the base.
        /// </summary>
        public void Fov(Camera cam, float speed01, float extra, float dt, FlightFeel f)
        {
            if (_baseFov < 0f) _baseFov = cam.fieldOfView;
            var want = math.max(0f, f?.SpeedFov ?? 0f) * math.saturate(speed01) + extra;
            _fovExtra = Mathf.Lerp(_fovExtra, want, 1f - math.exp(-(want > _fovExtra ? 6f : 3f) * dt));
            cam.fieldOfView = _baseFov + _fovExtra;
        }

        static float N(float seed, float t) => Mathf.PerlinNoise(seed, t) * 2f - 1f;
    }
}
