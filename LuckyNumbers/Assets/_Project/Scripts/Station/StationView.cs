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

        public void Init(Material station, Material starship, KitPiece[] kitPieces)
        {
            stationMaterial = station;
            starshipMaterial = starship;
            pieces = kitPieces;
        }

        public void Build(StationScene config)
        {
            Clear();
            Plan = new StationPlan(config.Plan, config.LightEvery);
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

            var batches = new Dictionary<(Vector2Int chunk, StationPlan.Kit kit), List<CombineInstance>>();
            var missing = new HashSet<string>();
            foreach (var piece in Plan.Pieces)
            {
                if (!parts.TryGetValue(piece.Name, out var meshes)) { missing.Add(piece.Name); continue; }
                var key = (new Vector2Int(piece.Cell.x / chunk, piece.Cell.y / chunk), piece.Kit);
                if (!batches.TryGetValue(key, out var list)) batches[key] = list = new List<CombineInstance>();
                var world = Matrix4x4.TRS(piece.Position, piece.Rotation, Vector3.one);
                foreach (var (mesh, local) in meshes)
                    for (var s = 0; s < mesh.subMeshCount; s++)
                        list.Add(new CombineInstance { mesh = mesh, subMeshIndex = s, transform = world * local });
            }
            if (missing.Count > 0) Debug.LogWarning($"[Station] no model for: {string.Join(", ", missing)}");

            var triangles = 0;
            foreach (var ((c, kit), list) in batches)
            {
                var mesh = new Mesh { name = $"Station {kit} {c.x},{c.y}", indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(list.ToArray(), mergeSubMeshes: true, useMatrices: true);
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
            Debug.Log($"[Station] {Plan.Width}x{Plan.Depth} cells, {Plan.Pieces.Count} pieces -> {batches.Count} meshes " +
                      $"({triangles} tris), {Plan.Boxes.Count} boxes");
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
