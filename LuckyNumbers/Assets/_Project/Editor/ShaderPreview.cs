using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Renders the 3D space shaders (starfield skybox + a planet sphere) to Logs/preview-space.png at
    /// 1280x720, so shaders can be checked visually from the command line (tools/ln.py preview).
    /// </summary>
    public static class ShaderPreview
    {
        const int Width = 1280, Height = 720;

        [MenuItem("LuckyNumbers/Preview/Render Space Background")]
        public static void RenderSpace()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = new Material(Find("LuckyNumbers/Starfield"));
            Shader.SetGlobalVector("_LnSunPosition", new Vector4(40f, 14f, -26f, 1f));
            Shader.SetGlobalVector("_LnSunColor", Color.white);

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.fieldOfView = 45f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.SetPositionAndRotation(new Vector3(0, 0.5f, -6), Quaternion.Euler(5, 0, 0));

            var planet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            planet.transform.position = new Vector3(1.4f, 0, 0);
            planet.transform.localScale = Vector3.one * 2.4f;
            planet.GetComponent<MeshRenderer>().sharedMaterial = new Material(Find("LuckyNumbers/Planet3D"));

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

        /// <summary>The fighter with its winning skin, three angles, to Logs/preview-ship.png.</summary>
        [MenuItem("LuckyNumbers/Preview/Render Ship")]
        public static void RenderShip()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = new Material(Find("LuckyNumbers/Starfield"));
            // LN_SHIP_BACKLIT=1: sun behind the ships (night-side check).
            var backlit = System.Environment.GetEnvironmentVariable("LN_SHIP_BACKLIT") == "1";
            Shader.SetGlobalVector("_LnSunPosition", backlit ? new Vector4(-10f, 20f, 80f, 1f) : new Vector4(40f, 30f, -26f, 1f));
            Shader.SetGlobalVector("_LnSunColor", new Color(1f, 0.95f, 0.85f));
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Models/Fighter/fighter1.fbx")
                        ?? throw new System.Exception("fighter1.fbx not imported");
            var mat = new Material(Find("LuckyNumbers/Ship"))
            {
                mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    $"Assets/_Project/Art/Models/Fighter/Skins/{System.Environment.GetEnvironmentVariable("LN_SHIP_SKIN") ?? "idolknight"}.jpg"),
            };
            var bounds = new Bounds();
            for (var k = 0; k < 3; k++)
            {
                var ship = (GameObject)Object.Instantiate(model);
                foreach (var r in ship.GetComponentsInChildren<Renderer>()) { r.sharedMaterial = mat; bounds = r.bounds; }
                var size = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                ship.transform.localScale *= 1.6f / Mathf.Max(size, 1e-4f);
                ship.transform.SetPositionAndRotation(new Vector3(-2.2f + 2.2f * k, 0, 0), Quaternion.Euler(20 + 15 * k, 40 + 110 * k, 0));
            }
            Debug.Log($"[ShaderPreview] fighter bounds {bounds.size}");
            if (System.Environment.GetEnvironmentVariable("LN_SHIP_AXES") == "1")
            {
                // Orientation check: one ship at identity seen from above; red = +X, blue = +Z markers.
                foreach (var go in Object.FindObjectsByType<MeshRenderer>()) Object.DestroyImmediate(go.gameObject);
                var ship = (GameObject)Object.Instantiate(model);
                foreach (var r in ship.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
                ship.transform.localScale *= 4f;
                ship.transform.position = Vector3.zero;
                foreach (var (dir, c) in new[] { (Vector3.right, Color.red), (Vector3.forward, Color.blue) })
                {
                    var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    m.transform.position = dir * 1.4f;
                    m.transform.localScale = Vector3.one * 0.2f;
                    m.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = c };
                }
                var top = new GameObject("TopCamera").AddComponent<Camera>();
                top.fieldOfView = 45f;
                top.clearFlags = CameraClearFlags.SolidColor;
                top.backgroundColor = new Color(0.1f, 0.1f, 0.12f);
                top.transform.SetPositionAndRotation(new Vector3(0, 4.5f, 0), Quaternion.Euler(90, 0, 0));
                Save(top, "preview-ship-axes.png");
                return;
            }
            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.fieldOfView = 45f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.SetPositionAndRotation(new Vector3(0, 0.3f, -4.5f), Quaternion.Euler(3, 0, 0));
            Save(cam, "preview-ship.png");
        }

        static void Save(Camera cam, string file)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
            else { cam.targetTexture = rt; cam.Render(); }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", file));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[ShaderPreview] wrote {path}");
        }

        static Shader Find(string name) => Shader.Find(name) ?? throw new System.Exception($"Shader not found: {name}");
    }
}
