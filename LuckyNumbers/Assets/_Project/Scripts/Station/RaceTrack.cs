using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// A seeded race circuit (content/race.txtpb track; Station/Race flies it): a closed loop of control points round a
    /// centre `radius` m out from the start line (each pulled in by up to `radius_jitter` of it, and up or down by up to
    /// `height` m), a smooth curve through them (closed Catmull-Rom), sampled densely so a racer's place on the line is a
    /// quick nearest-sample search. Gates sit evenly along it (gate 0 is the start / finish line), rows of mystery cubes
    /// between some of them, and rocks off to the sides (never on the line itself).
    ///
    /// The start line is at `start`, the loop heading off sideways and curving away along `outward` (from the station),
    /// so no part of it comes back nearer the station than the line. Pure (no scene objects): the views are Race's.
    /// </summary>
    public sealed class RaceTrack
    {
        public struct Gate
        {
            public Vector3 Position, Forward, Right, Up;
            public int Sample;
            public float Distance;
        }

        public struct CubeSpot
        {
            public Vector3 Position;
            // The gate the row comes after, which cube kind (an index into RaceConfig.cubes), and how far along the loop.
            public int AfterGate, Kind;
            public float Distance;
        }

        public readonly List<Vector3> Samples = new();
        public readonly List<Vector3> Tangents = new();
        public readonly List<Vector3> Rights = new();
        // Distance along the loop to each sample (Distances[0] = 0).
        public readonly List<float> Distances = new();
        public readonly List<Gate> Gates = new();
        public readonly List<CubeSpot> Cubes = new();
        public readonly List<Bounds> Hazards = new();
        public float Length { get; private set; }
        public float Spacing { get; private set; }
        public float GateSize { get; private set; }

        const int SubSteps = 48;

        /// <summary>
        /// The circuit for `seed`. `cubeWeights`: each cube kind's weight (RaceConfig.cubes), for picking a row's colours.
        /// </summary>
        public static RaceTrack Generate(RaceTrackRules rules, uint seed, Vector3 start, Vector3 outward,
                                         IReadOnlyList<float> cubeWeights)
        {
            var t = new RaceTrack();
            var rng = new Random(seed | 1u);
            rules ??= new RaceTrackRules();
            var n = math.max(4, rules.Points);
            var radius = math.max(200f, rules.Radius);
            var jitter = math.saturate(rules.RadiusJitter);
            var height = math.max(0f, rules.Height);
            outward = Vector3.ProjectOnPlane(outward, Vector3.up);
            if (outward.sqrMagnitude < 1e-4f) outward = Vector3.left;
            outward.Normalize();
            var side = Vector3.Cross(Vector3.up, outward).normalized;
            var centre = start + outward * radius;

            // Control points round the centre; point 0 is the start line itself (no jitter there, nor much at its
            // neighbours, so the grid has a straight to launch down).
            var points = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                var a = 2f * math.PI * i / n;
                var calm = i == 0 ? 0f : i == 1 || i == n - 1 ? 0.3f : 1f;
                var r = radius * (1f - jitter * rng.NextFloat() * calm);
                var h = rng.NextFloat(-height, height) * calm;
                points[i] = centre + (-outward * math.cos(a) + side * math.sin(a)) * r + Vector3.up * h;
            }

            // The curve, closed.
            for (var i = 0; i < n; i++)
            {
                var p0 = points[(i - 1 + n) % n];
                var p1 = points[i];
                var p2 = points[(i + 1) % n];
                var p3 = points[(i + 2) % n];
                for (var k = 0; k < SubSteps; k++) t.Samples.Add(CatmullRom(p0, p1, p2, p3, (float)k / SubSteps));
            }
            var m = t.Samples.Count;
            var total = 0f;
            for (var i = 0; i < m; i++)
            {
                t.Distances.Add(total);
                total += Vector3.Distance(t.Samples[i], t.Samples[(i + 1) % m]);
            }
            t.Length = total;
            for (var i = 0; i < m; i++)
            {
                var tan = (t.Samples[(i + 1) % m] - t.Samples[(i - 1 + m) % m]).normalized;
                t.Tangents.Add(tan);
                var right = Vector3.Cross(Vector3.up, tan);
                t.Rights.Add(right.sqrMagnitude > 1e-6f ? right.normalized : side);
            }

            // Gates, evenly spaced (the spacing stretched a little so they divide the loop exactly).
            var count = math.max(4, (int)math.floor(total / math.max(50f, rules.GateSpacing)));
            t.Spacing = total / count;
            t.GateSize = math.max(20f, rules.GateSize);
            for (var g = 0; g < count; g++)
            {
                var s = g * t.Spacing;
                var j = t.SampleAt(s);
                var fwd = t.Tangents[j];
                var right = t.Rights[j];
                t.Gates.Add(new Gate
                {
                    Position = t.At(s), Forward = fwd, Right = right, Up = Vector3.Cross(fwd, right).normalized,
                    Sample = j, Distance = s,
                });
            }

            // Cube rows: halfway between two gates, every `cube_every` gates (not the first, so the launch is clean).
            var every = math.max(1, rules.CubeEvery);
            var across = math.max(1, rules.CubeRow);
            var half = t.GateSize * 0.5f;
            for (var g = 1; g < count; g += every)
            {
                var s = g * t.Spacing + t.Spacing * 0.5f;
                var j = t.SampleAt(s);
                var at = t.At(s);
                for (var c = 0; c < across; c++)
                {
                    var lane = across == 1 ? 0f : math.lerp(-0.6f, 0.6f, (float)c / (across - 1));
                    t.Cubes.Add(new CubeSpot
                    {
                        Position = at + t.Rights[j] * (lane * half), AfterGate = g, Distance = s,
                        Kind = PickWeighted(cubeWeights, rng.NextFloat()),
                    });
                }
            }

            // Rocks: off to the sides (past the gate's half-width), never near any part of the line.
            var spread = math.max(0f, rules.HazardSpread);
            var minSize = math.max(2f, rules.HazardSizeMin);
            var maxSize = math.max(minSize, rules.HazardSizeMax);
            for (var i = 0; i < math.max(0, rules.Hazards) * 3 && t.Hazards.Count < rules.Hazards; i++)
            {
                var s = rng.NextFloat(t.Spacing * 1.5f, total - t.Spacing);
                var j = t.SampleAt(s);
                var size = rng.NextFloat(minSize, maxSize);
                var sideways = (rng.NextBool() ? 1f : -1f) * (half + size + rng.NextFloat(0f, spread));
                var at = t.At(s) + t.Rights[j] * sideways + Vector3.up * rng.NextFloat(-spread, spread) * 0.5f;
                var clear = half + size * 0.9f;
                if (Vector3.Distance(at, t.Samples[t.Nearest(at)]) < clear) continue;
                t.Hazards.Add(new Bounds(at, Vector3.one * size));
            }
            return t;
        }

        /// <summary>A weighted pick: the index whose share of the total weight `u` (0..1) lands in. No weights: 0.</summary>
        public static int PickWeighted(IReadOnlyList<float> weights, float u)
        {
            if (weights == null || weights.Count == 0) return 0;
            var sum = 0f;
            foreach (var w in weights) sum += math.max(0f, w);
            if (sum <= 0f) return 0;
            var x = math.saturate(u) * sum;
            for (var i = 0; i < weights.Count; i++)
            {
                x -= math.max(0f, weights[i]);
                if (x < 0f) return i;
            }
            return weights.Count - 1;
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

        /// <summary>`s` wrapped onto 0..Length.</summary>
        public float Wrap(float s)
        {
            if (Length <= 0f) return 0f;
            s %= Length;
            return s < 0f ? s + Length : s;
        }

        /// <summary>The sample at or just before distance `s` along the loop.</summary>
        public int SampleAt(float s)
        {
            s = Wrap(s);
            int lo = 0, hi = Distances.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Distances[mid] <= s) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The point `s` m along the loop.</summary>
        public Vector3 At(float s)
        {
            s = Wrap(s);
            var j = SampleAt(s);
            var k = (j + 1) % Samples.Count;
            var d0 = Distances[j];
            var d1 = k == 0 ? Length : Distances[k];
            var f = d1 > d0 ? (s - d0) / (d1 - d0) : 0f;
            return Vector3.Lerp(Samples[j], Samples[k], f);
        }

        /// <summary>The nearest sample to `p`: searched `window` either side of `hint`, or everywhere (hint &lt; 0).</summary>
        public int Nearest(Vector3 p, int hint = -1, int window = 24)
        {
            var m = Samples.Count;
            var best = 0;
            var bestD = float.MaxValue;
            if (hint < 0 || window * 2 + 1 >= m)
            {
                for (var i = 0; i < m; i++)
                {
                    var d = (Samples[i] - p).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                return best;
            }
            for (var o = -window; o <= window; o++)
            {
                var i = ((hint + o) % m + m) % m;
                var d = (Samples[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// How far sample `a` is past sample `b` going round the loop, in samples, as -half..+half of the loop (so a
        /// racer just behind a gate reads slightly negative, not nearly a whole lap ahead).
        /// </summary>
        public int Ahead(int a, int b)
        {
            var m = Samples.Count;
            var d = ((a - b) % m + m) % m;
            return d > m / 2 ? d - m : d;
        }
    }

    /// <summary>
    /// A mystery cube's roll (content/race.txtpb): d20 + bonus; a natural 1 is the fumble, a natural 20 the cube's nat20
    /// (or the top of its table), else the outcome with the highest `min` at or under the total (under every one: the
    /// lowest).
    /// </summary>
    public static class RaceDice
    {
        public static CubeOutcome Pick(MysteryCube cube, CubeOutcome fumble, int d20, int total)
        {
            if (d20 <= 1 && fumble != null) return fumble;
            CubeOutcome best = null, lowest = null, top = null;
            if (cube != null)
                foreach (var o in cube.Outcomes)
                {
                    if (o.Min <= total && (best == null || o.Min > best.Min)) best = o;
                    if (lowest == null || o.Min < lowest.Min) lowest = o;
                    if (top == null || o.Min > top.Min) top = o;
                }
            if (d20 >= 20) return cube?.Nat20 ?? top ?? best ?? lowest;
            return best ?? lowest;
        }

        /// <summary>Your bonus: the lucky number's share (players only) plus the catch-up for `place` (1 = leading).</summary>
        public static int Bonus(RaceRoll rules, uint luck, bool player, int place)
        {
            if (rules == null) return 0;
            var lucky = player && rules.LuckMax > 0 ? (int)(luck % (uint)(rules.LuckMax + 1)) : 0;
            var catchUp = math.min(math.max(0, rules.CatchUpMax), math.max(0, place - 1) * math.max(0, rules.CatchUpPerPlace));
            return lucky + catchUp;
        }
    }
}
