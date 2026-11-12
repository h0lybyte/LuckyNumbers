using System.Collections.Generic;
using System.IO;
using LuckyNumbers.Hold;
using LuckyNumbers.HoldScene;
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
    /// Renders the puzzles' fixtures in a real mine region (seed 1234, LN_HOLD_SEED to change): the first region found
    /// (depths 0..5) with a locked door and a portcullis, meshed as the game does, every fixture built by PuzzleKit as
    /// PuzzlePresenter builds it. Logs/preview-puzzles.png, three rows of two: locked door, key; portcullis, lever;
    /// door swung open, portcullis raised (lever pulled).
    /// Run: Unity -batchmode -quit -executeMethod LuckyNumbers.Editor.PuzzlePreview.Render (with graphics)
    /// </summary>
    public static class PuzzlePreview
    {
        const int TileW = 800, TileH = 500;

        [UnityEditor.MenuItem("LuckyNumbers/Preview/Render Puzzles")]
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var config = GameContent.Hold;
            var seed = uint.TryParse(System.Environment.GetEnvironmentVariable("LN_HOLD_SEED"), out var s) ? s : 1234u;
            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/HoldKit.mat")
                      ?? throw new System.Exception("HoldKit.mat missing (tools/ln.py hold-textures)");
            var p = MineParams.From(config);
            var settings = HoldMeshSettings.From(config);
            var slices = PuzzleKit.Slices.From(config.Textures);
            var glint = new Color(config.LanternR, config.LanternG, config.LanternB) * 2.2f;

            HoldGrid g = default;
            var found = int3.zero;
            for (var depth = 0; depth < 6 && !g.IsCreated; depth++)
            for (var i = 0; i < 49 && !g.IsCreated; i++)
            {
                var region = new int3(i % 7 - 3, -depth, i / 7 - 3);
                var grid = MineGenerator.Region(seed, region, p, Allocator.Persistent);
                bool hasDoor = false, hasGate = false;
                foreach (var f in grid.Fixtures) { hasDoor |= f.Kind == FixtureKind.LockedDoor; hasGate |= f.Kind == FixtureKind.Portcullis; }
                if (hasDoor && hasGate) { g = grid; found = region; }
                else grid.Dispose();
            }
            if (!g.IsCreated) throw new System.Exception($"[PuzzlePreview] seed {seed}: no region with a door and a gate");

            var palettes = HoldPalettes.Resolve(config.Textures, Allocator.Persistent);
            var build = HoldMesher.Schedule(g, palettes, settings);
            foreach (var chunk in build.Complete())
            {
                var go = new GameObject($"Chunk {chunk.Index}");
                go.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
            build.Dispose();
            palettes.Dispose();

            var parts = new Dictionary<FixtureKind, (PuzzleParts parts, HoldFixture f)>();
            var all = new List<PuzzleParts>();
            foreach (var f in g.Fixtures)
            {
                var built = PuzzleKit.Build(g, f, mat, slices, settings, glint);
                if (built == null) continue;
                all.Add(built);
                PuzzleKit.Pose(built, 0f, 1, settings);
                if (!parts.ContainsKey(f.Kind)) parts[f.Kind] = (built, f);
            }

            var ports = new NativeList<int3>(16, Allocator.Temp);
            MinePasses.PuzzlePorts(g, ports);
            var open = new NativeArray<bool>(g.Cells.Length, Allocator.Temp);
            HoldNav.Flood(g, ports[0], true, open);

            var cam = new GameObject("PreviewCamera").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.05f, 0.05f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 65f;

            var shots = new List<(Vector3 eye, Vector3 at)>();
            var door = parts[FixtureKind.LockedDoor];
            var gateP = parts[FixtureKind.Portcullis];
            shots.Add(Approach(g, door.f, door.parts, open, settings));
            shots.Add(Above(g, parts.TryGetValue(FixtureKind.Key, out var key) ? key.parts.Point : door.parts.Point, settings));
            shots.Add(Approach(g, gateP.f, gateP.parts, open, settings));
            var lever = parts[FixtureKind.Lever].parts;
            shots.Add((lever.Point + lever.Facing * 1.6f + Vector3.up * 0.25f + Vector3.Cross(Vector3.up, lever.Facing) * 0.5f, lever.Point));

            var sheet = new Texture2D(TileW * 2, TileH * 3, TextureFormat.RGB24, false);
            var rt = new RenderTexture(TileW, TileH, 24, RenderTextureFormat.ARGB32);
            for (var k = 0; k < 6; k++)
            {
                if (k == 4)
                {
                    // Opened: the door swung away from the camera, the gate raised, its lever pulled.
                    var away = door.parts.Root.InverseTransformPoint(shots[0].eye).z >= 0f ? 1 : -1;
                    PuzzleKit.Pose(door.parts, 1f, away, settings);
                    PuzzleKit.Pose(gateP.parts, 1f, 0, settings);
                    PuzzleKit.Pose(lever, 1f, 0, settings);
                }
                var shot = shots[k < 4 ? k : k == 4 ? 0 : 2];
                cam.transform.position = shot.eye;
                cam.transform.LookAt(shot.at);
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
                else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                sheet.ReadPixels(new Rect(0, 0, TileW, TileH), (k % 2) * TileW, (2 - k / 2) * TileH);
                RenderTexture.active = prev;
            }
            sheet.Apply();
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "preview-puzzles.png"));
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            Debug.Log($"[ShaderPreview] puzzles seed {seed} region {found}: {all.Count} fixtures; door {door.f.Cell}, gate {gateP.f.Cell}; wrote {path}");
            open.Dispose();
            ports.Dispose();
            g.Dispose();
        }

        /// <summary>From the open side of a door / gate, two cells back along the passage, eye height, looking at it.</summary>
        static (Vector3, Vector3) Approach(in HoldGrid g, HoldFixture f, PuzzleParts parts, NativeArray<bool> open, HoldMeshSettings s)
        {
            var along = f.Dir == 0 ? new int3(1, 0, 0) : new int3(0, 0, 1);
            var side = open[g.Index(f.Cell + along)] ? along : -along;
            var back = 1;
            while (back < 3 && g.InBounds(f.Cell + side * (back + 1)) && open[g.Index(f.Cell + side * (back + 1))]) back++;
            var eye = (Vector3)((new float3(g.ToWorld(f.Cell + side * back)) + new float3(0.5f, 0f, 0.5f)) * s.Scale) + Vector3.up * 1.6f;
            return (eye, parts.Point + Vector3.up * 0.1f);
        }

        /// <summary>Looking down at a point on the floor from 1.3 m up and 1.1 m off.</summary>
        static (Vector3, Vector3) Above(in HoldGrid g, Vector3 at, HoldMeshSettings s)
        {
            var c = (int3)math.floor((float3)at / s.Scale) - g.Origin;
            var off = Vector3.right;
            foreach (var d in new[] { new int3(1, 0, 0), new int3(-1, 0, 0), new int3(0, 0, 1), new int3(0, 0, -1) })
                if (HoldNav.Standable(g.Get(c + d))) { off = new Vector3(d.x, 0, d.z); break; }
            return (at + off * 1.1f + Vector3.up * 1.3f, at);
        }
    }
}
