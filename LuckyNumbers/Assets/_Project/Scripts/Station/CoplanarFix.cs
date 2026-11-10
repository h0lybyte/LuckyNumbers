using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Some kit models are skinned with flush panels: a panel face lies in the same plane as the face under it, facing
    /// the same way (the cryo pod's back, sides, top, bottom and lid). The two z-fight, a shimmering patch that moves as
    /// you do. This finds those overlapping same-facing coplanar triangles in a mesh and lifts the smaller of each pair
    /// a few millimetres out along its normal (on its own vertices, so neighbours keep their shape), the way the corner
    /// posts were made proud of the walls (b6c8d79). Run once per kit mesh when the station collects its parts; the
    /// FBX files stay untouched.
    /// </summary>
    public static class CoplanarFix
    {
        const float Lift = 0.003f;          // metres
        const float PlaneTolerance = 0.0015f;
        const float NormalTolerance = 0.999f;

        /// <summary>A copy of `mesh` with flush overlapping triangles separated, or `mesh` itself if it has none.</summary>
        public static Mesh Separate(Mesh mesh, out int lifted)
        {
            lifted = 0;
            if (mesh == null || !mesh.isReadable) return mesh;
            var vertices = mesh.vertices;
            var count = mesh.subMeshCount;
            var subTriangles = new List<int[]>(count);
            for (var s = 0; s < count; s++) subTriangles.Add(mesh.GetTriangles(s));

            // Every triangle: plane, area, which submesh and index.
            var tris = new List<Tri>();
            for (var s = 0; s < count; s++)
            {
                var t = subTriangles[s];
                for (var i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 a = vertices[t[i]], b = vertices[t[i + 1]], c = vertices[t[i + 2]];
                    var cross = Vector3.Cross(b - a, c - a);
                    var area = cross.magnitude * 0.5f;
                    if (area < 1e-6f) continue;
                    var n = cross / (area * 2f);
                    tris.Add(new Tri { Sub = s, Index = i, A = a, B = b, C = c, Normal = n, D = Vector3.Dot(n, a), Area = area });
                }
            }

            // Pairs in one plane, facing the same way, overlapping. In each, lift the panel, not the face under it:
            // the face under several panels overlaps more triangles, so lift the one with fewer overlaps (ties: the
            // smaller one).
            var pairs = new List<(int, int)>();
            var overlaps = new int[tris.Count];
            for (var i = 0; i < tris.Count; i++)
            for (var j = i + 1; j < tris.Count; j++)
            {
                var p = tris[i];
                var q = tris[j];
                if (Vector3.Dot(p.Normal, q.Normal) < NormalTolerance || Mathf.Abs(p.D - q.D) > PlaneTolerance) continue;
                if (!Overlap(p, q)) continue;
                pairs.Add((i, j));
                overlaps[i]++;
                overlaps[j]++;
            }
            var lift = new HashSet<(int sub, int index)>();
            foreach (var (i, j) in pairs)
            {
                var top = overlaps[i] != overlaps[j] ? (overlaps[i] < overlaps[j] ? i : j) : (tris[i].Area <= tris[j].Area ? i : j);
                lift.Add((tris[top].Sub, tris[top].Index));
            }
            if (lift.Count == 0) return mesh;

            // Copy the mesh; each lifted triangle gets three new vertices pushed out along its normal.
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name + " (separated)";
            var positions = new List<Vector3>(vertices);
            var normals = new List<Vector3>(mesh.normals);
            var uvs = new List<Vector2>(mesh.uv);
            var colors = new List<Color>(mesh.colors);
            var tangents = new List<Vector4>(mesh.tangents);
            foreach (var t in tris)
            {
                if (!lift.Contains((t.Sub, t.Index))) continue;
                var triangles = subTriangles[t.Sub];
                for (var k = 0; k < 3; k++)
                {
                    var old = triangles[t.Index + k];
                    positions.Add(vertices[old] + t.Normal * Lift);
                    if (normals.Count > old) normals.Add(normals[old]);
                    if (uvs.Count > old) uvs.Add(uvs[old]);
                    if (colors.Count > old) colors.Add(colors[old]);
                    if (tangents.Count > old) tangents.Add(tangents[old]);
                    triangles[t.Index + k] = positions.Count - 1;
                }
                lifted++;
            }
            copy.indexFormat = positions.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : copy.indexFormat;
            copy.SetVertices(positions);
            if (normals.Count == positions.Count) copy.SetNormals(normals);
            if (uvs.Count == positions.Count) copy.SetUVs(0, uvs);
            if (colors.Count == positions.Count) copy.SetColors(colors);
            if (tangents.Count == positions.Count) copy.SetTangents(tangents);
            for (var s = 0; s < count; s++) copy.SetTriangles(subTriangles[s], s);
            copy.RecalculateBounds();
            return copy;
        }

        struct Tri
        {
            public int Sub, Index;
            public Vector3 A, B, C, Normal;
            public float D, Area;
        }

        /// <summary>Do two coplanar triangles share area (not just an edge or a corner)?</summary>
        static bool Overlap(Tri p, Tri q)
        {
            // Into the plane's 2D frame, then separating axes on both triangles' edges.
            var u = Vector3.Cross(p.Normal, Mathf.Abs(p.Normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(p.Normal, u);
            Vector2 P(Vector3 x) => new(Vector3.Dot(x, u), Vector3.Dot(x, v));
            var a = new[] { P(p.A), P(p.B), P(p.C) };
            var b = new[] { P(q.A), P(q.B), P(q.C) };
            return !Separated(a, b) && !Separated(b, a);
        }

        static bool Separated(Vector2[] a, Vector2[] b)
        {
            const float margin = 0.0005f;   // touching edges don't count
            for (var i = 0; i < 3; i++)
            {
                var e = a[(i + 1) % 3] - a[i];
                var axis = new Vector2(-e.y, e.x);
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                foreach (var x in a) { var d = Vector2.Dot(x, axis); minA = Mathf.Min(minA, d); maxA = Mathf.Max(maxA, d); }
                foreach (var x in b) { var d = Vector2.Dot(x, axis); minB = Mathf.Min(minB, d); maxB = Mathf.Max(maxB, d); }
                var scale = axis.magnitude;
                if (maxA <= minB + margin * scale || maxB <= minA + margin * scale) return true;
            }
            return false;
        }
    }
}
