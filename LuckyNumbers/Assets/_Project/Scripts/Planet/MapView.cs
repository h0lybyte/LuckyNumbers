using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// The planet map: a smooth hex globe (HexSphere cells inset over a dark grout sphere) on its own layer, seen
    /// by its own camera, with the system's star and the planet's moons around it, a crash-site marker, a pin (you)
    /// and your landed ship.
    /// Globe radius 1. Shown in place of the first-person view (the sky is the scene's skybox).
    /// </summary>
    [View]
    public sealed class MapView : MonoBehaviour
    {
        [SerializeField] Camera mapCamera;
        [SerializeField] Transform globe;
        [SerializeField] MeshFilter globeMesh;
        [SerializeField] MeshRenderer globeRenderer;
        [SerializeField] Transform crash, pin;
        [SerializeField] MeshRenderer star;          // the system's star (StarLook material set in code)
        [SerializeField] MeshRenderer moonTemplate;  // a Planet3D sphere, copied per moon

        readonly List<MeshRenderer> _moons = new();

        public bool Built { get; private set; }
        public Transform Globe => globe;
        public Material Material => globeRenderer.sharedMaterial;

        public void Show(bool visible)
        {
            mapCamera.gameObject.SetActive(visible);
            gameObject.SetActive(visible);
        }

        /// <summary>Camera looking at the globe's centre along `rotation`'s forward, `radii` from it.</summary>
        public void Look(Quaternion rotation, float radii) =>
            mapCamera.transform.SetPositionAndRotation(globe.position - rotation * Vector3.forward * radii, rotation);

        /// <summary>Markers, as directions in globe space, just above the surface.</summary>
        public void Place(Vector3 crashDirection, Vector3 pinDirection, Color crashColor, Color pinColor)
        {
            crash.localPosition = crashDirection.normalized * 1.005f;
            crash.localRotation = Quaternion.FromToRotation(Vector3.up, crashDirection);
            pin.localPosition = pinDirection.normalized * 1.05f;   // over the crash marker's top: you always show
            Tint(crash, crashColor);
            Tint(pin, pinColor);
        }

        /// <summary>The star: a sphere in world direction `direction`, `distance` radii away, `size` radii across.</summary>
        public void Star(Material look, Vector3 direction, float distance, float size)
        {
            if (look != null) star.sharedMaterial = look;
            star.transform.position = globe.position + direction.normalized * distance;
            star.transform.localScale = Vector3.one * size;
        }

        /// <summary>A moon at a world offset from the globe (radii), `size` radii across, with its own look.</summary>
        public Material Moon(Vector3 offset, float size)
        {
            var moon = Instantiate(moonTemplate, transform);
            moon.gameObject.SetActive(true);
            moon.transform.position = globe.position + offset;
            moon.transform.localScale = Vector3.one * size;
            _moons.Add(moon);
            return moon.material;
        }

        Transform _ship;

        /// <summary>Your landed ship (a flat disc from the pin, on the ground under it), as a direction in globe space; null hides it.</summary>
        public void PlaceShip(Vector3? direction, Color color)
        {
            if (_ship == null)
            {
                _ship = Instantiate(pin, pin.parent);
                _ship.name = "Ship marker";
                _ship.localScale = Vector3.Scale(pin.localScale, new Vector3(1.8f, 0.3f, 1.8f));
            }
            _ship.gameObject.SetActive(direction.HasValue);
            if (!direction.HasValue) return;
            _ship.localPosition = direction.Value.normalized * 1.004f;
            _ship.localRotation = Quaternion.FromToRotation(Vector3.up, direction.Value);
            Tint(_ship, color);
        }

        /// <summary>The crash marker on or off (no wreck on a Clear job).</summary>
        public void ShowCrash(bool on) => crash.gameObject.SetActive(on);

        static void Tint(Transform t, Color c)
        {
            if (t.TryGetComponent<MeshRenderer>(out var r)) r.material.color = c;
        }

        /// <summary>Each cell a flat tile on the sphere, inset by `gap` over the same cell in grout, so seams read as lines.</summary>
        public void Build(List<HexSphere.Cell> cells, Color[] colors, float gap, Color grout)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tints = new List<Color>();
            var indices = new List<int>();
            void Polygon(Vector3 centre, Vector3[] corners, float radius, Color color)
            {
                var c = vertices.Count;
                vertices.Add(centre * radius); normals.Add(centre); tints.Add(color);
                foreach (var corner in corners) { vertices.Add(corner * radius); normals.Add(corner); tints.Add(color); }
                for (var k = 0; k < corners.Length; k++) { indices.Add(c); indices.Add(c + 1 + k); indices.Add(c + 1 + (k + 1) % corners.Length); }
            }
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                var corners = (Vector3[])cell.Corners.Clone();
                // Unity front faces: cross(b - a, c - a) points toward the viewer, i.e. outward here.
                if (Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), cell.Centre) < 0f) System.Array.Reverse(corners);
                Polygon(cell.Centre, corners, 0.999f, grout);
                var inset = new Vector3[corners.Length];
                for (var k = 0; k < inset.Length; k++) inset[k] = Vector3.Lerp(corners[k], cell.Centre, gap).normalized;
                Polygon(cell.Centre, inset, 1f, colors[i]);
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
