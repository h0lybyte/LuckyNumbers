using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Mathematics;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Celestial bodies as entities: parallel arrays indexed by entity, built from content
    /// (or later from a dice-seeded generator). Systems read/write these arrays; views mirror them.
    /// </summary>
    public enum BodyKind { Planet, Star, Galaxy }

    public sealed class SpaceWorld
    {
        public readonly string[] Ids;
        public readonly BodyKind[] Kind;
        public readonly float3[] BasePosition;
        public readonly float[] Scale;
        public readonly int[] OrbitParent;     // -1 = static
        public readonly float[] OrbitRadius;
        public readonly float[] OrbitPeriod;
        public readonly float[] OrbitPhase;
        public readonly float[] OrbitEccentricity;
        public readonly quaternion[] OrbitPlane;   // inclination + node, rotates the XZ ellipse
        public readonly PlanetLook[] Planet;   // set when Kind == Planet
        public readonly StarLook[] Star;       // set when Kind == Star
        public readonly GalaxyLook[] Galaxy;   // set when Kind == Galaxy
        public readonly float3[] Position;     // output of OrbitSystem

        public int Count => Ids.Length;

        public SpaceWorld(IReadOnlyList<CelestialBody> bodies)
        {
            var n = bodies.Count;
            Ids = new string[n];
            Kind = new BodyKind[n];
            BasePosition = new float3[n];
            Scale = new float[n];
            OrbitParent = new int[n];
            OrbitRadius = new float[n];
            OrbitPeriod = new float[n];
            OrbitPhase = new float[n];
            OrbitEccentricity = new float[n];
            OrbitPlane = new quaternion[n];
            Planet = new PlanetLook[n];
            Star = new StarLook[n];
            Galaxy = new GalaxyLook[n];
            Position = new float3[n];

            var index = new Dictionary<string, int>(n);
            for (var i = 0; i < n; i++)
            {
                var b = bodies[i];
                if (string.IsNullOrEmpty(b.Id) || !index.TryAdd(b.Id, i))
                    throw new ArgumentException($"celestial body #{i}: id '{b.Id}' is empty or duplicated");
                Ids[i] = b.Id;
                BasePosition[i] = new float3(b.Position.X, b.Position.Y, b.Position.Z);
                Scale[i] = b.Scale;
                switch (b.Appearance)
                {
                    case CelestialBody.AppearanceCase.Planet: Kind[i] = BodyKind.Planet; Planet[i] = b.Planet; break;
                    case CelestialBody.AppearanceCase.Star: Kind[i] = BodyKind.Star; Star[i] = b.Star; break;
                    case CelestialBody.AppearanceCase.Galaxy: Kind[i] = BodyKind.Galaxy; Galaxy[i] = b.Galaxy; break;
                    default: throw new ArgumentException($"'{b.Id}' needs a planet {{ }}, star {{ }} or galaxy {{ }} appearance");
                }
                OrbitParent[i] = -1;
                if (b.Orbit != null && b.Orbit.Radius > 0f)
                {
                    // Parents must come first so one pass in list order resolves positions.
                    if (!index.TryGetValue(b.Orbit.Around, out var parent))
                        throw new ArgumentException($"'{b.Id}' orbits '{b.Orbit.Around}', which isn't listed before it");
                    OrbitParent[i] = parent;
                    OrbitRadius[i] = b.Orbit.Radius;
                    OrbitPeriod[i] = math.max(0.01f, b.Orbit.PeriodSeconds);
                    OrbitPhase[i] = b.Orbit.Phase;
                    OrbitEccentricity[i] = math.clamp(b.Orbit.Eccentricity, 0f, 0.95f);
                    OrbitPlane[i] = math.mul(quaternion.RotateY(math.radians(b.Orbit.NodeDegrees)),
                                             quaternion.RotateX(math.radians(b.Orbit.InclinationDegrees)));
                }
                else OrbitPlane[i] = quaternion.identity;
            }
        }

        public int IndexOf(string id) => Array.IndexOf(Ids, id);

        /// <summary>First star, the light source for every planet; -1 if none.</summary>
        public int FirstStar() => Array.IndexOf(Kind, BodyKind.Star);
    }

    /// <summary>
    /// Positions every body for time t (seconds) on Keplerian orbits: ellipse with the parent at a focus,
    /// tilted by inclination/node. Pure function over the arrays; parents are updated before children.
    /// </summary>
    public static class OrbitSystem
    {
        public static void Update(SpaceWorld w, float t)
        {
            for (var i = 0; i < w.Count; i++)
            {
                var p = w.OrbitParent[i];
                w.Position[i] = p < 0
                    ? w.BasePosition[i]
                    : w.Position[p] + w.BasePosition[i] + OrbitOffset(w.OrbitRadius[i], w.OrbitEccentricity[i],
                          w.OrbitPhase[i] + t / w.OrbitPeriod[i], w.OrbitPlane[i]);
            }
        }

        /// <summary>Offset from the parent for a semi-major axis a, eccentricity e, and turns elapsed.</summary>
        public static float3 OrbitOffset(float a, float e, float turns, quaternion plane)
        {
            var mean = 2f * math.PI * math.frac(turns);              // mean anomaly
            var ecc = mean;                                          // Kepler: E - e sin E = M (Newton)
            for (var k = 0; k < 5; k++)
                ecc -= (ecc - e * math.sin(ecc) - mean) / (1f - e * math.cos(ecc));
            var local = new float3(a * (math.cos(ecc) - e), 0f, a * math.sqrt(1f - e * e) * math.sin(ecc));
            return math.mul(plane, local);
        }
    }
}
