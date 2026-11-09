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
    public sealed class SpaceWorld
    {
        public readonly string[] Ids;
        public readonly float3[] BasePosition;
        public readonly float[] Scale;
        public readonly int[] OrbitParent;     // -1 = static
        public readonly float[] OrbitRadius;
        public readonly float[] OrbitPeriod;
        public readonly float[] OrbitPhase;
        public readonly PlanetLook[] Look;
        public readonly float3[] Position;     // output of OrbitSystem

        public int Count => Ids.Length;

        public SpaceWorld(IReadOnlyList<CelestialBody> bodies)
        {
            var n = bodies.Count;
            Ids = new string[n];
            BasePosition = new float3[n];
            Scale = new float[n];
            OrbitParent = new int[n];
            OrbitRadius = new float[n];
            OrbitPeriod = new float[n];
            OrbitPhase = new float[n];
            Look = new PlanetLook[n];
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
                Look[i] = b.Look;
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
                }
            }
        }

        public int IndexOf(string id) => Array.IndexOf(Ids, id);
    }

    /// <summary>Positions every body for time t (seconds); orbits lie in the XZ plane. Pure function.</summary>
    public static class OrbitSystem
    {
        public static void Update(SpaceWorld w, float t)
        {
            for (var i = 0; i < w.Count; i++)
            {
                var p = w.OrbitParent[i];
                if (p < 0)
                {
                    w.Position[i] = w.BasePosition[i];
                    continue;
                }
                var angle = 2f * math.PI * (t / w.OrbitPeriod[i] + w.OrbitPhase[i]);
                w.Position[i] = w.Position[p] + w.BasePosition[i]
                              + w.OrbitRadius[i] * new float3(math.cos(angle), 0f, math.sin(angle));
            }
        }
    }
}
