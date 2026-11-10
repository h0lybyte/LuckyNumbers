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

        /// <summary>
        /// The station (content/station.txtpb) from both PSX kits: three eye-height views and a top-down plan
        /// (ceilings face down, so from above they're culled and the rooms show) to Logs/preview-station.png.
        /// </summary>
        [MenuItem("LuckyNumbers/Preview/Render Station")]
        public static void RenderStation()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var view = new GameObject("Station").AddComponent<LuckyNumbers.Station.StationView>();
            view.Init(KitMaterial("StationKit"), KitMaterial("StarshipKit"), new Material(Find("LuckyNumbers/StationGlass")),
                      KitPieces("StationKit", "StarshipKit"));

            // Outside: the seeded sky and a planet the station orbits, north of the hangar (its windows face it),
            // lit by a sun up and to the east. The windows should show these, not the kits' painted stars.
            RenderSettings.skybox = new Material(Find("LuckyNumbers/Starfield"));
            Shader.SetGlobalVector("_LnSunPosition", new Vector4(400f, 160f, 300f, 1f));
            Shader.SetGlobalColor("_LnSunColor", new Color(1f, 0.95f, 0.88f));
            var world = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(world.GetComponent<Collider>());
            world.name = "Planet";
            world.transform.position = new Vector3(4f, -40f, 140f);
            world.transform.localScale = Vector3.one * 150f;
            world.GetComponent<MeshRenderer>().sharedMaterial = new Material(Find("LuckyNumbers/Planet3D"));
            var config = LuckyNumbers.Model.GameContent.Station;
            view.Build(config);
            var plan = view.Plan;

            // Plan coordinates (column, row from the top, as in the .txtpb) -> eye-height point in that cell.
            Vector3 At(float col, float row, float y = 1.6f) =>
                new Vector3((col + 0.5f) * 2f, y, (plan.Depth - 1 - row + 0.5f) * 2f);
            Camera Eye(Vector3 from, Vector3 to)
            {
                var cam = new GameObject("Eye").AddComponent<Camera>();
                cam.fieldOfView = 70f;
                cam.nearClipPlane = 0.05f;
                cam.clearFlags = CameraClearFlags.Skybox;   // what's outside shows through the windows
                cam.farClipPlane = 1000f;
                cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(to - from));
                return cam;
            }

            var top = new GameObject("Top").AddComponent<Camera>();
            top.orthographic = true;
            top.clearFlags = CameraClearFlags.SolidColor;
            top.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            var size = new Vector2(plan.Width, plan.Depth) * 2f;
            top.orthographicSize = Mathf.Max(size.y, size.x * Height / Width) * 0.55f;
            top.transform.SetPositionAndRotation(new Vector3(size.x / 2f, 30f, size.y / 2f), Quaternion.Euler(90, 0, 0));
            top.farClipPlane = 60f;

            SaveGrid(new[]
            {
                Eye(At(5.3f, 5.3f), At(2f, 1f, 1.9f)),         // hangar, towards its windows
                Eye(At(8.6f, 3f), At(15f, 3f, 1.5f)),           // corridor, east
                Eye(At(14f, 8.6f), At(17.5f, 8f, 1.7f)),        // viewing room, towards the windows
                top,
            }, "preview-station.png");
            // Joins: both doorways, a corner post, the corridor's turn.
            SaveGrid(new[]
            {
                Eye(At(10f, 3f), At(6.5f, 3f, 1.4f)),           // corridor -> hangar doorway
                Eye(At(3f, 4.2f), At(3f, 7.5f, 1.2f)),          // hangar -> store doorway
                Eye(At(5.2f, 4.8f), At(6.6f, 1f, 1.5f)),        // hangar's north-east corner
                Eye(At(12.5f, 3f), At(15f, 5.5f, 1.4f)),        // corridor turning south
            }, "preview-station-joins.png");
            // Corridor joins up close: portholes, the turn's inner (convex) corner, the end at a doorway.
            SaveGrid(new[]
            {
                Eye(At(11.5f, 3.4f, 1.5f), At(11.5f, 2f, 2.2f)),       // portholes (north wall)
                Eye(At(15.2f, 4.6f, 2.2f), At(14.2f, 2.8f, 2.7f)),     // turn: inner corner, looking up at it
                Eye(At(14.6f, 3.2f, 1.7f), At(15.6f, 2.4f, 2.6f)),     // turn: outer corner (the leans cross)
                Eye(At(7.4f, 3.3f, 2f), At(8.6f, 2.4f, 2.6f)),         // from the doorway into the corridor: lean ends
            }, "preview-station-corridor.png");

            // Windows up close: they should open onto the sky and the planet (north), not painted stars.
            SaveGrid(new[]
            {
                Eye(At(3f, 1.15f, 1.55f), At(3f, -6f, 1.2f)),       // hangar porthole, nose to the glass, planet ahead
                Eye(At(4f, 1.6f, 1.5f), At(4.6f, -6f, 1.0f)),       // a step back, two portholes
                Eye(At(11f, 3.25f, 1.55f), At(11.2f, -6f, 1.3f)),   // corridor porthole
                Eye(At(15.6f, 8f, 1.55f), At(22f, 8f, 1.6f)),       // viewing room, east window (sky only)
            }, "preview-station-windows.png");

            // Light modes: corridor and hangar, lights on / off / emergency (one row each).
            var corridor = Eye(At(8.5f, 3f), At(15f, 3f, 1.8f));
            var hangar = Eye(At(5.3f, 5.3f), At(2f, 1f, 1.9f));
            var modes = new[] { LuckyNumbers.Model.StationLightMode.On, LuckyNumbers.Model.StationLightMode.Off,
                                LuckyNumbers.Model.StationLightMode.Emergency };
            SaveGrid(new[] { corridor, hangar }, modes.Length, "preview-station-lights.png",
                     row => view.SetLights(modes[row]));
            view.SetLights(LuckyNumbers.Model.StationLightMode.On);

            // Room lean: upright, the content value, the corridor's full lean (hangar and viewing room per row).
            var leans = new[] { 0f, config.RoomLean, 1f };
            var room = Eye(At(14f, 8.6f), At(17.5f, 8f, 1.7f));
            SaveGrid(new[] { hangar, room }, leans.Length, "preview-station-lean.png", row =>
            {
                var c = config.Clone();
                c.RoomLean = leans[row];
                view.Build(c);
            });
            view.Build(config);
        }

        static Material KitMaterial(string kit)
        {
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/_Project/Art/Models/{kit}/atlas.png")
                        ?? throw new System.Exception($"{kit}/atlas.png not imported");
            return new Material(Find("LuckyNumbers/StationKit")) { mainTexture = atlas };
        }

        static LuckyNumbers.Station.StationView.KitPiece[] KitPieces(params string[] kits)
        {
            var list = new System.Collections.Generic.List<LuckyNumbers.Station.StationView.KitPiece>();
            foreach (var kit in kits)
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { $"Assets/_Project/Art/Models/{kit}" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                list.Add(new LuckyNumbers.Station.StationView.KitPiece
                    { name = Path.GetFileNameWithoutExtension(path), model = AssetDatabase.LoadAssetAtPath<GameObject>(path) });
            }
            return list.ToArray();
        }

        /// <summary>Cameras rendered into a 2 x 2 grid (each at half size), saved to Logs/.</summary>
        static void SaveGrid(Camera[] cams, string file) => SaveGrid(cams, 2, file, null, rowsAreCams: true);

        /// <summary>
        /// Grid of half-size tiles, 2 per row: either the cameras two by two (rowsAreCams), or the same cameras
        /// on every row with <paramref name="beforeRow"/> run first (e.g. switching a look per row).
        /// </summary>
        static void SaveGrid(Camera[] cams, int rows, string file, System.Action<int> beforeRow, bool rowsAreCams = false)
        {
            int w = Width / 2, h = Height / 2;
            var count = rowsAreCams ? Mathf.Min(cams.Length, rows * 2) : rows * 2;
            var sheet = new Texture2D(Width, h * rows, TextureFormat.RGB24, false);
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            for (var i = 0; i < count; i++)
            {
                var row = i / 2;
                if (!rowsAreCams && i % 2 == 0) beforeRow?.Invoke(row);
                var cam = rowsAreCams ? cams[i] : cams[i % 2];
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
                else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, w, h), (i % 2) * w, (rows - 1 - row) * h);
                RenderTexture.active = prev;
            }
            sheet.Apply();
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", file));
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Debug.Log($"[ShaderPreview] wrote {path}");
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
