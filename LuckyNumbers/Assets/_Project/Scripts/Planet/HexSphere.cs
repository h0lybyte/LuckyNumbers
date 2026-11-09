using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A sphere tiled with hexagons and exactly 12 pentagons (a Goldberg polyhedron). Euler (V - E + F = 2)
    /// rules out all-hexagons; the pentagons sit where the 12 corners of an icosahedron were.
    /// Built as the dual of a geodesic sphere: each triangle of the icosahedron is split into frequency^2
    /// triangles pushed onto the sphere, and every vertex becomes a cell whose corners are the centres of
    /// the triangles around it. 10 * frequency^2 + 2 cells.
    /// </summary>
    public static class HexSphere
    {
        public sealed class Cell
        {
            public Vector3 Centre;        // unit vector
            public Vector3[] Corners;     // unit vectors, counter-clockwise seen from outside
            public bool Pentagon => Corners.Length == 5;
        }

        public static List<Cell> Build(int frequency)
        {
            var n = Mathf.Max(1, frequency);
            var t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var ico = new[]
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            int[] faces =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            // Geodesic vertices (shared along edges: deduplicated by rounded position) and triangles.
            var points = new List<Vector3>();
            var index = new Dictionary<Vector3Int, int>();
            var triangles = new List<int>();
            int Vertex(Vector3 p)
            {
                p.Normalize();
                var key = Vector3Int.RoundToInt(p * 100000f);
                if (index.TryGetValue(key, out var i)) return i;
                index[key] = points.Count;
                points.Add(p);
                return points.Count - 1;
            }
            var grid = new int[n + 1, n + 1];
            for (var f = 0; f < faces.Length; f += 3)
            {
                Vector3 a = ico[faces[f]], b = ico[faces[f + 1]], c = ico[faces[f + 2]];
                for (var i = 0; i <= n; i++)
                    for (var j = 0; j <= n - i; j++)
                        grid[i, j] = Vertex(a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n));
                for (var i = 0; i < n; i++)
                    for (var j = 0; j < n - i; j++)
                    {
                        triangles.Add(grid[i, j]); triangles.Add(grid[i + 1, j]); triangles.Add(grid[i, j + 1]);
                        if (i + j < n - 1) { triangles.Add(grid[i + 1, j]); triangles.Add(grid[i + 1, j + 1]); triangles.Add(grid[i, j + 1]); }
                    }
            }

            // Dual: each vertex's cell has a corner at the centre of every triangle touching it.
            var around = new List<Vector3>[points.Count];
            for (var i = 0; i < around.Length; i++) around[i] = new List<Vector3>(6);
            for (var k = 0; k < triangles.Count; k += 3)
            {
                var centre = (points[triangles[k]] + points[triangles[k + 1]] + points[triangles[k + 2]]).normalized;
                around[triangles[k]].Add(centre);
                around[triangles[k + 1]].Add(centre);
                around[triangles[k + 2]].Add(centre);
            }
            var cells = new List<Cell>(points.Count);
            for (var v = 0; v < points.Count; v++)
            {
                var normal = points[v];
                var tangent = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var bitangent = Vector3.Cross(normal, tangent);
                var corners = around[v];
                corners.Sort((p, q) => Angle(p).CompareTo(Angle(q)));
                float Angle(Vector3 p) => Mathf.Atan2(Vector3.Dot(p, bitangent), Vector3.Dot(p, tangent));
                cells.Add(new Cell { Centre = normal, Corners = corners.ToArray() });
            }
            return cells;
        }
    }
}
