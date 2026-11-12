using System.IO;
using LuckyNumbers.Hold;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// The hold's top (seed 1234, LN_HOLD_SEED to change): the hall region and mine region (0,0,0) under it, meshed
    /// with HoldMesher and HoldKit. Logs/preview-hall.png: from just inside the gate into the great hall;
    /// Logs/preview-hall-top.png: overhead cutaway, haze off (faces point inward, so you look into the rooms).
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.HallPreview.Render (needs graphics).
    /// </summary>
    public static class HallPreview
    {
        const int Width = 1280, Height = 720;

        [UnityEditor.MenuItem("LuckyNumbers/Preview/Render Hold Hall")]
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var config = LuckyNumbers.Model.GameContent.Hold;
            var seed = uint.TryParse(System.Environment.GetEnvironmentVariable("LN_HOLD_SEED"), out var s) ? s : 1234u;
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/HoldKit.mat")
                      ?? throw new System.Exception("HoldKit.mat missing (tools/ln.py hold-textures)");
            var palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            var settings = HoldMeshSettings.From(config);
            var hall = HallGenerator.Generate(seed, config, Allocator.Persistent, out var plan);
            var mine = MineGenerator.Region(seed, int3.zero, config, Allocator.Persistent);
            var triangles = 0;
            foreach (var grid in new[] { hall, mine })
            {
                var build = HoldMesher.Schedule(grid, palettes, settings);
                foreach (var chunk in build.Complete())
                {
                    var go = new GameObject($"Chunk {grid.Origin} {chunk.Index}");
                    go.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    triangles += (int)chunk.Mesh.GetIndexCount(0) / 3;
                }
                build.Dispose();
            }

            var cell = new float3(config.CellSize, config.StoreyHeight, config.CellSize);
            float3 Floor(int3 local) => (new float3(hall.ToWorld(local)) + new float3(0.5f, 0f, 0.5f)) * cell;
            var inward = (float3)(-plan.Outward);
            var eyeAt = Environment("LN_HALL_EYE", 2.2f);   // cells in from the spawn cell's centre
            var eye = Floor(plan.Spawn) + inward * cell.x * eyeAt + new float3(0f, 1.7f, 0f);
            var hallCentre = new float3(plan.Hall.x + plan.Hall.z * 0.5f, 0f, plan.Hall.y + plan.Hall.w * 0.5f);
            var far = (new float3(hall.Origin) + new float3(hallCentre.x, 0f, hallCentre.z)) * cell;
            var target = eye + inward * 20f + new float3(0f, 1.2f, 0f);
            hall.Dispose();
            mine.Dispose();
            palettes.Dispose();

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.05f, 0.05f);
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.transform.position = eye;
            cam.transform.LookAt((Vector3)target);
            Save(cam, "preview-hall.png");

            var clear = new Material(mat);
            clear.SetFloat("_HazeDistance", 100000f);
            foreach (var r in Object.FindObjectsByType<MeshRenderer>()) r.sharedMaterial = clear;
            var size = config.RegionCells * config.CellSize;
            cam.transform.SetPositionAndRotation(new Vector3(size * 0.5f, 110f, size * 0.5f), Quaternion.Euler(90f, 0f, 0f));
            cam.fieldOfView = 36f;
            Save(cam, "preview-hall-top.png");
            Debug.Log($"[HallPreview] seed {seed}: {plan.Rooms.Count} rooms, {triangles} triangles, gate out {plan.Outward}, " +
                      $"hall centre {far}");
        }

        static float Environment(string name, float fallback) =>
            float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

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
            Debug.Log($"[HallPreview] wrote {path}");
        }
    }
}
