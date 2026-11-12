using System.Collections.Generic;
using System.IO;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Renders of the Vertical pass (seed 1234, LN_HOLD_SEED to change), meshed with HoldMesher + HoldKit like the game:
    /// Logs/preview-stairs.png: from the foot of a flight of stairs, looking up it;
    /// Logs/preview-greatshaft.png: from a great shaft's top landing gateway, out across and down the shaft, the hoist
    /// cage at the next landing down with its lamp, the gates, cable and the landings below;
    /// Logs/preview-greatshaft-up.png: from the cage looking up the shaft at the headframe.
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.VerticalPreview.Render (needs graphics), or
    /// tools/ln.py preview stairs.
    /// </summary>
    public static class VerticalPreview
    {
        const int Width = 1280, Height = 720;

        [UnityEditor.MenuItem("LuckyNumbers/Preview/Render Hold Stairs + Great Shaft")]
        public static void Render()
        {
            var config = GameContent.Hold;
            var seed = uint.TryParse(System.Environment.GetEnvironmentVariable("LN_HOLD_SEED"), out var sd) ? sd : 1234u;
            var p = MineParams.From(config);
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/HoldKit.mat")
                      ?? throw new System.Exception("HoldKit.mat missing (tools/ln.py hold-textures)");
            var cell = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            var palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            var settings = HoldMeshSettings.From(config);

            // ---- stairs: the first region (near the top) with a flight whose foot has room to stand back ----
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var stairsDone = false;
            for (var ry = 0; ry >= -3 && !stairsDone; ry--)
            for (var rz = -1; rz <= 1 && !stairsDone; rz++)
            for (var rx = -1; rx <= 1 && !stairsDone; rx++)
            {
                var g = MineGenerator.Region(seed, new int3(rx, ry, rz), p, Allocator.Persistent);
                for (var i = 0; i < g.Cells.Length && !stairsDone; i++)
                {
                    var c0 = g.Cells[i];
                    if (!StairCell.IsStep(c0) || StairCell.Index(c0) != 0) continue;
                    var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                    var d = StairCell.Vec(StairCell.Dir(c0));
                    var back = 1;
                    while (back < 3 && HoldNav.Standable(g.Get(c - d * (back + 1)))) back++;
                    Show(g, palettes, settings, mat);
                    var len = StairCell.Length(g, c, c0);
                    var foot = g.ToWorld(c - d * back);
                    var top = g.ToWorld(c + d * len + new int3(0, 1, 0));
                    var eye = Centre(foot, cell) + new float3(0f, 1.6f, 0f) - (float3)d * (cell.x * 0.3f);
                    var at = Centre(top, cell) + new float3(0f, 1.2f, 0f);
                    var cam = MakeCamera(eye, at, 72f);
                    Save(cam, "preview-stairs.png");
                    Debug.Log($"[VerticalPreview] stairs: region {new int3(rx, ry, rz)} flight at {g.ToWorld(c)} dir {d} len {len} " +
                              $"({(StairCell.Timber(c0) ? "timber" : "stone")}), eye {eye}");
                    stairsDone = true;
                }
                g.Dispose();
            }
            if (!stairsDone) Debug.LogError("[VerticalPreview] no stairs found");

            // ---- great shaft: the first one near the top; every region it passes, and the column round it ----
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var found = false;
            var s = default(GreatShaft);
            for (var gz = -2; gz <= 2 && !found; gz++)
            for (var gx = -2; gx <= 2 && !found; gx++)
                found = MinePasses.GreatShaftOf(seed, new int2(gx, gz), 0, p, out s);
            if (!found) { Debug.LogError("[VerticalPreview] no great shaft found"); palettes.Dispose(); return; }
            var landings = new List<int>();
            for (var ry = s.TopRegion; ry >= s.BottomRegion; ry--)
            {
                var g = MineGenerator.Region(seed, new int3(s.Region.x, ry, s.Region.y), p, Allocator.Persistent);
                foreach (var f in g.Fixtures)
                    if (f.Kind == FixtureKind.Hoist)
                        for (var l = p.Levels - 1; l >= 0; l--)
                            if ((f.Data >> 16 & (1 << l)) != 0) landings.Add(ry * p.Levels + l);
                Show(g, palettes, settings, mat);
                g.Dispose();
            }
            palettes.Dispose();
            landings.Sort();
            landings.Reverse();

            var light = math.saturate(config.Ambient * 2.5f);
            var width = s.Size * cell.x - 2f * HoistKit.Inset;
            var min = (float2)s.WorldMin(p) * cell.x;
            var centre = min + s.Size * cell.x * 0.5f;
            var cageLevel = landings.Count > 1 ? landings[1] : landings[0];
            var cageY = cageLevel * cell.y;
            var cage = Place(HoistKit.Cage(width, cell.x, light), mat, new Vector3(centre.x, cageY, centre.y), HoistKit.Facing(s.Dock));
            var lamp = new GameObject("Hoist lamp").AddComponent<Light>();
            lamp.transform.SetParent(cage.transform, false);
            lamp.transform.localPosition = new Vector3(0f, HoistKit.Height - 0.3f, 0f);
            lamp.type = LightType.Point;
            lamp.color = new Color(config.LanternR, config.LanternG, config.LanternB);
            lamp.range = 9f;
            lamp.intensity = 1.5f;
            var cableBottom = cageY + HoistKit.Height + HoistKit.HookTop;
            var cableTop = (s.TopLevel(p) + 1) * cell.y - HoistKit.PulleyDrop;
            var cable = Place(HoistKit.Cable(cell.x, light), mat, new Vector3(centre.x, cableBottom, centre.y), Quaternion.identity);
            cable.transform.localScale = new Vector3(0.05f, cableTop - cableBottom, 0.05f);
            var dock = StairCell.Vec(s.Dock);
            var edge = (float2)(s.Region * p.Cells + s.DockEdge()) * cell.x + cell.x * 0.5f;
            var gateAt = edge + new float2(dock.x, dock.z) * (cell.x * 0.5f + 0.11f);
            var gateMesh = HoistKit.Gate(cell.x - 0.4f, cell.x, light);
            foreach (var level in landings)
            {
                var open = level == cageLevel ? 2.2f : 0f;
                Place(gateMesh, mat, new Vector3(gateAt.x, level * cell.y + open, gateAt.y), Quaternion.Euler(0f, dock.x != 0 ? 90f : 0f, 0f));
            }

            // From up in the shaft over the top landing's gateway, looking across and down at the cage and the landings.
            var topY = landings[0] * cell.y;
            var half = s.Size * cell.x * 0.5f;
            var eye2 = new float3(centre.x, topY + 2.3f, centre.y) + (float3)dock * (half - 0.4f);
            var look = new float3(centre.x, cageY + 0.4f, centre.y) - (float3)dock * (half * 0.55f);
            var cam2 = MakeCamera(eye2, look, 80f);
            Save(cam2, "preview-greatshaft.png");

            // From the shaft's floor by the bottom gateway, looking up past the cage to the landings and the headframe.
            var bottomY = landings[landings.Count - 1] * cell.y;
            cage.transform.position = new Vector3(centre.x, topY, centre.y);   // the cage up at the top landing
            cable.transform.position = new Vector3(centre.x, topY + HoistKit.Height + HoistKit.HookTop, centre.y);
            cable.transform.localScale = new Vector3(0.05f, math.max(0.01f, cableTop - topY - HoistKit.Height - HoistKit.HookTop), 0.05f);
            cam2.transform.position = new Vector3(centre.x, bottomY + 1.5f, centre.y) + (Vector3)((float3)dock * (half - 0.45f));
            cam2.transform.LookAt(new Vector3(centre.x, bottomY + 14f, centre.y) - (Vector3)((float3)dock * half * 0.3f));
            Save(cam2, "preview-greatshaft-up.png");
            Debug.Log($"[VerticalPreview] great shaft: column {s.Region} min {s.Min} size {s.Size} regions {s.TopRegion}..{s.BottomRegion} " +
                      $"dock {s.Dock}, landings {string.Join(",", landings)}, cage at storey {cageLevel}");
        }

        static float3 Centre(int3 world, float3 cell) => (new float3(world) + new float3(0.5f, 0f, 0.5f)) * cell;

        static void Show(HoldGrid g, NativeArray<PaletteLayers> palettes, HoldMeshSettings settings, Material mat)
        {
            var build = HoldMesher.Schedule(g, palettes, settings);
            foreach (var chunk in build.Complete())
            {
                var go = new GameObject($"Chunk {g.Origin} {chunk.Index}");
                go.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
            build.Dispose();
        }

        static GameObject Place(Mesh mesh, Material mat, Vector3 at, Quaternion rot)
        {
            var go = new GameObject(mesh.name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.transform.SetPositionAndRotation(at, rot);
            return go;
        }

        static Camera MakeCamera(float3 eye, float3 at, float fov)
        {
            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.05f, 0.05f);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.transform.position = eye;
            cam.transform.LookAt((Vector3)at);
            return cam;
        }

        static void Save(Camera cam, string file)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
            else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", file));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log($"[VerticalPreview] wrote {path}");
        }
    }
}
