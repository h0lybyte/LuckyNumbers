using System.Collections.Generic;
using System.IO;
using LuckyNumbers.Model;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// HoldKit check image (Logs/preview-holdkit.png): a corridor of the "hall" palette receding into the haze (floor,
    /// walls, ceiling), and a row of wall panels, one per array slice. Needs graphics: tools/ln.py hold-textures --preview.
    /// </summary>
    public static class HoldKitPreview
    {
        const int Width = 1280, Height = 720;

        [UnityEditor.MenuItem("LuckyNumbers/Holds/Render HoldKit Preview")]
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(HoldTextureBuilder.MaterialPath);
            if (mat == null) throw new System.InvalidOperationException("[HoldKitPreview] build the array first");
            var tex = GameContent.Hold.Textures;
            int Slice(string layer) => tex.Layers.IndexOf(layer);
            var hall = tex.Palettes[0];

            var b = new Builder();
            // Corridor along +z: 2 m cells, 3 m high, 14 cells long; uv in metres / 2 (one tile per cell).
            const float w = 4f, h = 3f, len = 28f;
            float sFloor = Slice(hall.Floor), sWall = Slice(hall.Wall), sCeil = Slice(hall.Ceiling);
            b.Quad(new Vector3(-w / 2, 0, 0), new Vector3(w, 0, 0), new Vector3(0, 0, len), Vector3.up, sFloor, 0.5f, 0.9f);
            b.Quad(new Vector3(-w / 2, h, len), new Vector3(w, 0, 0), new Vector3(0, 0, -len), Vector3.down, sCeil, 0.5f, 0.5f);
            b.Quad(new Vector3(-w / 2, 0, len), new Vector3(0, 0, -len), new Vector3(0, h, 0), Vector3.right, sWall, 0.5f, 0.7f);
            b.Quad(new Vector3(w / 2, 0, 0), new Vector3(0, 0, len), new Vector3(0, h, 0), Vector3.left, sWall, 0.5f, 0.7f);

            // Panels: every slice, 1.2 m squares in rows above the corridor mouth.
            int n = tex.Layers.Count, cols = 11;
            for (int i = 0; i < n; i++)
            {
                float x = -7.6f + (i % cols) * 1.4f, y = 4.6f - (i / cols) * 1.3f;
                b.Quad(new Vector3(x, y, -1.5f), new Vector3(1.2f, 0, 0), new Vector3(0, 1.2f, 0), Vector3.back, i, 1f / 1.2f, 1f);
            }

            var go = new GameObject("Hold");
            go.AddComponent<MeshFilter>().sharedMesh = b.Mesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = mat.GetColor("_HazeColor");
            cam.transform.SetPositionAndRotation(new Vector3(0, 2.2f, -9f), Quaternion.Euler(-8, 0, 0));

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
            else { cam.targetTexture = rt; cam.Render(); }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var shot = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            shot.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-holdkit.png"));
            File.WriteAllBytes(path, shot.EncodeToPNG());
            Debug.Log($"[HoldKitPreview] wrote {path}");
        }

        sealed class Builder
        {
            readonly List<Vector3> pos = new(), nrm = new(), uv = new();
            readonly List<Color> col = new();
            readonly List<int> tri = new();

            /// <summary>Quad at o spanning a (u) and b (v); uv = metres * scale; colour = grey baked light.</summary>
            public void Quad(Vector3 o, Vector3 a, Vector3 b, Vector3 n, float slice, float scale, float light)
            {
                int k = pos.Count;
                float ua = a.magnitude * scale, vb = b.magnitude * scale;
                pos.Add(o); pos.Add(o + a); pos.Add(o + a + b); pos.Add(o + b);
                uv.Add(new Vector3(0, 0, slice)); uv.Add(new Vector3(ua, 0, slice));
                uv.Add(new Vector3(ua, vb, slice)); uv.Add(new Vector3(0, vb, slice));
                for (int i = 0; i < 4; i++) { nrm.Add(n); col.Add(new Color(light, light, light * 0.9f, 1)); }
                // Wind so the face points along n (Unity: clockwise = front).
                bool flip = Vector3.Dot(Vector3.Cross(a, b), n) < 0;
                if (flip) tri.AddRange(new[] { k, k + 2, k + 1, k, k + 3, k + 2 });
                else tri.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }

            public Mesh Mesh()
            {
                var m = new Mesh();
                m.SetVertices(pos);
                m.SetNormals(nrm);
                m.SetColors(col);
                m.SetUVs(0, uv);
                m.SetTriangles(tri, 0);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
