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
            // LN_SHIP_SKIN: one skin, or several comma-separated (one preview-ship-<skin>.png each). In-game look
            // (SceneBuilder.ShipLook) under a planet's sky / ground ambient (LN_SHIP_PLANET=1: warm sky, rust ground).
            var skins = (System.Environment.GetEnvironmentVariable("LN_SHIP_SKIN") ?? "idolknight").Split(',');
            var mat = new Material(Find("LuckyNumbers/Ship"))
            {
                mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/_Project/Art/Models/Fighter/Skins/{skins[0]}.jpg"),
            };
            SceneBuilder.ShipLook(mat);
            var planet = System.Environment.GetEnvironmentVariable("LN_SHIP_PLANET") == "1";
            Shader.SetGlobalVector("_LnSkyColor", planet ? new Vector4(1.15f, 0.8f, 0.6f, 1f) : Vector4.zero);
            Shader.SetGlobalVector("_LnGroundColor", planet ? new Vector4(0.55f, 0.35f, 0.3f, 1f) : Vector4.zero);
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
            if (skins.Length > 1)
                foreach (var skin in skins)
                {
                    mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/_Project/Art/Models/Fighter/Skins/{skin}.jpg");
                    Save(cam, $"preview-ship-{skin}.png");
                }
        }

        /// <summary>
        /// Engine flames (content/engines.txtpb) on the fighter, one row per manoeuvre, from behind-above (left) and
        /// below-side (right), to Logs/preview-exhaust.png. Rows: idle, small trim (cold gas only), cruise, boost, lift,
        /// strafe right + yaw right, roll + pitch, brake. LN_EXHAUST_SHIP=freighter renders the museum freighter;
        /// LN_EXHAUST_SKY=1 puts it against a bright planet sky (does the cold gas still read?).
        /// </summary>
        [MenuItem("LuckyNumbers/Preview/Render Exhaust")]
        public static void RenderExhaust()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = new Material(Find("LuckyNumbers/Starfield"));
            Shader.SetGlobalVector("_LnSunPosition", new Vector4(40f, 30f, -26f, 1f));
            Shader.SetGlobalVector("_LnSunColor", new Color(1f, 0.95f, 0.85f));
            var sky = System.Environment.GetEnvironmentVariable("LN_EXHAUST_SKY") == "1";
            var skyColour = new Color(0.95f, 0.55f, 0.3f);
            Shader.SetGlobalVector("_LnSkyColor", sky ? new Vector4(1.15f, 0.8f, 0.6f, 1f) : Vector4.zero);
            Shader.SetGlobalVector("_LnGroundColor", Vector4.zero);
            // LN_EXHAUST_SHIP=freighter: the museum freighter instead of the fighter.
            var freighter = System.Environment.GetEnvironmentVariable("LN_EXHAUST_SHIP") == "freighter";
            var ship = freighter ? SceneBuilder.FreighterRig() : SceneBuilder.ShipRig();
            ship.Show(System.Environment.GetEnvironmentVariable("LN_SHIP_SKIN") ?? "idolknight", 4f);
            ship.Pose(Vector3.zero, Quaternion.identity);
            var states = new (Vector3 push, Vector3 turn, float boost)[]
            {
                (Vector3.zero, Vector3.zero, 1f),                     // idle
                (new Vector3(0.08f, 0.08f, 0f), new Vector3(0.1f, 0.1f, 0.1f), 1f),   // small trim: cold gas only
                (Vector3.forward, Vector3.zero, 1f),                  // cruise
                (Vector3.forward, Vector3.zero, 2f),                  // boost
                (Vector3.up, Vector3.zero, 1f),                       // lift
                (Vector3.right, new Vector3(0f, 1f, 0f), 1f),         // strafe right + yaw right
                (Vector3.zero, new Vector3(0.8f, 0f, 1f), 1f),        // pitch down + roll
                (Vector3.back, Vector3.zero, 1f),                     // brake
            };
            Camera Cam(Vector3 at, Vector3 look)
            {
                var c = new GameObject("PreviewCamera").AddComponent<Camera>();
                c.fieldOfView = 40f;
                c.clearFlags = sky ? CameraClearFlags.SolidColor : CameraClearFlags.Skybox;
                c.backgroundColor = skyColour;
                c.nearClipPlane = 0.05f;
                c.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look - at));
                return c;
            }
            var cams = new[] { Cam(new Vector3(3.2f, 2.2f, -6.5f), new Vector3(0f, 0f, -1.2f)), Cam(new Vector3(6.5f, -2f, 0.5f), new Vector3(0f, -0.4f, -0.3f)) };
            SaveGrid(cams, states.Length, "preview-exhaust.png", row =>
            {
                var s = states[row];
                ship.SetThrust(s.push, s.turn, s.boost);
                ship.SnapEngines();
            });
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
            view.Init(new Material(SceneBuilder.KitMaterial("CombinedKit")), new Material(Find("LuckyNumbers/StationGlass")),
                      SceneBuilder.KitPieces("StationKit", "StarshipKit"));

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
            var seeded = config.Generator != null && config.Generator.Enabled;
            var layout = seeded ? LuckyNumbers.Station.LayoutGenerator.Generate(config, 3250720893u) : null;   // the title's first roll
            view.Build(config, layout);
            var plan = view.Plan;

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
            Vector3 Eyes(Vector2Int cell, float y = 1.6f) => LuckyNumbers.Station.StationPlan.Centre(cell) + Vector3.up * y;
            // A room seen from its south end, looking north (the planet side, where most windows are).
            Camera RoomView(System.Collections.Generic.List<Vector2Int> room)
            {
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                foreach (var c in room)
                {
                    minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
                    minZ = Mathf.Min(minZ, c.y); maxZ = Mathf.Max(maxZ, c.y);
                }
                var mid = (minX + maxX) / 2f;
                var from = new Vector3((mid + 0.5f) * 2f, 1.6f, (minZ + 0.25f) * 2f);
                var to = new Vector3((mid + 0.5f) * 2f, 1.9f, (maxZ + 1f) * 2f);
                return Eye(from, to);
            }

            var rooms = plan.Rooms();
            var spawnRoom = rooms.Find(r => r.Contains(plan.Spawn));
            var spawn = Eye(Eyes(plan.Spawn), Eyes(plan.Spawn) + Quaternion.Euler(0, config.SpawnYaw, 0) * Vector3.forward * 4f);
            var (runFrom, runTo) = plan.LongestCorridor();
            var corridor = Eye(Eyes(runFrom), Eyes(runTo, 1.8f));
            var big = rooms.FindAll(r => r != spawnRoom);

            var top = new GameObject("Top").AddComponent<Camera>();
            top.orthographic = true;
            top.clearFlags = CameraClearFlags.SolidColor;
            top.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            var size = new Vector2(plan.Width, plan.Depth) * 2f;
            top.orthographicSize = Mathf.Max(size.y, size.x * Height / Width) * 0.55f;
            top.transform.SetPositionAndRotation(new Vector3(size.x / 2f, 30f, size.y / 2f), Quaternion.Euler(90, 0, 0));
            top.farClipPlane = 60f;

            // Overview: where you start, the two biggest rooms, the plan from above.
            SaveGrid(new[] { spawn, RoomView(big[0]), RoomView(big[1]), top }, "preview-station.png");
            // More rooms and the longest corridor.
            var more = new System.Collections.Generic.List<Camera>();
            for (var i = 2; i < big.Count && more.Count < 3; i++) more.Add(RoomView(big[i]));
            more.Add(corridor);
            SaveGrid(more.ToArray(), "preview-station-rooms.png");

            // Joins, to hunt overdraw: a doorway seen from the corridor and from its room, a corridor's inner
            // turn, a tall room's corner and the wall over its doorway (cells in plan coordinates: column, row).
            Vector3 P(float col, float row, float y = 1.6f) =>
                new Vector3((col + 0.5f) * 2f, y, (plan.Depth - 1 - row + 0.5f) * 2f);
            SaveGrid(new[]
            {
                Eye(P(15f, 11f, 1.7f), P(12.6f, 11f, 2.2f)),      // bay-hub corridor, looking west into the bay door
                Eye(P(10.5f, 12.5f, 1.6f), P(14.5f, 10.8f, 2.4f)),// from inside the bay at its east door, up and right
                Eye(P(12.4f, 19.6f, 2.1f), P(10.6f, 19f, 2.6f)),  // south corridor's turn down to storage: inner corner
                Eye(P(2.2f, 15.8f, 1.6f), P(0.6f, 17.4f, 4.5f)),  // bay's south-west corner, looking up both storeys
            }, "preview-station-joins.png");
            SaveGrid(new[]
            {
                Eye(P(13.5f, 19f, 1.6f), P(10.5f, 19f, 2.4f)),    // along the south corridor to its west turn
                Eye(P(11f, 19f, 1.0f), P(11.01f, 19.01f, 6f)),    // in the turn, straight up
                Eye(P(11f, 21.5f, 1.6f), P(11f, 18.4f, 2.6f)),    // up the storage leg into the turn
                Eye(P(12.4f, 19.6f, 2.1f), P(10.6f, 19f, 2.6f)),  // the gap shot again
            }, "preview-station-turn.png");

            // Light modes: corridor and the biggest room, lights on / off / emergency (one row each).
            var modes = new[] { LuckyNumbers.Model.StationLightMode.On, LuckyNumbers.Model.StationLightMode.Off,
                                LuckyNumbers.Model.StationLightMode.Emergency };
            SaveGrid(new[] { corridor, RoomView(big[0]) }, modes.Length, "preview-station-lights.png",
                     row => view.SetLights(modes[row]));
            view.SetLights(LuckyNumbers.Model.StationLightMode.On);
            // Four lucky numbers, four stations (from above).
            if (seeded)
            {
                int w = Width / 2, h = Height / 2;
                var sheet = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                var seeds = new[] { 1u, 777u, 424242u, 9001u };
                for (var i = 0; i < seeds.Length; i++)
                {
                    view.Build(config, LuckyNumbers.Station.LayoutGenerator.Generate(config, seeds[i]));
                    var p2 = view.Plan;
                    var cam = new GameObject("Seed").AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
                    var span = new Vector2(p2.Width, p2.Depth) * 2f;
                    cam.orthographicSize = Mathf.Max(span.y, span.x * Height / Width) * 0.55f;
                    cam.transform.SetPositionAndRotation(new Vector3(span.x / 2f, 30f, span.y / 2f), Quaternion.Euler(90, 0, 0));
                    cam.farClipPlane = 60f;
                    var request = new RenderPipeline.StandardRequest { destination = rt };
                    if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
                    else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    sheet.ReadPixels(new Rect(0, 0, w, h), (i % 2) * w, (1 - i / 2) * h);
                    RenderTexture.active = prev;
                    Object.DestroyImmediate(cam.gameObject);
                }
                sheet.Apply();
                File.WriteAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-station-seeds.png")),
                                   sheet.EncodeToPNG());
                view.Build(config, layout);
            }
            Debug.Log($"[ShaderPreview] station: {rooms.Count} rooms (largest {big[0].Count} cells), corridor run " +
                      $"{runFrom} -> {runTo}");
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
