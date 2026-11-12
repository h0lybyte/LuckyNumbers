using System.Collections.Generic;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.HoldScene
{
    /// <summary>One built puzzle fixture: its root, the part that moves, the point you work it from, what it owns.</summary>
    public sealed class PuzzleParts
    {
        public FixtureKind Kind;
        public Transform Root;
        /// <summary>Door: the hinge. Portcullis: the gate. Lever: the handle's pivot. Key: the key itself.</summary>
        public Transform Moving;
        public Transform Glint;
        public Collider Block;
        /// <summary>World point you work it from / look at (door: the lock; lever: its pivot; key: where it lies).</summary>
        public Vector3 Point;
        /// <summary>Unit vector along the passage (doors, gates) or out of the wall (levers), world xz.</summary>
        public Vector3 Facing;
        public readonly List<Mesh> Meshes = new();

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root.gameObject);
            foreach (var m in Meshes) if (m != null) Object.Destroy(m);
            Meshes.Clear();
        }
    }

    /// <summary>
    /// Builds the puzzles' fixtures (MinePasses.Puzzles) out of boxes in the HoldKit vertex format (position, normal,
    /// colour = light, uv0 = (u, v, slice)) with the one HoldKit material, lit like the mesh round them (ambient plus
    /// the cell's lanterns, baked into the vertex colours). Shared by PuzzlePresenter and the editor's render.
    /// - Locked door: a timber leaf (dlv_door3a) in the Door cell's frame, iron hinge straps, a lock plate (dlv_keys1's
    ///   gold rune), hinged on one jamb; a box collider until it swings open.
    /// - Key: a brass key on the floor (bow, shank, bit) with a lantern-coloured glint above it.
    /// - Portcullis: iron bars (dlv_metalstr2a) with spikes, filling the frame; slides up into the lintel.
    /// - Lever: an iron plate (dlv_metalpan3a) on the wall with a blue rune (dlv_keys1) and a handle that pulls down.
    /// Frame numbers follow HoldMesher's door frame (0.3 m jambs, 0.6 m headroom under the lintel).
    /// </summary>
    public static class PuzzleKit
    {
        public const float Jamb = 0.3f, Headroom = 0.6f;
        const float LeafThick = 0.1f;
        static readonly Color Brass = new(1f, 0.78f, 0.42f), Iron = new(0.78f, 0.8f, 0.85f), Timber = new(1f, 0.92f, 0.85f);

        /// <summary>Texture-array slices the kit uses (-1: missing; that face falls back to the next best).</summary>
        public struct Slices
        {
            public int Door, Keys, Strap, Panel, Wood, Metal;

            public static Slices From(HoldTextures t)
            {
                int L(string n) => HoldPalettes.Layer(t, n);
                var s = new Slices
                {
                    Door = L("Surfaces/dlv_door3a"), Keys = L("Surfaces/dlv_keys1"), Strap = L("Surfaces/dlv_metalstr2a"),
                    Panel = L("Surfaces/dlv_metalpan3a"), Wood = L("Surfaces/dlv_wood1a"), Metal = L("Surfaces/dlv_metaltrm1a"),
                };
                if (s.Metal < 0) s.Metal = s.Strap;
                if (s.Wood < 0) s.Wood = s.Door;
                return s;
            }
        }

        /// <summary>
        /// Ambient plus every lantern on this level within lantern_radius with a clear line (cells walkable along it),
        /// at a point 1.2 m over a cell's floor; boosted a little so the fixtures read against the rock.
        /// </summary>
        public static Color LightAt(in HoldGrid g, int3 local, HoldMeshSettings s)
        {
            var sum = new float3(s.Ambient);
            var scale = s.Scale;
            var at = (new float3(local) + new float3(0.5f, 0f, 0.5f)) * scale + new float3(0f, 1.2f, 0f);
            var r = (int)math.ceil(s.LanternRadius / s.CellSize) + 1;
            var r2 = s.LanternRadius * s.LanternRadius;
            for (var dz = -r; dz <= r; dz++)
            for (var dx = -r; dx <= r; dx++)
            {
                var c = local + new int3(dx, 0, dz);
                var cell = g.Get(c);
                if (!cell.Walkable || (cell.Flags & CellFlags.Lantern) == 0) continue;
                var lp = (new float3(c) + new float3(0.5f, 0f, 0.5f)) * scale + new float3(0f, s.StoreyHeight - 0.6f, 0f);
                var d2 = math.lengthsq(lp - at);
                if (d2 >= r2 || !ClearLine(g, local, c)) continue;
                var f = 1f - d2 / r2;
                sum += s.LanternColor * (f * f * 0.85f);
            }
            sum = math.min(sum * 1.25f + 0.04f, 1.2f);
            return new Color(sum.x, sum.y, sum.z, 1f);
        }

        static bool ClearLine(in HoldGrid g, int3 a, int3 b)
        {
            var from = new float2(a.x + 0.5f, a.z + 0.5f);
            var to = new float2(b.x + 0.5f, b.z + 0.5f);
            var steps = (int)math.ceil(math.distance(from, to) * 4f);
            for (var i = 1; i < steps; i++)
            {
                var p = math.lerp(from, to, (float)i / steps);
                if (!g.Get(new int3((int)math.floor(p.x), a.y, (int)math.floor(p.y))).Walkable) return false;
            }
            return true;
        }

        /// <summary>Build one fixture of grid g (LockedDoor, Key, Portcullis, Lever; null for anything else), in world space.</summary>
        public static PuzzleParts Build(in HoldGrid g, HoldFixture f, Material mat, Slices sl, HoldMeshSettings s, Color glint)
        {
            var light = LightAt(g, f.Cell, s);
            var world = g.ToWorld(f.Cell);
            var floor = (Vector3)((new float3(world) + new float3(0.5f, 0f, 0.5f)) * s.Scale);
            switch (f.Kind)
            {
                case FixtureKind.LockedDoor: return Door(floor, f.Dir, mat, sl, s, light);
                case FixtureKind.Portcullis: return Portcullis(floor, f.Dir, mat, sl, s, light);
                case FixtureKind.Lever: return Lever(floor, f.Dir, mat, sl, s, light);
                case FixtureKind.Key: return Key(floor, world, mat, sl, light, glint);
                default: return null;
            }
        }

        /// <summary>Door / gate frame: local z along the passage (Dir 0: along x, else along z), x across it.</summary>
        static Quaternion Across(byte dir) => dir == 0 ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;

        static PuzzleParts Door(Vector3 floor, byte dir, Material mat, Slices sl, HoldMeshSettings s, Color light)
        {
            var parts = new PuzzleParts { Kind = FixtureKind.LockedDoor };
            var width = s.CellSize - 2f * Jamb - 0.04f;
            var height = math.max(1f, s.StoreyHeight - Headroom) - 0.03f;
            var root = new GameObject("Puzzle locked door").transform;
            root.SetPositionAndRotation(floor, Across(dir));
            parts.Root = root;
            parts.Facing = root.forward;
            var hinge = new GameObject("Hinge").transform;
            hinge.SetParent(root, false);
            hinge.localPosition = new Vector3(-width * 0.5f, 0f, 0f);
            parts.Moving = hinge;

            var m = new KitMesh();
            var t = LeafThick * 0.5f;
            var timber = light * Timber;
            m.Box(new Vector3(0f, 0.02f, -t), new Vector3(width, height, t), sl.Door, timber, new Rect(0f, 0f, 1f, 1f), sl.Wood);
            // Iron hinge straps across both faces, and the lock plate (a gold rune) above the ring.
            var iron = light * Iron;
            foreach (var y in new[] { 0.42f, height - 0.5f })
                m.Box(new Vector3(-0.01f, y, -t - 0.015f), new Vector3(width * 0.55f, y + 0.09f, t + 0.015f), sl.Strap, iron * 0.7f);
            var lx = width - 0.3f;
            m.Box(new Vector3(lx, 1.28f, -t - 0.025f), new Vector3(lx + 0.22f, 1.56f, t + 0.025f), sl.Keys, light * 1.15f,
                new Rect(0.5f, 0.5f, 0.5f, 0.5f), sl.Metal);
            var leaf = Part("Leaf", hinge, m, mat, parts);
            var box = leaf.AddComponent<BoxCollider>();
            box.center = new Vector3(width * 0.5f, height * 0.5f, 0f);
            box.size = new Vector3(width, height, LeafThick + 0.04f);
            parts.Block = box;
            parts.Point = floor + Vector3.up * 1.2f;
            return parts;
        }

        static PuzzleParts Portcullis(Vector3 floor, byte dir, Material mat, Slices sl, HoldMeshSettings s, Color light)
        {
            var parts = new PuzzleParts { Kind = FixtureKind.Portcullis };
            var width = s.CellSize - 2f * Jamb;
            var height = math.max(1f, s.StoreyHeight - Headroom);
            var root = new GameObject("Puzzle portcullis").transform;
            root.SetPositionAndRotation(floor, Across(dir));
            parts.Root = root;
            parts.Facing = root.forward;
            var gate = new GameObject("Gate").transform;
            gate.SetParent(root, false);
            parts.Moving = gate;

            var m = new KitMesh();
            var iron = light * Iron;
            const int bars = 7;
            const float bar = 0.035f;
            for (var i = 0; i < bars; i++)
            {
                var x = -width * 0.5f + 0.1f + (width - 0.2f) * i / (bars - 1);
                m.Box(new Vector3(x - bar, 0.12f, -bar), new Vector3(x + bar, height + 0.4f, bar), sl.Strap, iron);
                m.Box(new Vector3(x - bar * 0.5f, 0f, -bar * 0.5f), new Vector3(x + bar * 0.5f, 0.12f, bar * 0.5f), sl.Metal, iron * 0.9f);   // spike
            }
            foreach (var y in new[] { 0.45f, 1.2f, 1.95f })
                m.Box(new Vector3(-width * 0.5f + 0.02f, y, -bar - 0.02f), new Vector3(width * 0.5f - 0.02f, y + 0.08f, bar + 0.02f), sl.Strap, iron * 0.85f);
            var grid = Part("Bars", gate, m, mat, parts);
            var box = grid.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, height * 0.5f, 0f);
            box.size = new Vector3(width, height, 0.16f);
            parts.Block = box;
            parts.Point = floor + Vector3.up * 1.2f;
            return parts;
        }

        static PuzzleParts Lever(Vector3 floor, byte dir, Material mat, Slices sl, HoldMeshSettings s, Color light)
        {
            var parts = new PuzzleParts { Kind = FixtureKind.Lever };
            var wall = (Vector3)(float3)MinePasses.PuzzleDirStep(dir);
            var root = new GameObject("Puzzle lever").transform;
            root.SetPositionAndRotation(floor + wall * (s.CellSize * 0.5f), Quaternion.LookRotation(-wall, Vector3.up));
            parts.Root = root;
            parts.Facing = -wall;

            var plate = new KitMesh();
            var iron = light * Iron;
            plate.Box(new Vector3(-0.17f, 0.92f, -0.01f), new Vector3(0.17f, 1.5f, 0.05f), sl.Panel, light, new Rect(0f, 0f, 0.5f, 0.5f), sl.Metal);
            plate.Box(new Vector3(-0.1f, 0.68f, -0.01f), new Vector3(0.1f, 0.88f, 0.035f), sl.Keys, light * 1.15f, new Rect(0f, 0.5f, 0.5f, 0.5f), sl.Metal);
            plate.Box(new Vector3(-0.06f, 1.15f, 0.05f), new Vector3(0.06f, 1.27f, 0.12f), sl.Metal, iron * 0.8f);   // the hub
            Part("Plate", root, plate, mat, parts);

            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(root, false);
            pivot.localPosition = new Vector3(0f, 1.21f, 0.09f);
            parts.Moving = pivot;
            var handle = new KitMesh();
            handle.Box(new Vector3(-0.022f, 0f, -0.022f), new Vector3(0.022f, 0.46f, 0.022f), sl.Metal, iron);
            handle.Box(new Vector3(-0.045f, 0.44f, -0.045f), new Vector3(0.045f, 0.56f, 0.045f), sl.Wood, light * Timber);   // grip
            Part("Handle", pivot, handle, mat, parts);
            parts.Point = pivot.position;
            return parts;
        }

        static PuzzleParts Key(Vector3 floor, int3 world, Material mat, Slices sl, Color light, Color glint)
        {
            var parts = new PuzzleParts { Kind = FixtureKind.Key };
            // Off-centre and turned by a hash of the cell, so keys don't all lie the same way.
            var h = MineGenerator.Hash(0x4B4559u, 0, world.x, world.y, world.z, 0);
            var offset = new Vector3(((h & 0xFF) / 255f - 0.5f) * 0.6f, 0.005f, (((h >> 8) & 0xFF) / 255f - 0.5f) * 0.6f);
            var root = new GameObject("Puzzle key").transform;
            root.SetPositionAndRotation(floor + offset, Quaternion.Euler(0f, (h >> 16) % 360, 0f));
            parts.Root = root;
            parts.Moving = root;
            parts.Facing = root.forward;

            var m = new KitMesh();
            var brass = light * Brass * 1.25f;
            const float th = 0.03f, ring = 0.15f, bow = 0.035f;
            // Bow: a square ring; shank; a two-toothed bit.
            m.Box(new Vector3(-ring, 0f, -ring * 0.5f), new Vector3(-ring + bow, th, ring * 0.5f), sl.Metal, brass);
            m.Box(new Vector3(-bow, 0f, -ring * 0.5f), new Vector3(0f, th, ring * 0.5f), sl.Metal, brass);
            m.Box(new Vector3(-ring, 0f, ring * 0.5f - bow), new Vector3(0f, th, ring * 0.5f), sl.Metal, brass);
            m.Box(new Vector3(-ring, 0f, -ring * 0.5f), new Vector3(0f, th, -ring * 0.5f + bow), sl.Metal, brass);
            m.Box(new Vector3(0f, 0.002f, -0.017f), new Vector3(0.26f, th - 0.002f, 0.017f), sl.Metal, brass);
            m.Box(new Vector3(0.19f, 0.002f, 0.017f), new Vector3(0.215f, th - 0.002f, 0.075f), sl.Metal, brass);
            m.Box(new Vector3(0.235f, 0.002f, 0.017f), new Vector3(0.26f, th - 0.002f, 0.06f), sl.Metal, brass);
            Part("Key", root, m, mat, parts);
            var g = Glint(glint, parts);
            g.SetParent(root, false);
            g.localPosition = new Vector3(0.04f, 0.3f, 0f);
            parts.Glint = g;
            parts.Point = root.position;
            return parts;
        }

        static Material _placeholder;

        /// <summary>A tiny unlit lantern-coloured cube (Resources/Placeholder, the material Placeholders.Block uses), no collider.</summary>
        static Transform Glint(Color color, PuzzleParts parts)
        {
            var go = new GameObject("PLACEHOLDER_key_glint");
            var cube = new KitMesh();
            cube.Box(Vector3.one * -0.5f, Vector3.one * 0.5f, 0, Color.white);
            var mesh = cube.ToMesh("glint");
            parts.Meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            _placeholder ??= Resources.Load<Material>("Placeholder");
            if (_placeholder != null) r.sharedMaterial = _placeholder;
            r.shadowCastingMode = ShadowCastingMode.Off;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            r.SetPropertyBlock(block);
            go.transform.localScale = Vector3.one * 0.07f;
            return go.transform;
        }

        static GameObject Part(string name, Transform parent, KitMesh m, Material mat, PuzzleParts parts)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = m.ToMesh(name);
            parts.Meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        /// <summary>
        /// Pose at t (0 shut / down / lying, 1 open / raised / pulled). Door: swings 90 degrees to `side` (+1: towards
        /// local -z). Gate: up into the lintel. Lever: handle from up-and-out to down-and-out. Eased.
        /// </summary>
        public static void Pose(PuzzleParts p, float t, int side, HoldMeshSettings s)
        {
            if (p?.Moving == null) return;
            var e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            switch (p.Kind)
            {
                case FixtureKind.LockedDoor:
                    p.Moving.localRotation = Quaternion.Euler(0f, 90f * e * (side >= 0 ? 1f : -1f), 0f);
                    break;
                case FixtureKind.Portcullis:
                    p.Moving.localPosition = new Vector3(0f, (math.max(1f, s.StoreyHeight - Headroom) - 0.18f) * e, 0f);
                    if (p.Block != null) p.Block.enabled = e < 0.6f;
                    break;
                case FixtureKind.Lever:
                    p.Moving.localRotation = Quaternion.Euler(Mathf.Lerp(28f, 150f, e), 0f, 0f);
                    break;
            }
        }

        /// <summary>Boxes in the HoldKit vertex format: position, normal, colour (light, shaded by face), uv0 (u, v, slice).</summary>
        sealed class KitMesh
        {
            readonly List<Vector3> _pos = new(), _nrm = new(), _uv = new();
            readonly List<Color32> _col = new();
            readonly List<int> _idx = new();

            /// <summary>
            /// An axis-aligned box. `fit`: the ±z faces show that rect of the slice (u, v in 0..1) stretched over them
            /// (the +z face mirrored so both read the same way round); other faces get `sideSlice` (or `slice`) at
            /// 1 tile per 2 m. Light is shaded by face: top 1, sides 0.82, bottom 0.6.
            /// </summary>
            public void Box(Vector3 min, Vector3 max, int slice, Color light, Rect? fit = null, int sideSlice = -1)
            {
                var side = sideSlice >= 0 ? sideSlice : slice;
                for (var axis = 0; axis < 3; axis++)
                for (var s = 0; s < 2; s++)
                {
                    var n = Vector3.zero;
                    n[axis] = s == 1 ? 1f : -1f;
                    int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                    var corners = new Vector3[4];
                    for (var k = 0; k < 4; k++)
                    {
                        var p = min;
                        p[axis] = s == 1 ? max[axis] : min[axis];
                        p[a1] = k == 1 || k == 2 ? max[a1] : min[a1];
                        p[a2] = k >= 2 ? max[a2] : min[a2];
                        corners[k] = p;
                    }
                    var shade = axis == 1 ? (s == 1 ? 1f : 0.6f) : 0.82f;
                    var c = light * shade;
                    var col = new Color32(B(c.r), B(c.g), B(c.b), 255);
                    var faceSlice = fit.HasValue && axis == 2 ? slice : side;
                    var b = _pos.Count;
                    for (var k = 0; k < 4; k++)
                    {
                        var p = corners[k];
                        Vector2 uv;
                        if (fit.HasValue && axis == 2)
                        {
                            var r = fit.Value;
                            var u = Mathf.InverseLerp(min.x, max.x, p.x);
                            if (s == 1) u = 1f - u;   // seen from +z, +x is on your left
                            uv = new Vector2(r.x + r.width * u, r.y + r.height * Mathf.InverseLerp(min.y, max.y, p.y));
                        }
                        else if (axis == 1) uv = new Vector2(p.x, p.z) * 0.5f;
                        else uv = new Vector2(axis == 0 ? p.z : p.x, p.y) * 0.5f;
                        _pos.Add(p);
                        _nrm.Add(n);
                        _col.Add(col);
                        _uv.Add(new Vector3(uv.x, uv.y, math.max(0, faceSlice)));
                    }
                    if (Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), n) >= 0f)
                    {
                        _idx.Add(b); _idx.Add(b + 1); _idx.Add(b + 2);
                        _idx.Add(b); _idx.Add(b + 2); _idx.Add(b + 3);
                    }
                    else
                    {
                        _idx.Add(b); _idx.Add(b + 2); _idx.Add(b + 1);
                        _idx.Add(b); _idx.Add(b + 3); _idx.Add(b + 2);
                    }
                }
            }

            static byte B(float v) => (byte)math.clamp(v * 255f + 0.5f, 0f, 255f);

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = $"Puzzle {name}" };
                mesh.SetVertices(_pos);
                mesh.SetNormals(_nrm);
                mesh.SetColors(_col);
                mesh.SetUVs(0, _uv);
                mesh.SetTriangles(_idx, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
