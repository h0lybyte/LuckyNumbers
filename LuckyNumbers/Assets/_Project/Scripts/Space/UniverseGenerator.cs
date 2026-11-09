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
        public static List<CelestialBody> Generate(SystemGenerator rules, uint seed)
        {
            if (rules.Stars.Count == 0 || rules.Planets.Count == 0)
                throw new ArgumentException("SystemGenerator needs at least one star and one planet look");
            var rng = new Random(seed == 0 ? 0x9E3779B9u : seed);
            var bodies = new List<CelestialBody>();

            bodies.Add(new CelestialBody
            {
                Id = "sun",
                Position = new Vec3(0, 0, 0),
                Scale = rng.NextFloat(rules.StarScaleMin, rules.StarScaleMax),
                Star = Pick(rules.Stars, ref rng).Clone(),
            });

            var star = bodies[0];
            var planetCount = rng.NextInt(rules.MinPlanets, rules.MaxPlanets + 1);
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
                var wantMoons = rules.Moons.Count > 0 && rng.NextFloat() < rules.MoonChance ? rng.NextInt(1, rules.MaxMoons + 1) : 0;
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

            for (var g = 0; g < rules.GalaxyCount && rules.Galaxies.Count > 0; g++)
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
