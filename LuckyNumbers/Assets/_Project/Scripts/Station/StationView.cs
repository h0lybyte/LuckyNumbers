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

        public void Init(Material station, Material starship, KitPiece[] kitPieces)
        {
            stationMaterial = station;
            starshipMaterial = starship;
            pieces = kitPieces;
        }

        public void Build(StationScene config)
        {
            Clear();
            _config = config;
            Plan = new StationPlan(config.Plan, config.LightEvery, config.CeilingLightEvery);
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
            foreach (var name in new List<string>(parts.Keys)) parts[name + StationPlan.Flat] = Flatten(parts[name], lean);

            var batches = new Dictionary<(Vector2Int chunk, StationPlan.Kit kit), List<CombineInstance>>();
            var dark = new Dictionary<(Vector2Int, StationPlan.Kit), List<bool>>();   // per CombineInstance: unlit?
            var missing = new HashSet<string>();
            foreach (var piece in Plan.Pieces)
            {
                if (!parts.TryGetValue(piece.Name, out var meshes)) { missing.Add(piece.Name); continue; }
                var key = (new Vector2Int(piece.Cell.x / chunk, piece.Cell.y / chunk), piece.Kit);
                if (!batches.TryGetValue(key, out var list))
                {
                    batches[key] = list = new List<CombineInstance>();
                    dark[key] = new List<bool>();
                }
                var world = Matrix4x4.TRS(piece.Position, piece.Rotation, Vector3.one);
                foreach (var (mesh, local) in meshes)
                    for (var s = 0; s < mesh.subMeshCount; s++)
                    {
                        list.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = world * local });
                        dark[key].Add(piece.Unlit);
                    }
            }
            if (missing.Count > 0) Debug.LogWarning($"[Station] no model for: {string.Join(", ", missing)}");

            var triangles = 0;
            foreach (var ((c, kit), list) in batches)
            {
                var mesh = new Mesh { name = $"Station {kit} {c.x},{c.y}", indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(list.ToArray(), mergeSubMeshes: true, useMatrices: true);
                MarkUnlit(mesh, list, dark[(c, kit)]);
                mesh.RecalculateBounds();
                mesh.UploadMeshData(markNoLongerReadable: true);
                triangles += (int)(mesh.GetIndexCount(0) / 3);
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = kit == StationPlan.Kit.Station ? stationMaterial : starshipMaterial;
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
