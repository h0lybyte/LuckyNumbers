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

        /// <summary>First-person arms: lists clips, renders 4 poses from the rig's own camera to Logs/preview-arms.png.</summary>
        [MenuItem("LuckyNumbers/Preview/Render Arms")]
        public static void RenderArms()
        {
            const string path = "Assets/_Project/Art/Characters/Arms/arms_rig.fbx";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);   // pick up ArtImport rule changes
            var clips = new System.Collections.Generic.List<AnimationClip>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o is AnimationClip c && !c.name.StartsWith("__preview")) clips.Add(c);
            Debug.Log($"[ShaderPreview] arms clips ({clips.Count}): {string.Join(", ", clips.ConvertAll(c => $"{c.name} {c.length:0.##}s"))}");

            var poses = new[] { "relax", "guard_idle", "finger_gun_idle", "grab.R" };
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/_Project/Art/Characters/Arms/arms_gloves_01.png");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);   // destroys loose objects: create after
            var sheet = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            Shader.SetGlobalVector("_LnSunPosition", new Vector4(40f, 60f, -20f, 1f));
            Shader.SetGlobalVector("_LnSunColor", Color.white);
            var arms = (GameObject)Object.Instantiate(model);
            var mat = new Material(Find("LuckyNumbers/Ship")) { mainTexture = tex };
            foreach (var r in arms.GetComponentsInChildren<Renderer>()) r.sharedMaterial = mat;
            var cam = arms.GetComponentInChildren<Camera>();
            var own = cam != null;
            if (!own) cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.16f);
            cam.nearClipPlane = 0.01f;
            // How much each clip actually moves: max bone rotation change between its start and middle.
            foreach (var c in clips)
            {
                var bones = arms.GetComponentsInChildren<Transform>();
                c.SampleAnimation(arms, 0f);
                var start = System.Array.ConvertAll(bones, b => b.localRotation);
                c.SampleAnimation(arms, c.length * 0.5f);
                var most = 0f; var which = "";
                for (var i = 0; i < bones.Length; i++)
                {
                    var d = Quaternion.Angle(start[i], bones[i].localRotation);
                    if (d > most) { most = d; which = bones[i].name; }
                }
                Debug.Log($"[ShaderPreview] clip {c.name}: {c.length:0.##}s, max bone change {most:0.#} deg ({which}), loop {c.isLooping}");
            }
            var rt = new RenderTexture(Width / 2, Height / 2, 24, RenderTextureFormat.ARGB32);
            for (var k = 0; k < poses.Length; k++)
            {
                var clip = clips.Find(c => c.name == poses[k] || c.name.EndsWith("|" + poses[k]));
                if (clip != null) clip.SampleAnimation(arms, clip.length * 0.3f);
                if (!own)
                {
                    var b = new Bounds();
                    var first = true;
                    foreach (var r in arms.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        r.updateWhenOffscreen = true;
                        if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
                    }
                    var hand = FindDeep(arms.transform, "hand.R");
                    Debug.Log($"[ShaderPreview] arms {poses[k]}: bounds {b.center} size {b.size}, hand.R {(hand ? hand.position.ToString() : "?")}");
                    // First-person eye: the rig's origin column at head height, looking +Z slightly down.
                    cam.transform.SetPositionAndRotation(new Vector3(0f, 1.66f, -0.02f), Quaternion.Euler(12f, 0f, 0f));
                    cam.fieldOfView = 62f;
                }
                Debug.Log($"[ShaderPreview] arms camera {(own ? "from rig" : "framed")} at {cam.transform.position} rot {cam.transform.eulerAngles} fov {cam.fieldOfView}");
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
                else { cam.targetTexture = rt; cam.Render(); }
                RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, Width / 2, Height / 2), (k % 2) * Width / 2, (1 - k / 2) * Height / 2);
                RenderTexture.active = null;
            }
            sheet.Apply();
            var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-arms.png"));
            File.WriteAllBytes(outPath, sheet.EncodeToPNG());
            Debug.Log($"[ShaderPreview] wrote {outPath}");
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t) { var f = FindDeep(c, name); if (f != null) return f; }
            return null;
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
