using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The planet map: a hex globe (HexSphere cells as raised prisms with gaps) on its own layer, seen by its
    /// own camera, with a crash-site marker and a pin for you. Shown in place of the first-person view.
    /// </summary>
    [View]
    public sealed class MapView : MonoBehaviour
    {
        [SerializeField] Camera mapCamera;
        [SerializeField] Transform globe;
        [SerializeField] MeshFilter globeMesh;
        [SerializeField] MeshRenderer globeRenderer;
        [SerializeField] Transform crash, pin;

        public bool Built { get; private set; }
        public Transform Globe => globe;
        public Material Material => globeRenderer.sharedMaterial;

        public void Show(bool visible, Color background)
        {
            mapCamera.backgroundColor = background;
            mapCamera.gameObject.SetActive(visible);
            globe.gameObject.SetActive(visible);
        }

        /// <summary>Camera distance from the globe's centre, in globe radii (the globe has radius 1).</summary>
        public void Distance(float radii) => mapCamera.transform.position = globe.position - Vector3.forward * radii;

        /// <summary>Markers, as directions from the centre (globe space), sitting `lift` above the surface.</summary>
        public void Place(Vector3 crashDirection, Vector3 pinDirection, float lift, Color crashColor, Color pinColor)
        {
            crash.localPosition = crashDirection.normalized * (1f + lift);
            crash.localRotation = Quaternion.FromToRotation(Vector3.up, crashDirection);
            pin.localPosition = pinDirection.normalized * (1f + lift * 1.6f);
            pin.localRotation = Quaternion.FromToRotation(Vector3.up, pinDirection);
            Tint(crash, crashColor);
            Tint(pin, pinColor);
        }

        static void Tint(Transform t, Color c)
        {
            if (t.TryGetComponent<MeshRenderer>(out var r)) r.material.color = c;
        }

        /// <summary>
        /// Build the globe mesh: each cell a prism (top inset by `gap`, raised to 1 + height) with walls down to
        /// the base, so cells read as tiles. Vertex colours per cell.
        /// </summary>
        public void Build(List<HexSphere.Cell> cells, float[] heights, Color[] colors, float gap)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tints = new List<Color>();
            var indices = new List<int>();
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                var top = 1f + heights[c];
                var count = cell.Corners.Length;
                var ring = new Vector3[count];
                for (var k = 0; k < count; k++) ring[k] = Vector3.Lerp(cell.Corners[k], cell.Centre, gap).normalized;
                // Unity front faces: cross(b - a, c - a) points toward the viewer, i.e. outward here.
                if (Vector3.Dot(Vector3.Cross(ring[1] - ring[0], ring[2] - ring[0]), cell.Centre) < 0f) System.Array.Reverse(ring);

                var centre = vertices.Count;
                vertices.Add(cell.Centre * top); normals.Add(cell.Centre); tints.Add(colors[c]);
                for (var k = 0; k < count; k++) { vertices.Add(ring[k] * top); normals.Add(cell.Centre); tints.Add(colors[c]); }
                for (var k = 0; k < count; k++) { indices.Add(centre); indices.Add(centre + 1 + k); indices.Add(centre + 1 + (k + 1) % count); }

                // Walls, a little darker, down to just under the lowest cell.
                var wall = colors[c] * 0.6f; wall.a = 1f;
                for (var k = 0; k < count; k++)
                {
                    Vector3 a = ring[k], b = ring[(k + 1) % count];
                    var outward = Vector3.Cross(b - a, cell.Centre).normalized;
                    if (Vector3.Dot(outward, (a + b) * 0.5f - cell.Centre) < 0f) outward = -outward;
                    var w = vertices.Count;
                    vertices.Add(a * top); vertices.Add(b * top); vertices.Add(b * 0.98f); vertices.Add(a * 0.98f);
                    for (var q = 0; q < 4; q++) { normals.Add(outward); tints.Add(wall); }
                    var flip = Vector3.Dot(Vector3.Cross(vertices[w + 1] - vertices[w], vertices[w + 2] - vertices[w]), outward) < 0f;
                    if (flip) { indices.Add(w); indices.Add(w + 2); indices.Add(w + 1); indices.Add(w); indices.Add(w + 3); indices.Add(w + 2); }
                    else { indices.Add(w); indices.Add(w + 1); indices.Add(w + 2); indices.Add(w); indices.Add(w + 2); indices.Add(w + 3); }
                }
            }
            var mesh = new Mesh { name = "Hex globe", indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(tints);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            globeMesh.sharedMesh = mesh;
            Built = true;
        }
    }
}
