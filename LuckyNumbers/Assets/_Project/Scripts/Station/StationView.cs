using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Builds a station from its plan: kit pieces merged per chunk and kit, so a chunk is one mesh (one draw call)
    /// per kit however many pieces it holds; colliders are plain boxes from the plan. Static: nothing moves after
    /// Build (doors, animated pieces come later as their own objects).
    /// </summary>
    public sealed class StationView : MonoBehaviour
    {
        [Serializable]
        public struct KitPiece
        {
            public string name;
            public GameObject model;   // the kit's FBX (readable mesh, see ArtImport)
        }

        [SerializeField] Material stationMaterial;
        [SerializeField] Material starshipMaterial;
        [SerializeField] Material glassMaterial;   // StationGlass: window panes, so they look out at the real scene
        [SerializeField] KitPiece[] pieces = Array.Empty<KitPiece>();

        public StationPlan Plan { get; private set; }
        StationScene _config;

        static readonly int Ambient = Shader.PropertyToID("_Ambient");
        static readonly int LightColor = Shader.PropertyToID("_LightColor");
        static readonly int LightDesaturate = Shader.PropertyToID("_LightDesaturate");
        static readonly int Pulse = Shader.PropertyToID("_Pulse");

        /// <summary>Switch every light in the station (both kits' materials, so every chunk at once).</summary>
        public void SetLights(StationLightMode mode)
        {
            var look = mode switch
            {
                StationLightMode.Off => _config?.LightsOff,
                StationLightMode.Emergency => _config?.Emergency,
                _ => _config?.LightsOn,
            };
            if (look == null) return;
            foreach (var m in new[] { stationMaterial, starshipMaterial })
            {
                if (m == null) continue;
                m.SetColor(Ambient, ToColor(look.Ambient, Color.white));
                m.SetColor(LightColor, ToColor(look.Light, new Color(1, 1, 1, 1.15f)));
                m.SetFloat(LightDesaturate, look.Desaturate);
                m.SetVector(Pulse, new Vector4(look.PulseHz, look.PulseDepth, 0, 0));
            }
        }

        /// <summary>Unset in content (all zero) -> fallback.</summary>
        static Color ToColor(Rgba c, Color fallback) => c.Equals(default) ? fallback : new Color(c.R, c.G, c.B, c.A);

        public void Init(Material station, Material starship, Material glass, KitPiece[] kitPieces)
        {
            stationMaterial = station;
            starshipMaterial = starship;
            glassMaterial = glass;
            pieces = kitPieces;
        }

        public void Build(StationScene config)
        {
            Clear();
            _config = config;
            Plan = new StationPlan(config.Plan, config.LightEvery, config.CeilingLightEvery, config.TallRoomCells, config.TallLevels);
            var chunk = Math.Max(1, config.ChunkCells);

            // Each piece's meshes with their transform inside the model (FBX parts: a doorway has 3, a viewport 5).
            var parts = new Dictionary<string, List<(Mesh mesh, Matrix4x4 local)>>();
            foreach (var p in pieces)
            {
                if (p.model == null) continue;
                var root = p.model.transform.worldToLocalMatrix;
                var list = new List<(Mesh, Matrix4x4)>();
                foreach (var mf in p.model.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null) list.Add((mf.sharedMesh, root * mf.transform.localToWorldMatrix));
                parts[p.name] = list;
            }
            var lean = Mathf.Clamp01(config.RoomLean);
            foreach (var name in new List<string>(parts.Keys))
            {
                parts[name + StationPlan.Flat] = Flatten(parts[name], lean);
                parts[name + StationPlan.Upright] = Flatten(parts[name], 0f);
            }

            // Windows: the kits paint stars on their glass. Split those panes off (glassMaterial), so a window
            // is a real opening onto whatever is outside: the sky, the planet, the rest of the station.
            var panes = new Dictionary<string, List<(Mesh mesh, Matrix4x4 local)>>();
            var split = new Dictionary<Mesh, (Mesh frame, Mesh pane)>();
            if (glassMaterial != null)
                foreach (var name in new List<string>(parts.Keys))
                {
                    if (!name.Contains("porthole") && !name.Contains("viewport")) continue;
                    var solid = new List<(Mesh, Matrix4x4)>();
                    var glass = new List<(Mesh, Matrix4x4)>();
                    foreach (var (mesh, local) in parts[name])
                    {
                        if (!split.TryGetValue(mesh, out var s)) split[mesh] = s = SplitGlass(mesh);
                        solid.Add((s.frame, local));
                        if (s.pane != null) glass.Add((s.pane, local));
                    }
                    parts[name] = solid;
                    if (glass.Count > 0) panes[name] = glass;
                }

            var batches = new Dictionary<(Vector2Int chunk, StationPlan.Kit kit, bool glass), List<CombineInstance>>();
            var dark = new Dictionary<(Vector2Int, StationPlan.Kit, bool), List<bool>>();   // per CombineInstance: unlit?
            var missing = new HashSet<string>();
            void Add((Vector2Int, StationPlan.Kit, bool) key, List<(Mesh mesh, Matrix4x4 local)> meshes, Matrix4x4 world,
                     bool unlit)
            {
                if (!batches.TryGetValue(key, out var list))
                {
                    batches[key] = list = new List<CombineInstance>();
                    dark[key] = new List<bool>();
                }
                foreach (var (mesh, local) in meshes)
                    for (var s = 0; s < mesh.subMeshCount; s++)
                    {
                        list.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = world * local });
                        dark[key].Add(unlit);
                    }
            }
            foreach (var piece in Plan.Pieces)
            {
                if (!parts.TryGetValue(piece.Name, out var meshes)) { missing.Add(piece.Name); continue; }
                var c = new Vector2Int(piece.Cell.x / chunk, piece.Cell.y / chunk);
                var world = Matrix4x4.TRS(piece.Position, piece.Rotation, Vector3.one);
                Add((c, piece.Kit, false), meshes, world, piece.Unlit);
                if (panes.TryGetValue(piece.Name, out var glass)) Add((c, piece.Kit, true), glass, world, false);
            }
            if (missing.Count > 0) Debug.LogWarning($"[Station] no model for: {string.Join(", ", missing)}");

            // Corridor corners: one small mesh per hip, in world space, merged with its chunk.
            if (parts.TryGetValue(StationPlan.WallCorridor, out var corridorWall))
                foreach (var hip in Plan.Hips)
                {
                    var mesh = HipMesh(hip, corridorWall);
                    if (mesh == null) continue;
                    var c = new Vector2Int(hip.Cell.x / chunk, hip.Cell.y / chunk);
                    Add((c, StationPlan.Kit.Starship, false), new List<(Mesh, Matrix4x4)> { (mesh, Matrix4x4.identity) },
                        Matrix4x4.identity, false);
                }

            var triangles = 0;
            foreach (var ((c, kit, glass), list) in batches)
            {
                var mesh = new Mesh { name = $"Station {kit}{(glass ? " Glass" : "")} {c.x},{c.y}", indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(list.ToArray(), mergeSubMeshes: true, useMatrices: true);
                if (!glass) MarkUnlit(mesh, list, dark[(c, kit, glass)]);
                mesh.RecalculateBounds();
                mesh.UploadMeshData(markNoLongerReadable: true);
                triangles += (int)(mesh.GetIndexCount(0) / 3);
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = glass ? glassMaterial : kit == StationPlan.Kit.Station ? stationMaterial : starshipMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            var colliders = new GameObject("Colliders");
            colliders.transform.SetParent(transform, false);
            foreach (var box in Plan.Boxes)
            {
                var b = colliders.AddComponent<BoxCollider>();
                b.center = box.Center;
                b.size = box.Size;
            }
            SetLights(StationLightMode.On);
            Debug.Log($"[Station] {Plan.Width}x{Plan.Depth} cells, {Plan.Pieces.Count} pieces -> {batches.Count} meshes " +
                      $"({triangles} tris), {Plan.Boxes.Count} boxes");
        }

        /// <summary>
        /// A corridor wall for rooms: the part that leans in over head height (2.3 to 3 m, 0.6 m into the cell) is
        /// cut back towards the wall's face to <paramref name="keep"/> of its depth (0 = upright: the lean's texture,
        /// light strip and all, becomes the panel's top band). Other parts are shared as they are. Walls face local -Z.
        /// </summary>
        static List<(Mesh, Matrix4x4)> Flatten(List<(Mesh mesh, Matrix4x4 local)> source, float keep)
        {
            if (keep >= 0.999f) return source;
            var result = new List<(Mesh, Matrix4x4)>();
            foreach (var (mesh, local) in source)
            {
                var b = mesh.bounds;
                var lean = b.min.y >= 2.2f && b.size.z >= 0.4f;   // in the mesh's own space, before `local`
                if (!lean || !mesh.isReadable) { result.Add((mesh, local)); continue; }
                // Fully upright: drop the lean. The wall body's own face already runs up to 3 m behind it, and a lean
                // pushed flat onto that plane would z-fight with it (flickering bands at 2.3-3 m).
                if (keep <= 0.001f) continue;
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                // Which way is the front (into the cell)? The lean reaches furthest that way.
                var front = Mathf.Abs(b.min.z) > Mathf.Abs(b.max.z) ? -1f : 1f;
                var face = front < 0 ? -0.1f : 0.1f;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var v = vertices[i];
                    if (v.z * front > 0.1f) v.z = face + (v.z - face) * keep;
                    vertices[i] = v;
                    if (keep <= 0.001f && i < normals.Length) normals[i] = new Vector3(0, 0, front);
                }
                var flat = Instantiate(mesh);
                flat.name = mesh.name + StationPlan.Flat;
                flat.vertices = vertices;
                if (keep <= 0.001f) flat.normals = normals;
                else flat.RecalculateNormals();
                flat.RecalculateBounds();
                result.Add((flat, local));
            }
            return result;
        }

        /// <summary>
        /// The wedge at a convex corridor corner: each wall's lean (2.3 m up, 0.1 m out, to 3 m up, 0.6 m out)
        /// carried on past the corner up to the diagonal where the two leans meet. Texture and baked shading are
        /// sampled from the kit's own lean (mirrored along the wall), so the filler reads as the same panel.
        /// </summary>
        static Mesh HipMesh(StationPlan.Hip hip, List<(Mesh mesh, Matrix4x4 local)> wall)
        {
            Mesh lean = null;
            foreach (var (m, _) in wall)
                if (m.isReadable && m.bounds.min.y >= 2.2f && m.bounds.size.z >= 0.4f) lean = m;
            if (lean == null) return null;
            var lv = lean.vertices;
            var luv = lean.uv;
            var lcol = lean.colors;
            // Corners of the lean in its own space: x = +-1 along the wall, bottom (y 2.3) and top (y 3).
            int Nearest(float x, float y)
            {
                var best = 0;
                var bestD = float.MaxValue;
                for (var i = 0; i < lv.Length; i++)
                {
                    var d = (lv[i].x - x) * (lv[i].x - x) + (lv[i].y - y) * (lv[i].y - y);
                    if (d < bestD) { bestD = d; best = i; }
                }
                return best;
            }
            int b1 = Nearest(1f, 2.3f), b0 = Nearest(-1f, 2.3f), t1 = Nearest(1f, 3f), t0 = Nearest(-1f, 3f);
            Vector2 Uv(float s, float t) =>   // s metres past the corner (mirrored back along the wall), t 0..1 up the lean
                Vector2.Lerp(Vector2.Lerp(luv[b1], luv[b0], s / 2f), Vector2.Lerp(luv[t1], luv[t0], s / 2f), t);
            var colour = lcol.Length == lv.Length ? Color.Lerp(lcol[b1], lcol[t1], 0.5f) : Color.white;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var up = Vector3.up;
            void Side(Vector3 n, Vector3 along)
            {
                var v = hip.Vertex;
                var q = new[]
                {
                    v + n * 0.1f + up * 2.3f,                       // s 0,   t 0
                    v + n * 0.1f + along * 0.1f + up * 2.3f,        // s 0.1, t 0  (on the diagonal)
                    v + n * 0.6f + along * 0.6f + up * 3f,          // s 0.6, t 1  (on the diagonal)
                    v + n * 0.6f + up * 3f,                         // s 0,   t 1
                };
                var st = new[] { new Vector2(0f, 0f), new Vector2(0.1f, 0f), new Vector2(0.6f, 1f), new Vector2(0f, 1f) };
                var start = vertices.Count;
                for (var i = 0; i < 4; i++) { vertices.Add(q[i]); uvs.Add(Uv(st[i].x, st[i].y)); }
                // Face into the corridor (towards n, i.e. away from the solid cell).
                var flip = Vector3.Dot(Vector3.Cross(q[1] - q[0], q[3] - q[0]), n) < 0f;
                if (flip) tris.AddRange(new[] { start, start + 3, start + 2, start, start + 2, start + 1 });
                else tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            Side(hip.NormalA, hip.NormalB);
            Side(hip.NormalB, hip.NormalA);

            var mesh = new Mesh { name = "Station hip" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            var colours = new Color[vertices.Count];
            for (var i = 0; i < colours.Length; i++) colours[i] = colour;
            mesh.colors = colours;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// A window part -> its frame (everything else) and its pane: the triangles lying in the wall's centre plane
        /// (local z ~ 0; the frame stands proud at +-0.14 and wall faces sit at +-0.1). No pane: (mesh, null).
        /// </summary>
        static (Mesh frame, Mesh pane) SplitGlass(Mesh mesh)
        {
            if (!mesh.isReadable) return (mesh, null);
            var vertices = mesh.vertices;
            var frameTris = new List<int>();
            var paneTris = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (var t = 0; t < tris.Length; t += 3)
                {
                    var flat = Mathf.Abs(vertices[tris[t]].z) < 0.02f && Mathf.Abs(vertices[tris[t + 1]].z) < 0.02f &&
                               Mathf.Abs(vertices[tris[t + 2]].z) < 0.02f;
                    var target = flat ? paneTris : frameTris;
                    target.Add(tris[t]);
                    target.Add(tris[t + 1]);
                    target.Add(tris[t + 2]);
                }
            }
            if (paneTris.Count == 0) return (mesh, null);
            Mesh Copy(List<int> tris, string suffix)
            {
                var m = Instantiate(mesh);
                m.name = mesh.name + suffix;
                m.subMeshCount = 1;
                m.SetTriangles(tris, 0);
                return m;
            }
            return (Copy(frameTris, " frame"), Copy(paneTris, " pane"));
        }

        /// <summary>
        /// Vertex colour alpha = 1 for lit pieces, 0 for unlit (the shader keeps their painted lights dark).
        /// CombineMeshes appends each instance's vertices in order, so each instance owns a contiguous range.
        /// </summary>
        static void MarkUnlit(Mesh merged, List<CombineInstance> instances, List<bool> unlit)
        {
            if (!unlit.Contains(true)) return;
            var colors = merged.colors;
            if (colors.Length != merged.vertexCount) return;   // kit meshes all carry vertex colours
            var start = 0;
            for (var i = 0; i < instances.Count; i++)
            {
                var count = instances[i].mesh.vertexCount;   // whole mesh: sub-meshes share its vertex buffer
                if (unlit[i])
                    for (var v = start; v < start + count && v < colors.Length; v++) colors[v].a = 0f;
                start += count;
            }
            merged.colors = colors;
        }

        void Clear()
        {
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }
    }
}
