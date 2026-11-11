using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Builds a station from its plan. Draw calls are the budget: every kit piece, prop and door is merged per
    /// chunk into one mesh, all on one material (both kits share a combined atlas: station-kit UVs are shifted into
    /// its lower band), so a chunk costs one draw call however much it holds, plus one for its window glass.
    /// Doors are merged too: their leaves carry a door index and slide direction per vertex, and the StationKit
    /// shader slides them open from a global array (_LnDoorOpen) that UpdateDoors fills, so they add no draw calls.
    /// Colliders are plain boxes from the plan (doors: a box each, on while shut).
    /// </summary>
    public sealed class StationView : MonoBehaviour
    {
        [Serializable]
        public struct KitPiece
        {
            public string name;
            public GameObject model;   // the kit's FBX (readable mesh, see ArtImport)
            public int kit;            // 0 = station kit, 1 = starship kit (which atlas / material)
        }

        /// <summary>A door in a doorway: where it is, its box while shut, how open (0..1).</summary>
        sealed class DoorState
        {
            public Vector3 Position, Across;
            public BoxCollider Block;
            public float Open;
        }

        /// <summary>Per merged instance: lights kept dark; door leaf (index + 1, slide side, slide direction).</summary>
        struct Meta
        {
            public bool Unlit;
            public float Door, Side;
            public Vector2 Slide;
        }

        public const int MaxDoors = 64;   // matches _LnDoorOpen[64] in StationKit.shader
        /// <summary>Where the station kit's atlas sits in the combined one: its rows moved down by this (UV units).</summary>
        public const float StationAtlasOffset = 320f / 2048f;
        readonly List<DoorState> _doors = new();
        readonly float[] _doorOpen = new float[MaxDoors];
        /// <summary>Lids that sit flush on their body.</summary>
        const float LidLift = 0.006f;

        /// <summary>
        /// The kits model some lids (the cryo pod's) flush on their body: the lid's outer face lies in the same plane as
        /// the body's, and the two z-fight (a shimmering patch as you move). Lift each "*_lid" part a few millimetres
        /// out along its thin axis, away from the body, so the lid always wins (like the corner posts, b6c8d79).
        /// </summary>
        static void LiftLids(List<(Mesh mesh, Matrix4x4 local)> list, List<string> names)
        {
            if (list.Count < 2) return;
            var bodyCentre = Vector3.zero;
            foreach (var (mesh, local) in list) bodyCentre += local.MultiplyPoint3x4(mesh.bounds.center);
            bodyCentre /= list.Count;
            for (var i = 0; i < list.Count; i++)
            {
                if (!names[i].EndsWith("_lid", StringComparison.OrdinalIgnoreCase)) continue;
                var (mesh, local) = list[i];
                var size = mesh.bounds.size;
                var axis = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                // Out: the side of the lid facing away from the rest of the model.
                var lidCentre = local.MultiplyPoint3x4(mesh.bounds.center);
                var worldAxis = local.MultiplyVector(axis).normalized;
                var sign = Vector3.Dot(lidCentre - bodyCentre, worldAxis) >= 0f ? 1f : -1f;
                list[i] = (mesh, Matrix4x4.Translate(worldAxis * (sign * LidLift)) * local);
            }
        }

        static readonly int DoorOpen = Shader.PropertyToID("_LnDoorOpen");
        static readonly int Pools = Shader.PropertyToID("_Pools");
        static readonly int PoolPeriod = Shader.PropertyToID("_PoolPeriod");

        [SerializeField] Material material;        // StationKit on the combined atlas: every chunk
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
            if (material == null) return;
            material.SetColor(Ambient, ToColor(look.Ambient, Color.white));
            material.SetColor(LightColor, ToColor(look.Light, new Color(1, 1, 1, 1.15f)));
            material.SetFloat(LightDesaturate, look.Desaturate);
            material.SetVector(Pulse, new Vector4(look.PulseHz, look.PulseDepth, 0, 0));
        }

        /// <summary>Unset in content (all zero) -> fallback.</summary>
        static Color ToColor(Rgba c, Color fallback) => c.Equals(default) ? fallback : new Color(c.R, c.G, c.B, c.A);

        public void Init(Material kits, Material glass, KitPiece[] kitPieces)
        {
            material = kits;
            glassMaterial = glass;
            pieces = kitPieces;
        }

        /// <param name="layout">Plan, props and consoles (default: content's; or seeded, or the page's editor).</param>
        public void Build(StationScene config, StationLayout layout = null)
        {
            Clear();
            _config = config;
            layout ??= StationLayout.FromContent(config);
            Plan = new StationPlan(layout.Plan, config.LightEvery, config.CeilingLightEvery, config.TallRoomCells, config.TallLevels,
                                   config.CorridorHandrails);
            var chunk = Math.Max(1, config.ChunkCells);

            // Each piece's meshes with their transform inside the model (FBX parts: a doorway has 3, a viewport 5).
            var parts = new Dictionary<string, List<(Mesh mesh, Matrix4x4 local)>>();
            var separated = new Dictionary<Mesh, Mesh>();
            foreach (var p in pieces)
            {
                if (p.model == null) continue;
                var root = p.model.transform.worldToLocalMatrix;
                var list = new List<(Mesh, Matrix4x4)>();
                var names = new List<string>();
                foreach (var mf in p.model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    // Flush panels in one mesh (the cryo pod's skin) z-fight: lift them off the face under them.
                    if (!separated.TryGetValue(mf.sharedMesh, out var mesh))
                    {
                        separated[mf.sharedMesh] = mesh = CoplanarFix.Separate(mf.sharedMesh, out var lifted);
                        if (lifted > 0) Debug.Log($"[Station] {p.name}/{mf.name}: lifted {lifted} flush triangles");
                    }
                    list.Add((mesh, root * mf.transform.localToWorldMatrix));
                    names.Add(mf.name);
                }
                LiftLids(list, names);   // and lids, a separate mesh flush on their body
                parts[p.name] = list;
            }
            var lean = Mathf.Clamp01(config.RoomLean);
            foreach (var name in new List<string>(parts.Keys))
            {
                parts[name + StationPlan.Flat] = Flatten(parts[name], lean);
                parts[name + StationPlan.Upright] = Flatten(parts[name], 0f);
            }

            // Props and console models: placed by plan coordinates, merged with their kit's chunks like the walls.
            var kitOf = new Dictionary<string, int>();
            foreach (var p in pieces) kitOf[p.name] = p.kit;
            var solids = new List<(string name, Vector3 position, Quaternion rotation)>();
            var missingProps = new HashSet<string>();
            void Place(string piece, float col, float row, float yaw, bool solid)
            {
                if (string.IsNullOrEmpty(piece)) return;
                if (!kitOf.TryGetValue(piece, out var k)) { missingProps.Add(piece); return; }
                var position = Plan.PlanToWorld(col, row);
                var rotation = Quaternion.Euler(0f, yaw + 180f, 0f);   // the kits' fronts face -Z: yaw 0 = facing north
                Plan.Add(piece, k == 0 ? StationPlan.Kit.Station : StationPlan.Kit.Starship, position, rotation);
                if (solid) solids.Add((piece, position, rotation));
            }
            foreach (var prop in layout.Props) Place(prop.Piece, prop.Col, prop.Row, prop.Yaw, prop.Solid);
            foreach (var sw in layout.Switches) Place(sw.Piece, sw.Col, sw.Row, sw.Yaw, true);
            if (missingProps.Count > 0) Debug.LogWarning($"[Station] no model for props: {string.Join(", ", missingProps)}");

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

            // Station-kit meshes, with their UVs moved into the combined atlas's lower band (cached per mesh).
            var shifted = new Dictionary<Mesh, Mesh>();
            Mesh InAtlas(Mesh mesh, StationPlan.Kit kit)
            {
                if (kit != StationPlan.Kit.Station || !mesh.isReadable) return mesh;
                if (shifted.TryGetValue(mesh, out var m)) return m;
                m = Instantiate(mesh);
                m.name = mesh.name + " (atlas)";
                var uv = m.uv;
                for (var i = 0; i < uv.Length; i++) uv[i].y -= StationAtlasOffset;
                m.uv = uv;
                return shifted[mesh] = m;
            }

            var batches = new Dictionary<(Vector2Int chunk, bool glass), List<CombineInstance>>();
            var metas = new Dictionary<(Vector2Int, bool), List<Meta>>();
            var missing = new HashSet<string>();
            void Add((Vector2Int, bool) key, List<(Mesh mesh, Matrix4x4 local)> meshes, Matrix4x4 world, StationPlan.Kit kit,
                     Meta meta)
            {
                if (!batches.TryGetValue(key, out var list))
                {
                    batches[key] = list = new List<CombineInstance>();
                    metas[key] = new List<Meta>();
                }
                foreach (var (mesh, local) in meshes)
                {
                    var m = InAtlas(mesh, kit);
                    for (var s = 0; s < m.subMeshCount; s++)
                    {
                        list.Add(new CombineInstance { mesh = m, subMeshIndex = s, transform = world * local });
                        metas[key].Add(meta);
                    }
                }
            }
            _doors.Clear();
            parts.TryGetValue(StationPlan.Door, out var doorParts);
            foreach (var piece in Plan.Pieces)
            {
                if (!parts.TryGetValue(piece.Name, out var meshes)) { missing.Add(piece.Name); continue; }
                var c = new Vector2Int(piece.Cell.x / chunk, piece.Cell.y / chunk);
                var world = Matrix4x4.TRS(piece.Position, piece.Rotation, piece.Scale);
                Add((c, false), meshes, world, piece.Kit, new Meta { Unlit = piece.Unlit });
                // Glass: one mesh for the whole station (cheap to draw, not worth culling per chunk): one draw call.
                if (panes.TryGetValue(piece.Name, out var glass)) Add((Vector2Int.zero, true), glass, world, piece.Kit, default);
                if (piece.Name == StationPlan.DoorwayShip && doorParts != null && _doors.Count < MaxDoors)
                    AddDoor(piece, c, world, doorParts, Add);
            }
            if (missing.Count > 0) Debug.LogWarning($"[Station] no model for: {string.Join(", ", missing)}");

            // Corridor corners: one small mesh per hip, in world space, merged with its chunk.
            if (parts.TryGetValue(StationPlan.WallCorridor, out var corridorWall))
                foreach (var hip in Plan.Hips)
                {
                    var mesh = HipMesh(hip, corridorWall);
                    if (mesh == null) continue;
                    var c = new Vector2Int(hip.Cell.x / chunk, hip.Cell.y / chunk);
                    Add((c, false), new List<(Mesh, Matrix4x4)> { (mesh, Matrix4x4.identity) }, Matrix4x4.identity,
                        StationPlan.Kit.Starship, default);
                }

            var triangles = 0;
            foreach (var ((c, glass), list) in batches)
            {
                var mesh = new Mesh { name = $"Station{(glass ? " Glass" : "")} {c.x},{c.y}", indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(list.ToArray(), mergeSubMeshes: true, useMatrices: true);
                if (!glass) WriteStreams(mesh, list, metas[(c, glass)]);
                mesh.RecalculateBounds();
                if (!glass)
                {
                    var b = mesh.bounds;
                    b.Expand(new Vector3(1.4f, 0f, 1.4f));   // door leaves slide up to 0.6 m out of their closed pose
                    mesh.bounds = b;
                }
                mesh.UploadMeshData(markNoLongerReadable: true);
                triangles += (int)(mesh.GetIndexCount(0) / 3);
                var go = new GameObject(mesh.name);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = glass ? glassMaterial : material;
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
            // Solid props: a box from the model's bounds, turned with it.
            foreach (var (name, position, rotation) in solids)
            {
                if (!parts.TryGetValue(name, out var meshes) || meshes.Count == 0) continue;
                var bounds = ModelBounds(meshes);
                var go = new GameObject(name + " collider");
                go.transform.SetParent(colliders.transform, false);
                go.transform.SetPositionAndRotation(position, rotation);
                var b = go.AddComponent<BoxCollider>();
                b.center = bounds.center;
                b.size = bounds.size;
            }
            foreach (var d in _doors)
            {
                d.Block = colliders.AddComponent<BoxCollider>();
                d.Block.center = d.Position + Vector3.up * 1.1f;
                d.Block.size = DoorBox(d);
            }
            Array.Clear(_doorOpen, 0, _doorOpen.Length);
            Shader.SetGlobalFloatArray(DoorOpen, _doorOpen);
            SetPools(config.LightPools, StationPlan.Cell * 2f * Mathf.Max(1, config.CeilingLightEvery));
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

        static Bounds ModelBounds(List<(Mesh mesh, Matrix4x4 local)> meshes)
        {
            var first = true;
            var bounds = new Bounds();
            foreach (var (mesh, local) in meshes)
            {
                var b = mesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = local.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (first) { bounds = new Bounds(corner, Vector3.zero); first = false; }
                    else bounds.Encapsulate(corner);
                }
            }
            return bounds;
        }

        /// <summary>
        /// A door (door_ship) merged into its doorway's chunk: the frame stays put, each leaf is tagged with the door's
        /// index, which way it slides (away from the middle, into the wall) and that direction in the world.
        /// </summary>
        void AddDoor(StationPlan.Piece piece, Vector2Int chunk, Matrix4x4 world, List<(Mesh mesh, Matrix4x4 local)> door,
                     Action<(Vector2Int, bool), List<(Mesh, Matrix4x4)>, Matrix4x4, StationPlan.Kit, Meta> add)
        {
            var index = _doors.Count;
            var across = piece.Rotation * Vector3.right;   // the door's own X: the leaves slide along it
            foreach (var (mesh, local) in door)
            {
                var x = local.MultiplyPoint3x4(mesh.bounds.center).x;
                var meta = Mathf.Abs(x) < 0.1f
                    ? default
                    : new Meta { Door = index + 1, Side = Mathf.Sign(x), Slide = new Vector2(across.x, across.z) };
                add((chunk, false), new List<(Mesh, Matrix4x4)> { (mesh, local) }, world, StationPlan.Kit.Starship, meta);
            }
            _doors.Add(new DoorState { Position = piece.Position, Across = across });
        }

        static Vector3 DoorBox(DoorState d)
        {
            // 1.2 m wide across the doorway, 2.2 m high, 0.2 m deep along the way through.
            var a = new Vector3(Mathf.Abs(d.Across.x), 0f, Mathf.Abs(d.Across.z));
            var along = new Vector3(a.z, 0f, a.x);
            var s = a * 1.2f + along * 0.2f;
            return new Vector3(s.x, 2.2f, s.z);
        }

        /// <summary>Doors slide open when <paramref name="player"/> is within <paramref name="distance"/> metres.</summary>
        public void UpdateDoors(Vector3 player, float distance, float dt)
        {
            var changed = false;
            for (var i = 0; i < _doors.Count; i++)
            {
                var d = _doors[i];
                var near = Vector2.Distance(new Vector2(player.x, player.z), new Vector2(d.Position.x, d.Position.z)) < distance;
                var open = Mathf.MoveTowards(d.Open, near ? 1f : 0f, dt / 0.45f);
                if (Mathf.Approximately(open, d.Open)) continue;
                d.Open = open;
                _doorOpen[i] = open * open * (3f - 2f * open);   // eased, read by the shader
                if (d.Block != null) d.Block.enabled = open < 0.6f;
                changed = true;
            }
            if (changed) Shader.SetGlobalFloatArray(DoorOpen, _doorOpen);
        }

        /// <summary>
        /// After merging: vertex colour alpha 0 for unlit pieces (their lamps stay dark), uv2 = (door index + 1, slide
        /// side) and uv3 = slide direction for door leaves (zero elsewhere). CombineMeshes appends each instance's
        /// vertices in order, so each instance owns a contiguous range.
        /// </summary>
        static void WriteStreams(Mesh merged, List<CombineInstance> instances, List<Meta> metas)
        {
            var n = merged.vertexCount;
            var colors = merged.colors;
            var hasColors = colors.Length == n;
            var door = new Vector2[n];
            var slide = new Vector2[n];
            var start = 0;
            for (var i = 0; i < instances.Count; i++)
            {
                var count = instances[i].mesh.vertexCount;   // whole mesh: sub-meshes share its vertex buffer
                var m = metas[i];
                for (var v = start; v < start + count && v < n; v++)
                {
                    if (m.Unlit && hasColors) colors[v].a = 0f;
                    door[v] = new Vector2(m.Door, m.Side);
                    slide[v] = m.Slide;
                }
                start += count;
            }
            if (hasColors) merged.colors = colors;
            merged.SetUVs(1, door);
            merged.SetUVs(2, slide);
        }

        /// <summary>Light pools: surfaces a little brighter under the room ceilings' lit rows (every <paramref name="period"/> m).</summary>
        void SetPools(float amount, float period)
        {
            if (material == null) return;
            material.SetFloat(Pools, Mathf.Clamp01(amount));
            material.SetFloat(PoolPeriod, period);
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
