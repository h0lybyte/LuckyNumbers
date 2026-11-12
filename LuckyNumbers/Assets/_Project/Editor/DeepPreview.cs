using System.IO;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using LuckyNumbers.Space;
using Unity.Mathematics;
using UnityEditor;
using MenuItem = UnityEditor.MenuItem;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// The Emberdeep (content/universes.txtpb) for a look without a build, each a 2x2 sheet in Logs/:
    /// preview-deep-space.png: one of its systems from above, and three of its worlds up close;
    /// preview-deep-ground.png: the terraced crash site with the hold's surface gate (the wake spot, the steps, from the
    /// air, the far terraces). Batch: tools/ln.py preview deep-space / deep-ground (-lnSeed N picks the lucky number).
    /// </summary>
    public static class DeepPreview
    {
        const int W = 640, H = 360;
        const string Universe = "emberdeep";

        static uint SeedArg()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lnSeed" && uint.TryParse(args[i + 1], out var s)) return s;
            return 4242u;
        }

        [MenuItem("LuckyNumbers/Preview/Render Emberdeep System")]
        public static void RenderSpace()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rules = Multiverse.Generator(Universe, null);
            var roll = new UniverseSeedChanged(SeedArg(), -1f, -1f, -1f, -1f);
            var bodies = UniverseGenerator.Generate(rules, roll);
            var world = new SpaceWorld(bodies);
            OrbitSystem.Update(world, 0f);

            RenderSettings.skybox = new Material(Shader.Find("LuckyNumbers/Starfield"));
            UniverseGenerator.Sky(rules, roll)?.ApplyTo(RenderSettings.skybox);
            var sun = world.FirstStar();
            Shader.SetGlobalVector("_LnSunPosition", sun >= 0 ? new Vector4(world.Position[sun].x, world.Position[sun].y, world.Position[sun].z, 1f) : Vector4.zero);
            Shader.SetGlobalVector("_LnSunColor", sun >= 0 ? world.Star[sun].LightColor.ToColor() : Color.white);

            var planetShader = Shader.Find("LuckyNumbers/Planet3D");
            var starShader = Shader.Find("LuckyNumbers/Star");
            var worlds = new System.Collections.Generic.List<int>();
            var log = new System.Text.StringBuilder();
            for (var i = 0; i < world.Count; i++)
            {
                if (world.Kind[i] == BodyKind.Galaxy) continue;
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = world.Ids[i];
                go.transform.position = world.Position[i];
                go.transform.localScale = Vector3.one * world.Scale[i];
                var m = new Material(world.Kind[i] == BodyKind.Star ? starShader : planetShader);
                if (world.Kind[i] == BodyKind.Star) world.Star[i].ApplyTo(m);
                else world.Planet[i].ApplyTo(m);
                go.GetComponent<MeshRenderer>().sharedMaterial = m;
                if (world.Kind[i] == BodyKind.Planet && world.OrbitParent[i] >= 0 && world.Kind[world.OrbitParent[i]] == BodyKind.Star) worlds.Add(i);
                log.Append($" {world.Ids[i]}(d{world.Scale[i]:0.#})");
            }
            Debug.Log($"[DeepPreview] {Universe} lucky number {roll.Seed}:{log}");

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.farClipPlane = 20000f;
            var sheet = new Texture2D(W * 2, H * 2, TextureFormat.RGB24, false);
            var centre = sun >= 0 ? (Vector3)world.Position[sun] : Vector3.zero;

            // Behind the innermost world, looking past it at the dim star (its rim and haze against the sun).
            var first = worlds[0];
            var fp = (Vector3)world.Position[first];
            var away = (fp - centre).normalized;
            var eye = fp + away * (world.Scale[first] * 2.5f) + Vector3.up * (world.Scale[first] * 0.5f) + Vector3.Cross(away, Vector3.up) * world.Scale[first] * 0.9f;
            cam.fieldOfView = 55f;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(Vector3.Lerp(fp, centre, 0.02f) - eye));
            Shoot(cam, sheet, 0);
            // Close-ups: the next three worlds, lit from the side (the terminator shows).
            for (var k = 0; k < 3; k++)
            {
                var i = worlds[math.min(k + 1, worlds.Count - 1)];
                var p = (Vector3)world.Position[i];
                var toSun = (centre - p).normalized;
                var side = Vector3.Cross(toSun, Vector3.up).normalized;
                eye = p + (side * 0.8f + toSun * 0.6f + Vector3.up * 0.25f).normalized * world.Scale[i] * 2.2f;
                cam.fieldOfView = 40f;
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(p - eye));
                Shoot(cam, sheet, k + 1);
            }
            Save(sheet, "preview-deep-space.png");
        }

        [MenuItem("LuckyNumbers/Preview/Render Emberdeep Crash Site")]
        public static void RenderGround()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var config = GameContent.Planet;
            var rules = Multiverse.Generator(Universe, null);
            var roll = new UniverseSeedChanged(SeedArg(), -1f, -1f, -1f, -1f);
            var bodies = UniverseGenerator.Generate(rules, roll);
            var planet = bodies.Find(b => b.Id == "home");
            var star = bodies.Find(b => b.Star != null);
            var look = planet.Planet;
            var land = planet.Ground;
            var seed = JobSite.Seed(roll.Seed, 0u);
            var rng = new Random(seed | 1u);

            // The ground, as CrashSitePresenter builds it.
            var ground = new GameObject("Ground").AddComponent<GroundView>();
            var so = new SerializedObject(ground);
            so.FindProperty("material").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/Ground.mat");
            so.FindProperty("holdKit").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/HoldKit.mat");
            so.ApplyModifiedPropertiesWithoutUndo();
            var m = ground.Material;
            m.SetColor("_PlanetColor", look.PlanetColor.ToColor());
            m.SetColor("_AtmosphereColor", look.AtmosphereColor.ToColor());
            m.SetVector("_NoiseOffset", new Vector4(look.NoiseOffset.X, look.NoiseOffset.Y, look.NoiseOffset.Z, 0f));
            m.SetFloat("_NoiseRemap", look.NoiseRemap);
            m.SetFloat("_SurfaceScale", land.SurfaceNoiseScale);
            m.SetFloat("_Haze", config.Haze);
            m.SetFloat("_HazeDistance", config.ChunkSize * math.max(1, config.ViewChunks) * 1.1f);
            ground.Configure(config.ChunkSize, config.ChunkResolution, config.ViewChunks, land.Height, land.NoiseScale,
                             rng.NextFloat2(0f, 100f), config.WreckDistance + config.WreckLength * 0.6f);
            var far = math.max(0, config.FarViewChunks);
            m.SetFloat("_HazeHorizon", far > 0 ? (far + 0.5f) * config.ChunkSize * math.max(2, config.FarChunkScale) : 0f);
            ground.ConfigureFar(config.FarChunkScale, config.FarResolution, config.FarViewChunks, land.SiteRelief, land.SwellHeight, land.SwellScale);
            ground.ConfigureRelief(land.TerraceHeight, land.TerraceSharpness, land.TerraceBreakScale, land.CliffHeight, land.CliffScale);
            var gate = GatePlacement.Place(seed, config.Gate);
            gate = gate.At(ground.Pad(new float2(gate.Position.x, gate.Position.z), config.Gate.PadRadius));
            ground.Stream(Vector3.zero, int.MaxValue);

            RenderSettings.skybox = new Material(Shader.Find("LuckyNumbers/Starfield"));
            UniverseGenerator.Sky(rules, roll)?.ApplyTo(RenderSettings.skybox);
            RenderSettings.skybox.SetColor("_HazeColor", look.AtmosphereColor.ToColor());
            RenderSettings.skybox.SetFloat("_Haze", config.Haze);
            var sunDir = (Quaternion.Euler(-config.SunElevationDegrees, 205f, 0f) * Vector3.forward * -1f);
            sunDir.y = math.abs(sunDir.y);
            sunDir.Normalize();
            Shader.SetGlobalVector("_LnSunColor", star.Star.LightColor.ToColor());

            SurfaceGateBuilder.Build(gate, config.Gate, ground.HoldKit, sunDir, star.Star.LightColor.ToColor(), look.AtmosphereColor.ToColor(),
                                     config.ChunkSize * math.max(1, config.ViewChunks) * 2.2f, out _);
            // The wreck's spot, as a stand-in block (the hull comes from Addressables in the game).
            var wreck = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wreck.transform.position = new Vector3(0f, ground.HeightAt(new Vector3(0f, 0f, config.WreckDistance)) + 4f, config.WreckDistance);
            wreck.transform.localScale = new Vector3(18f, 8f, config.WreckLength);
            wreck.transform.rotation = Quaternion.Euler(10f, -130f, 0f);
            wreck.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(0.25f, 0.27f, 0.3f) };
            Debug.Log($"[DeepPreview] {Universe} lucky number {roll.Seed}: gate at ({gate.Position.x:0}, {gate.Position.y:0.0}, {gate.Position.z:0}) facing {gate.Yaw:0}");

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.farClipPlane = 3000f;
            cam.nearClipPlane = 0.1f;
            var sheet = new Texture2D(W * 2, H * 2, TextureFormat.RGB24, false);
            var door = gate.Position + Vector3.up * 3.5f;

            void View(int slot, Vector3 eye, Vector3 at, float fov)
            {
                cam.fieldOfView = fov;
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
                var s = eye + sunDir * 900f;
                Shader.SetGlobalVector("_LnSunPosition", new Vector4(s.x, s.y, s.z, 1f));
                Shoot(cam, sheet, slot);
            }
            Vector3 On(Vector3 p, float up) => new(p.x, ground.HeightAt(p) + up, p.z);
            // Three-quarter view of the gate from the site's side: the facade cut into its rock, on its pad.
            View(0, On(gate.Position + gate.Rotation * new Vector3(-22f, 0f, 30f), 5f), door + Vector3.up * 2f, 60f);
            // At the foot of the steps.
            View(1, On(gate.Outside(config.Gate), 1.7f), door, 70f);
            // From the air, off to one side: the site, the wreck, the gate, the steps of land round them.
            var side = Vector3.Cross(Vector3.up, gate.Position.normalized);
            View(2, On(gate.Position * 1.6f + side * 90f, 55f), new Vector3(gate.Position.x * 0.6f, gate.Position.y, gate.Position.z * 0.6f), 60f);
            // Out on the full-height terraces, 300 m from the wreck, looking along them.
            var outward = -gate.Position.normalized;
            var stand = outward * 300f;
            ground.Stream(stand, int.MaxValue);   // the detailed chunks out there
            View(3, On(stand, 8f), On(stand + Vector3.Cross(Vector3.up, outward) * 110f + outward * 40f, 0f), 65f);
            Save(sheet, "preview-deep-ground.png");
        }

        static void Shoot(Camera cam, Texture2D sheet, int slot)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
            else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            sheet.ReadPixels(new Rect(0, 0, W, H), (slot % 2) * W, (1 - slot / 2) * H);
            RenderTexture.active = prev;
            rt.Release();
        }

        static void Save(Texture2D sheet, string name)
        {
            sheet.Apply();
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", name));
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Debug.Log($"[DeepPreview] wrote {path}");
        }
    }
}
