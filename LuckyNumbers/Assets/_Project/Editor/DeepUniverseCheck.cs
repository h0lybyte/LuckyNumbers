using System.Text;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using Unity.Mathematics;
using UnityEditor;
using MenuItem = UnityEditor.MenuItem;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// The second universe mustn't move the first: home's ground heights (GroundShape with no terraces or pad) equal the
    /// original formula bit for bit on a grid, and the home universe's generator (content/universes.txtpb) gives the same
    /// systems as the scenes' own backdrop generator did for many seeds. Then the Emberdeep: its gate lands within range,
    /// deterministically, on a dead-flat pad; its ground really is terraced. Batch:
    /// -executeMethod LuckyNumbers.Editor.DeepUniverseCheck.Run (tools/ln.py deep-check); exits 1 on a failure.
    /// </summary>
    public static class DeepUniverseCheck
    {
        [MenuItem("LuckyNumbers/Checks/Deep Universe")]
        public static void Run()
        {
            var failures = 0;
            void Fail(string what) { failures++; Debug.LogError($"[DeepCheck] FAIL {what}"); }

            // 1. Home ground: the original GroundShape.HeightAt, inline, against the new one.
            var rolling = GameContent.Planet.Backdrop.Generator.Ground;
            var rng = new Random(12345u);
            var points = 0;
            for (var s = 0; s < 8; s++)
            {
                var shape = new GroundShape
                {
                    Height = rolling.Height, Scale = rolling.NoiseScale, Offset = rng.NextFloat2(0f, 100f),
                    FlatRadius = 108f, SiteRelief = math.saturate(rolling.SiteRelief), SwellHeight = rolling.SwellHeight,
                    SwellScale = rolling.SwellScale > 0f ? rolling.SwellScale : 0.25f,
                };
                for (var z = -900; z <= 900; z += 7)
                for (var x = -900; x <= 900; x += 7)
                {
                    var p = new float2(x + 0.37f * s, z - 0.11f * s);
                    var want = new OriginalShape { Height = shape.Height, Scale = shape.Scale, FlatRadius = shape.FlatRadius, SiteRelief = shape.SiteRelief,
                                                   SwellHeight = shape.SwellHeight, SwellScale = shape.SwellScale, Offset = shape.Offset }.HeightAt(p);
                    var inline = OldHeight(shape, p);
                    if (math.asuint(want) != math.asuint(inline)) Debug.LogWarning($"[DeepCheck] inline copy vs original struct differ at {p}: {math.asuint(inline):x} {math.asuint(want):x}");
                    var got = shape.HeightAt(p);
                    points++;
                    if (math.asuint(want) != math.asuint(got)) { Fail($"home height at {p}: {math.asuint(got):x} != {math.asuint(want):x}"); goto heightsDone; }
                }
            }
            heightsDone:
            Debug.Log($"[DeepCheck] home ground: {points} points bit-identical to the original formula");

            // 2. Home systems: the universe catalog's home generator vs the scenes' backdrop generator.
            var backdrop = GameContent.Planet.Backdrop.Generator;
            var home = Multiverse.Generator("home", null);
            if (home == null || Multiverse.Find("") ?.Id != "home") Fail("home is not the default universe");
            var seeds = 0;
            for (uint seed = 1; seed < 4000 && home != null; seed += 37)
            {
                foreach (var dice in new[] { -1f, 0f, 0.5f, 1f })
                {
                    var roll = new UniverseSeedChanged(seed * 2654435761u | 1u, dice, dice, dice, dice);
                    var a = Fingerprint(UniverseGenerator.Generate(backdrop, roll), UniverseGenerator.Sky(backdrop, roll), UniverseGenerator.Disk(backdrop, roll));
                    var b = Fingerprint(UniverseGenerator.Generate(home, roll), UniverseGenerator.Sky(home, roll), UniverseGenerator.Disk(home, roll));
                    seeds++;
                    if (a != b) { Fail($"home system differs for seed {roll.Seed}"); break; }
                }
            }
            Debug.Log($"[DeepCheck] home systems: {seeds} rolls identical to the backdrop generator");
            if (Multiverse.HasHolds("home")) Fail("home has holds");

            // 3. The Emberdeep: gate placement, its pad, terraces.
            var deep = Multiverse.Find("emberdeep");
            if (deep == null || deep.Id != "emberdeep" || !deep.Holds) Fail("no emberdeep universe with holds");
            else
            {
                var gate = GameContent.Planet.Gate;
                var land = deep.Generator.Ground;
                var bodies = UniverseGenerator.Generate(deep.Generator, new UniverseSeedChanged(777u, -1f, -1f, -1f, -1f));
                if (bodies.Find(b => b.Id == "home")?.Ground?.TerraceHeight <= 0f) Fail("emberdeep worlds have no terraces");
                for (uint seed = 1; seed < 200; seed += 13)
                {
                    var g = GatePlacement.Place(seed, gate);
                    var again = GatePlacement.Place(seed, gate);
                    var d = math.length(new float2(g.Position.x, g.Position.z));
                    if (!g.Exists || g.Position != again.Position || g.Yaw != again.Yaw) Fail($"gate not deterministic for {seed}");
                    if (d < gate.DistanceMin - 0.01f || d > gate.DistanceMax + 0.01f) Fail($"gate {d:0.0} m out for {seed}");
                    if (math.distance(new float2(g.Position.x, g.Position.z), new float2(0f, GameContent.Planet.WreckDistance)) < GameContent.Planet.WreckLength * 0.5f + gate.PadRadius)
                        Fail($"gate under the wreck for {seed}");
                    // Facing the site's middle.
                    var outward = Quaternion.Euler(0f, g.Yaw, 0f) * Vector3.forward;
                    if (Vector3.Dot(outward, -g.Position.normalized) < 0.99f) Fail($"gate faces away from the site for {seed}");

                    var shape = Terraced(land, new Random(seed | 1u).NextFloat2(0f, 100f));
                    var c = new float2(g.Position.x, g.Position.z);
                    shape.PadRadius = 0f;
                    var h0 = shape.HeightAt(c);
                    shape.PadCentre = c; shape.PadHeight = h0; shape.PadRadius = gate.PadRadius;
                    var spread = 0f;
                    for (var a = 0; a < 360; a += 20)
                    for (var r = 0f; r <= gate.PadRadius; r += 2f)
                        spread = math.max(spread, math.abs(shape.HeightAt(c + r * new float2(math.cos(math.radians(a)), math.sin(math.radians(a)))) - h0));
                    if (spread > 1e-4f) Fail($"gate pad not flat for {seed}: {spread} m");
                }
                // Terraces: out past the site, a good share of the land is flat plateau and some is steep cliff.
                var far = Terraced(land, new float2(31f, 7f));
                int flat = 0, steep = 0, n = 0;
                for (var z = 400; z < 1400; z += 3)
                for (var x = 400; x < 1400; x += 3)
                {
                    var nrm = far.NormalAt(new float2(x, z), 0.5f);
                    n++;
                    if (nrm.y > 0.995f) flat++;
                    else if (nrm.y < 0.5f) steep++;
                }
                Debug.Log($"[DeepCheck] terraced land: {100f * flat / n:0}% flat plateau, {100f * steep / n:0}% cliff (steeper than 60 deg)");
                if (flat < n * 0.3f || steep < n * 0.03f) Fail("the terraced ground isn't terraced enough");
            }

            if (failures == 0) Debug.Log("[DeepCheck] all checks passed");
            if (Application.isBatchMode) EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        static GroundShape Terraced(GroundProfile land, float2 offset) => new()
        {
            Height = land.Height, Scale = land.NoiseScale, Offset = offset, FlatRadius = 108f,
            SiteRelief = math.saturate(land.SiteRelief), SwellHeight = land.SwellHeight, SwellScale = land.SwellScale,
            TerraceHeight = land.TerraceHeight, TerraceSharpness = land.TerraceSharpness, TerraceBreakScale = land.TerraceBreakScale,
            CliffHeight = land.CliffHeight, CliffScale = land.CliffScale,
        };

        /// <summary>GroundShape as it was before terraces and pads (struct and method copied verbatim).</summary>
        struct OriginalShape
        {
            public float Height, Scale, FlatRadius, SiteRelief, SwellHeight, SwellScale;
            public float2 Offset;

            public readonly float HeightAt(float2 p)
            {
                var q = p * Scale + Offset;
                var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
                var swell = noise.snoise(q * SwellScale - 7.7f);
                var r = math.length(p);
                var site = math.lerp(SiteRelief, 1f, math.smoothstep(FlatRadius * 0.6f, FlatRadius * 2f, r));
                var far = math.smoothstep(FlatRadius, FlatRadius * 4f, r);   // swells only away from the site
                return hills * Height * site + swell * SwellHeight * far;
            }
        }

        /// <summary>GroundShape.HeightAt as it was before terraces and pads (copied verbatim).</summary>
        static float OldHeight(in GroundShape s, float2 p)
        {
            var q = p * s.Scale + s.Offset;
            var hills = noise.snoise(q) * 0.7f + noise.snoise(q * 2.7f + 13.1f) * 0.3f;
            var swell = noise.snoise(q * s.SwellScale - 7.7f);
            var r = math.length(p);
            var site = math.lerp(s.SiteRelief, 1f, math.smoothstep(s.FlatRadius * 0.6f, s.FlatRadius * 2f, r));
            var far = math.smoothstep(s.FlatRadius, s.FlatRadius * 4f, r);
            return hills * s.Height * site + swell * s.SwellHeight * far;
        }

        /// <summary>Everything a system is made of, as text (floats as bits).</summary>
        static string Fingerprint(System.Collections.Generic.List<CelestialBody> bodies, StarfieldLook sky, DiskLook disk)
        {
            var sb = new StringBuilder();
            void F(float f) => sb.Append(math.asuint(f).ToString("x")).Append(',');
            foreach (var b in bodies)
            {
                sb.Append(b.Id).Append(':');
                F(b.Position.X); F(b.Position.Y); F(b.Position.Z); F(b.Scale);
                if (b.Orbit != null) { sb.Append(b.Orbit.Around); F(b.Orbit.Radius); F(b.Orbit.PeriodSeconds); F(b.Orbit.Phase); F(b.Orbit.Eccentricity); F(b.Orbit.InclinationDegrees); F(b.Orbit.NodeDegrees); }
                if (b.Planet != null) sb.Append(JsonUtility.ToJson(b.Planet)).Append(b.Planet.PlanetColor.R).Append(b.Planet.NoiseOffset.X).Append(b.Planet.AxialTiltDegrees).Append(b.Planet.RotationSpeed);
                if (b.Star != null) sb.Append(b.Star.CoreColor.R).Append(b.Star.Intensity);
                if (b.Galaxy != null) { F(b.Galaxy.Seed); F(b.Galaxy.RollDegrees); F(b.Galaxy.Squash); F(b.Galaxy.ArmCount); }
                if (b.Ground != null) { F(b.Ground.Height); F(b.Ground.NoiseScale); F(b.Ground.TerraceHeight); }
                sb.Append('|');
            }
            if (sky != null) { F(sky.Seed); F(sky.BandWidth); F(sky.NebulaIntensity); F(sky.SpaceColor.R); F(sky.BandNormal.X); }
            if (disk != null) { F(disk.Seed); F(disk.InnerColor.R); }
            return sb.ToString();
        }
    }
}
