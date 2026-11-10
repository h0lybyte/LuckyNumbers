using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Seed -> solar system. Pure and deterministic: the same seed and rules always give the same bodies.
    /// Output is ordinary CelestialBody data, so the result goes through the same SpaceWorld/OrbitSystem
    /// as hand-placed content. The first planet is named "home" (the camera's default target).
    /// </summary>
    public static class UniverseGenerator
    {
        /// <summary>
        /// Bodies for a roll. Dice with a role (roll.Star/Worlds/Luck in 0..1) pick that aspect directly;
        /// -1 leaves it to the seed. Everything else comes from the seed.
        /// </summary>
        public static List<CelestialBody> Generate(SystemGenerator rules, in UniverseSeedChanged roll)
        {
            if (rules.Stars.Count == 0 || rules.Planets.Count == 0)
                throw new ArgumentException("SystemGenerator needs at least one star and one planet look");
            var seed = roll.Seed;
            var rng = new Random(seed == 0 ? 0x9E3779B9u : seed);
            var bodies = new List<CelestialBody>();
            var luck = Role(roll.Luck, ref rng);

            bodies.Add(new CelestialBody
            {
                Id = "sun",
                Position = new Vec3(0, 0, 0),
                Scale = rng.NextFloat(rules.StarScaleMin, rules.StarScaleMax),
                Star = rules.Stars[RoleIndex(roll.Star, rules.Stars.Count, ref rng)].Clone(),
            });

            var star = bodies[0];
            var planetCount = rules.MinPlanets + RoleIndex(roll.Worlds, rules.MaxPlanets - rules.MinPlanets + 1, ref rng);
            var moonChance = math.saturate(rules.MoonChance * math.lerp(0.4f, 1.6f, luck));
            var radius = rules.FirstOrbitRadius;
            var prevOuter = star.Scale * 0.5f;   // inner edge for the next orbit: star surface, then each system's apoapsis
            for (var i = 0; i < planetCount; i++)
            {
                var id = i == 0 ? "home" : $"planet-{i + 1}";
                var scale = rng.NextFloat(rules.PlanetScaleMin, rules.PlanetScaleMax);
                var orbit = OrbitAround("sun", radius, rules, ref rng);
                var e = orbit.Eccentricity;

                // Moons first: they decide how much room this planet's system needs.
                var moons = new List<CelestialBody>();
                var wantMoons = rules.Moons.Count > 0 && rng.NextFloat() < moonChance ? rng.NextInt(1, rules.MaxMoons + 1) : 0;
                var extent = scale * 0.5f;
                for (var m = 0; m < wantMoons; m++)
                {
                    var moonScale = scale * rules.MoonScaleRatio * rng.NextFloat(0.7f, 1.3f);
                    var moonRadius = scale * rng.NextFloat(rules.MoonOrbitMin, rules.MoonOrbitMax) * math.pow(1.6f, m);
                    moons.Add(new CelestialBody
                    {
                        Id = $"{id}-moon-{m + 1}",
                        Position = new Vec3(0, 0, 0),
                        Scale = moonScale,
                        Planet = Vary(Pick(rules.Moons, ref rng), ref rng),
                        Orbit = new Orbit
                        {
                            Around = id,
                            Radius = moonRadius,
                            PeriodSeconds = 24f * math.pow(moonRadius / scale, 1.5f), // Kepler shape, in parent diameters
                            Phase = rng.NextFloat(),
                            InclinationDegrees = rng.NextFloat(-15f, 15f),
                        },
                    });
                    extent = math.max(extent, moonRadius + moonScale * 0.5f);
                }

                // Clearance: periapsis minus this system's extent must clear the previous system's apoapsis.
                radius = math.max(radius, (prevOuter + rules.MinOrbitGap + extent) / (1f - e));
                // Hill sphere: moons stay inside a stable share of it, so push the orbit out until they fit.
                var massRatio = rules.PlanetStarDensityRatio * math.pow(scale / star.Scale, 3f);
                var hillPerA = (1f - e) * math.pow(massRatio / 3f, 1f / 3f);
                if (moons.Count > 0 && rules.MoonHillFraction > 0f)
                    radius = math.max(radius, extent / (rules.MoonHillFraction * hillPerA));
                orbit.Radius = radius;
                orbit.PeriodSeconds = rules.SecondsPerOrbitUnit * math.pow(radius, 1.5f); // Kepler's third law shape

                bodies.Add(new CelestialBody
                {
                    Id = id,
                    Position = new Vec3(0, 0, 0),
                    Scale = scale,
                    Planet = Vary(Pick(rules.Planets, ref rng), ref rng),
                    Orbit = orbit,
                });
                bodies.AddRange(moons);
                prevOuter = radius * (1f + e) + extent;
                radius *= rng.NextFloat(rules.OrbitSpacingMin, rules.OrbitSpacingMax);
            }

            var galaxies = rules.GalaxyCount + (int)math.round(luck * 4f - 2f);
            for (var g = 0; g < galaxies && rules.Galaxies.Count > 0; g++)
            {
                var dir = math.normalize(rng.NextFloat3Direction());
                var look = Pick(rules.Galaxies, ref rng).Clone();
                look.Seed = rng.NextFloat(0f, 100f);
                look.RollDegrees = rng.NextFloat(0f, 360f);
                look.Squash = rng.NextFloat(0.05f, 0.75f);
                bodies.Add(new CelestialBody
                {
                    Id = $"galaxy-{g + 1}",
                    Position = new Vec3(dir.x * rules.GalaxyDistance, dir.y * rules.GalaxyDistance, dir.z * rules.GalaxyDistance),
                    Scale = rng.NextFloat(rules.GalaxyScaleMin, rules.GalaxyScaleMax),
                    Galaxy = look,
                });
            }
            return bodies;
        }

        /// <summary>The sky for a seed: a preset with its own seed, band direction and gas shape. Null if no presets.</summary>
        public static StarfieldLook Sky(SystemGenerator rules, in UniverseSeedChanged roll)
        {
            if (rules.Skies.Count == 0) return null;
            var rng = new Random((roll.Seed ^ 0x5BD1E995u) | 1u);
            var luck = Role(roll.Luck, ref rng);
            var sky = Pick(rules.Skies, ref rng).Clone();
            var n = rng.NextFloat3Direction();
            sky.BandNormal = new Vec3(n.x, n.y, n.z);
            sky.Seed = rng.NextFloat(0f, 500f);
            sky.BandWidth = sky.BandWidth * rng.NextFloat(0.75f, 1.35f);
            sky.NebulaScale = sky.NebulaScale * rng.NextFloat(0.8f, 1.3f);
            sky.Warp = sky.Warp * rng.NextFloat(0.6f, 1.5f);
            sky.Dust = math.saturate(sky.Dust * rng.NextFloat(0.6f, 1.3f));
            sky.NebulaIntensity = sky.NebulaIntensity * math.lerp(0.75f, 1.35f, luck);   // luckier = richer sky
            sky.KnotBrightness = sky.KnotBrightness * math.lerp(0.6f, 1.6f, luck);
            return sky;
        }

        /// <summary>The forming-system dust disk for a roll. Null if no presets.</summary>
        public static DiskLook Disk(SystemGenerator rules, in UniverseSeedChanged roll)
        {
            if (rules.Disks.Count == 0) return null;
            var rng = new Random((roll.Seed ^ 0x27D4EB2Fu) | 1u);
            var disk = Pick(rules.Disks, ref rng).Clone();
            disk.Seed = rng.NextFloat(0f, 100f);
            return disk;
        }

        static float Role(float value, ref Random rng) => value >= 0f ? math.saturate(value) : rng.NextFloat();

        static int RoleIndex(float value, int count, ref Random rng) =>
            value >= 0f ? math.min(count - 1, (int)(math.saturate(value) * count)) : rng.NextInt(count);

        static Orbit OrbitAround(string parent, float radius, SystemGenerator rules, ref Random rng) => new()
        {
            Around = parent,
            Radius = radius,
            PeriodSeconds = rules.SecondsPerOrbitUnit * math.pow(radius, 1.5f), // Kepler's third law shape
            Phase = rng.NextFloat(),
            Eccentricity = rng.NextFloat(0f, rules.MaxEccentricity),
            InclinationDegrees = rng.NextFloat(-rules.MaxInclinationDegrees, rules.MaxInclinationDegrees),
            NodeDegrees = rng.NextFloat(0f, 360f),
        };

        /// <summary>A preset with per-body variation, so two planets from one preset never look identical.</summary>
        static PlanetLook Vary(PlanetLook preset, ref Random rng)
        {
            var look = preset.Clone();
            look.NoiseOffset = new Vec3(rng.NextFloat(0f, 100f), rng.NextFloat(0f, 100f), rng.NextFloat(0f, 100f));
            look.AxialTiltDegrees = rng.NextFloat(-35f, 35f);
            look.RotationSpeed = preset.RotationSpeed * rng.NextFloat(0.6f, 1.6f);
            return look;
        }

        static T Pick<T>(List<T> items, ref Random rng) => items[rng.NextInt(items.Count)];
    }

    /// <summary>Lucky numbers: dice faces chained onto the previous number (FNV-1a), so rolls keep exploring.</summary>
    /// <summary>Readable, seeded names for bodies ("planet-3" in system 1234 -> "Velmor III").</summary>
    public static class BodyNames
    {
        static readonly string[] Syllables = { "ka", "vel", "mor", "ri", "tan", "lu", "xe", "dra", "on", "pha", "sun", "ae", "quil", "bor", "nyx", "ti" };
        static readonly string[] Numerals = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII" };

        /// <summary>The system's name from the seed, then the body's place in it (index in Worlds order).</summary>
        public static string Planet(uint seed, int index)
        {
            var rng = new Unity.Mathematics.Random((seed ^ 0x5EEDBEEFu) | 1u);
            var name = "";
            for (var i = rng.NextInt(2, 4); i > 0; i--) name += Syllables[rng.NextInt(Syllables.Length)];
            name = char.ToUpperInvariant(name[0]) + name.Substring(1);
            return $"{name} {Numerals[Unity.Mathematics.math.clamp(index, 0, Numerals.Length - 1)]}";
        }
    }

    public static class LuckyNumber
    {
        public static uint Next(uint previous, IReadOnlyList<int> faces, IReadOnlyList<int> sides)
        {
            var h = 2166136261u ^ previous;
            for (var i = 0; i < faces.Count; i++)
            {
                h = (h ^ (uint)sides[i]) * 16777619u;
                h = (h ^ (uint)faces[i]) * 16777619u;
            }
            return h == 0 ? 1u : h;
        }
    }
}
