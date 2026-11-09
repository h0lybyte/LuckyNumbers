using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Renders the space background (starfield + planet) to Logs/preview-space.png at 1280x720,
    /// so shaders can be checked visually from the command line (tools/ln.py preview).
    /// </summary>
    public static class ShaderPreview
    {
        const int Width = 1280, Height = 720;

        [MenuItem("LuckyNumbers/Preview/Render Space Background")]
        public static void RenderSpace()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.transform.position = new Vector3(0, 0, -10);

            Quad("Starfield", "LuckyNumbers/Starfield", new Vector3(0, 0, 5), new Vector3(4f, 2.25f, 1f));
            Quad("Planet", "LuckyNumbers/Planet2D", new Vector3(0.45f, -0.1f, 0), new Vector3(1.2f, 1.2f, 1f));

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
            {
                cam.targetTexture = rt;
                cam.Render();
            }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-space.png"));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[ShaderPreview] wrote {path}");
        }

        static void Quad(string name, string shader, Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            var s = Shader.Find(shader);
            if (s == null) throw new System.Exception($"Shader not found: {shader}");
            go.GetComponent<MeshRenderer>().sharedMaterial = new Material(s);
        }
    }
}
