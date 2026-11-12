using Unity.Mathematics;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Launches on a ballistic arc (a monster's thrown flask: CombatPresenter.Ranged): plain maths, no Unity objects,
    /// so it's testable alone. Gravity pulls along -Y.
    /// </summary>
    public static class Ballistic
    {
        /// <summary>
        /// A launch velocity of at most `speed` that lands on `to` from `from` under `gravity` (m/s^2, positive). At
        /// full speed two arcs reach it; it throws between them: the lower (flatter, sooner) one, but never flatter than
        /// `minAngle` (radians up), slowing the throw to suit, so a near target gets a lob rather than a flat dart, and
        /// it never goes over the high arc's apex. Out of reach at this speed: thrown its farthest (45 degrees, full
        /// speed, toward it), and it lands short. Returns whether it reaches; `time` is the flight to `to` (out of
        /// reach: until it's back down to `from`'s height).
        /// </summary>
        public static bool Solve(float3 from, float3 to, float speed, float gravity, out float3 velocity, out float time, float minAngle = 0f)
        {
            speed = math.max(0.01f, speed);
            var d = to - from;
            var flat = new float2(d.x, d.z);
            var x = math.length(flat);
            var y = d.y;
            var dir = x > 1e-4f ? flat / x : new float2(0f, 1f);
            if (gravity <= 1e-4f)
            {
                velocity = math.normalizesafe(d, new float3(0f, 0f, 1f)) * speed;
                time = math.length(d) / speed;
                return true;
            }
            if (x < 1e-3f)
            {
                // Straight up or down: a gentle toss onto it.
                velocity = new float3(0f, y > 0f ? speed : 0f, 0f);
                time = y > 0f ? speed / gravity : math.sqrt(2f * math.max(0f, -y) / gravity);
                return y <= speed * speed / (2f * gravity);
            }
            var v2 = speed * speed;
            var disc = v2 * v2 - gravity * (gravity * x * x + 2f * y * v2);
            bool reaches;
            float cos, sin;
            if (disc >= 0f)
            {
                // tan = (v^2 -+ sqrt(disc)) / (g x): the low and high roots; at least minAngle, at most the high one.
                var root = math.sqrt(disc);
                var low = (v2 - root) / (gravity * x);
                var high = (v2 + root) / (gravity * x);
                var tan = math.clamp(math.tan(math.clamp(minAngle, 0f, 1.5f)), low, high);
                cos = math.rsqrt(1f + tan * tan);
                sin = tan * cos;
                // The speed this angle needs: v^2 = g x^2 / (2 cos^2 (x tan - y)) (no more than `speed`: it's between the roots).
                var lift = x * tan - y;
                if (lift > 1e-4f) speed = math.min(speed, math.sqrt(gravity * x * x / (2f * cos * cos * lift)));
                reaches = true;
            }
            else
            {
                cos = sin = math.SQRT2 * 0.5f;
                reaches = false;
            }
            var h = speed * cos;
            velocity = new float3(dir.x * h, speed * sin, dir.y * h);
            time = reaches ? x / h : 2f * speed * sin / gravity;
            return reaches;
        }

        /// <summary>
        /// Solve, aimed `lead` (0..1) of the way to where a target moving at `targetVelocity` will be when it lands
        /// (one refinement: the flight time to where it is now, then to the led point).
        /// </summary>
        public static bool SolveLeading(float3 from, float3 to, float3 targetVelocity, float lead, float speed, float gravity,
                                        out float3 velocity, out float time, float minAngle = 0f)
        {
            var reaches = Solve(from, to, speed, gravity, out velocity, out time, minAngle);
            if (lead <= 0f || math.lengthsq(targetVelocity.xz) < 1e-4f) return reaches;
            var aim = to + new float3(targetVelocity.x, 0f, targetVelocity.z) * (math.saturate(lead) * time);
            return Solve(from, aim, speed, gravity, out velocity, out time, minAngle);
        }

        /// <summary>Where a launch at `velocity` from `from` is after `t` seconds.</summary>
        public static float3 At(float3 from, float3 velocity, float gravity, float t) =>
            from + velocity * t - new float3(0f, 0.5f * gravity * t * t, 0f);

        /// <summary>The farthest it carries on flat ground at this speed (m): v^2 / g.</summary>
        public static float Reach(float speed, float gravity) => gravity > 1e-4f ? speed * speed / gravity : float.PositiveInfinity;
    }
}
