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
    /// The Depth kit rendered (as ShaderPreview.RenderHold): a collapse's rubble heap, a drop shaft's lip with its
    /// broken rail and warning plate, a depth seal's hatch under its winch beam, side by side in
    /// Logs/preview-depth.png. Seed 1234 (LN_HOLD_SEED to change). A dim warm point light stands in for a lantern
    /// near each subject (as the mine's own lanterns would be, wherever they are).
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.DepthPreview.Render (with graphics).
    /// </summary>
    public static class DepthPreview
    {
        const int PanelW = 640, PanelH = 540;

        [UnityEditor.MenuItem("LuckyNumbers/Preview/Render Depth Kit")]
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var config = GameContent.Hold;
            var p = MineParams.From(config);
            var seed = uint.TryParse(System.Environment.GetEnvironmentVariable("LN_HOLD_SEED"), out var sv) ? sv : 1234u;
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/HoldKit.mat")
                      ?? throw new System.Exception("HoldKit.mat missing (tools/ln.py hold-textures)");
            var palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            var settings = HoldMeshSettings.From(config);
            var cell = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            var stone = "";
            foreach (var pal in config.Textures.Palettes) if (pal.Name == (config.Mines?.Palette ?? "mine")) stone = pal.Wall;
            var slices = DepthKit.Slices.From(config.Textures, stone);
            var shown = new HashSet<int3>();
            var grids = new Dictionary<int3, HoldGrid>();

            HoldGrid Grid(int3 r)
            {
                if (grids.TryGetValue(r, out var g)) return g;
                g = MineGenerator.Region(seed, r, p, Allocator.Persistent);
                grids[r] = g;
                return g;
            }
            void Show(int3 r)
            {
                if (!shown.Add(r)) return;
                var build = HoldMesher.Schedule(Grid(r), palettes, settings);
                foreach (var chunk in build.Complete())
                {
                    var go = new GameObject($"Chunk {r} {chunk.Index}");
                    go.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                }
                build.Dispose();
            }
            Vector3 Floor(HoldGrid g, int3 c) { var w = g.ToWorld(c); return new Vector3((w.x + 0.5f) * cell.x, w.y * cell.y, (w.z + 0.5f) * cell.z); }
            void Lamp(Vector3 at)
            {
                var l = new GameObject("Lamp").AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(config.LanternR, config.LanternG, config.LanternB);
                l.range = 9f;
                l.intensity = 1.6f;
                l.transform.position = at;
            }
            void Piece(Mesh mesh, Vector3 at, Quaternion rot)
            {
                var go = new GameObject(mesh.name);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.transform.SetPositionAndRotation(at, rot);
            }
            // A view from `away` cells off target cell c along a standable direction, eye 1.65 m, looking at `look`.
            bool View(HoldGrid g, int3 c, int away, float lookY, out Vector3 eye, out Vector3 target)
            {
                eye = target = default;
                for (var a = away; a >= 1; a--)
                for (var i = 0; i < 4; i++)
                {
                    var d = HoldRules.Around(i);
                    var ok = true;
                    for (var s = 1; s <= a && ok; s++) ok = HoldNav.Standable(g.Get(c + d * s)) && g.Get(c + d * s).Kind != CellKind.ShaftDown;
                    if (!ok) continue;
                    eye = Floor(g, c + d * a) + Vector3.up * 1.65f;
                    target = Floor(g, c) + Vector3.up * lookY;
                    return true;
                }
                return false;
            }

            var views = new List<(string name, Vector3 eye, Vector3 target)>();

            // 1. A collapse near the hold's column.
            var found = false;
            for (var r = 0; r <= 2 && !found; r++)
            for (var dx = -r; dx <= r && !found; dx++)
            for (var dz = -r; dz <= r && !found; dz++)
            {
                var region = new int3(dx, -1, dz);
                var g = Grid(region);
                for (var i = 0; i < g.Cells.Length && !found; i++)
                {
                    if (g.Cells[i].Kind != CellKind.Rubble) continue;
                    var c = new int3(i % g.Size.x, i / (g.Size.x * g.Size.z), i / g.Size.x % g.Size.z);
                    if (!View(g, c, 2, 0.8f, out var eye, out var target)) continue;
                    Show(region);
                    Lamp(Vector3.Lerp(eye, target, 0.2f) + Vector3.up * 0.5f);
                    views.Add(($"collapse {g.ToWorld(c)}", eye, target));
                    found = true;
                }
            }

            // 2. A drop shaft's lip.
            found = false;
            for (var y = -1; y >= -6 && !found; y--)
            for (var dx = -2; dx <= 2 && !found; dx++)
            for (var dz = -2; dz <= 2 && !found; dz++)
            {
                var region = new int3(dx, y, dz);
                if (!MinePasses.DropBelow(seed, region, p, out var col)) continue;
                var g = Grid(region);
                var c = new int3(col.x, 0, col.y);
                if (!View(g, c, 1, -2.2f, out var eye, out var target)) continue;
                Show(region);
                Show(region - new int3(0, 1, 0));
                for (var i = 0; i < 4; i++)
                {
                    var d = HoldRules.Around(i);
                    if (!HoldRules.StepOff(g.Get(c + d))) continue;
                    var dir = new Vector3(d.x, 0f, d.z);
                    Piece(DepthKit.WarningPlate(config.CellSize, slices, 1f), Floor(g, c) + dir * (cell.x * 0.5f + 0.2f) + Vector3.up * 0.78f,
                          Quaternion.LookRotation(-dir, Vector3.up) * Quaternion.Euler(0f, 0f, 4f));
                    break;
                }
                Lamp(Floor(g, c) + Vector3.up * 2.4f + (eye - Floor(g, c)).normalized * 1.2f);
                views.Add(($"drop lip {g.ToWorld(c)}", eye + Vector3.up * 0.2f, target));
                found = true;
            }

            // 3. A depth seal (band 0's bottom).
            found = false;
            var sy = -MinePasses.BandBottom(0, p);
            for (var dx = -2; dx <= 2 && !found; dx++)
            for (var dz = -2; dz <= 2 && !found; dz++)
            {
                var region = new int3(dx, sy, dz);
                if (!MineGenerator.ShaftBelow(seed, region, p, out var col)) continue;
                var g = Grid(region);
                var c = new int3(col.x, 0, col.y);
                if (!View(g, c, 2, 1.0f, out var eye, out var target)) continue;
                Show(region);
                Piece(DepthKit.SealHatch(config.CellSize, slices, 0.62f), Floor(g, c), Quaternion.identity);
                var chain = DepthKit.Chain(config.CellSize, slices, 0.55f);
                for (var i = 0; i < 2; i++)
                {
                    var bottom = DepthKit.HatchHeight + 0.05f;
                    var go = new GameObject("Chain");
                    go.AddComponent<MeshFilter>().sharedMesh = chain;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    go.transform.position = Floor(g, c) + new Vector3(i == 0 ? -0.08f : 0.08f, bottom, 0f);
                    go.transform.localScale = new Vector3(1f, cell.y - 0.5f - bottom, 1f);
                }
                Lamp(Vector3.Lerp(eye, target, 0.2f) + Vector3.up * 0.5f);
                views.Add(($"seal {g.ToWorld(c)}", eye + Vector3.up * 0.25f, target));
                found = true;
            }

            foreach (var g in grids.Values) g.Dispose();
            palettes.Dispose();

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.05f, 0.05f);
            cam.fieldOfView = 68f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            var sheet = new Texture2D(PanelW * 3, PanelH, TextureFormat.RGB24, false);
            var black = new Color32[PanelW * 3 * PanelH];
            sheet.SetPixels32(black);
            for (var v = 0; v < views.Count && v < 3; v++)
            {
                cam.transform.position = views[v].eye;
                cam.transform.LookAt(views[v].target);
                var rt = new RenderTexture(PanelW, PanelH, 24, RenderTextureFormat.ARGB32);
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
                else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, PanelW, PanelH), v * PanelW, 0);
                RenderTexture.active = prev;
                rt.Release();
                Debug.Log($"[DepthPreview] panel {v}: {views[v].name}");
            }
            sheet.Apply();
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-depth.png"));
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Debug.Log($"[DepthPreview] wrote {path} ({views.Count} views)");
        }
    }
}
