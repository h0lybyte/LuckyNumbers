using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// The Depth pass's kit in the chunk meshes (docs/holds.md, "Depth"): rubble heaps against a collapse from the
    /// cells beside it, a broken timber rail round a drop shaft's lip, a winch beam over a sealed shaft. The moving
    /// parts (seal hatch, warning plate, key) are DepthKit meshes built by the scene's DepthPresenter.
    /// </summary>
    partial struct ChunkMesher
    {
        partial void KitDepth(int3 c, HoldCell cell, PaletteLayers p, bool floor, bool ceiling)
        {
            for (var i = 0; i < 4; i++)
                if (G.Get(c + Dir(i)).Kind == CellKind.Rubble) RubbleHeap(c, Dir(i), p);
            if (DepthCells.IsDropLip(cell)) DropRail(c, p);
            if (DepthCells.IsSealed(cell)) SealBeam(c, p);
        }

        static uint DepthMix(int3 w, int salt)
        {
            var h = (uint)w.x * 0x9E3779B1u ^ (uint)w.y * 0x85EBCA77u ^ (uint)w.z * 0xC2B2AE3Du ^ (uint)salt * 0x27D4EB2Fu;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }

        static float Unit(uint h, int k) => ((h >> (k * 4)) & 15u) / 15f;

        /// <summary>
        /// A box in cell c's frame against side d: t from the side's face into the cell (metres), a along it (0..1 of
        /// the cell), y up from the floor (metres). Skips the face against the side and, at the floor or ceiling, those.
        /// </summary>
        void SideBox(int3 c, int3 d, float t0, float t1, float a0, float a1, float y0, float y1, int slice, bool collide, bool back = true,
                     int top = -1)
        {
            int across = d.x != 0 ? 0 : 2, along = d.x != 0 ? 2 : 0;
            var s = d[across];
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var face = s > 0 ? hi[across] : lo[across];
            var min = float3.zero; var max = float3.zero;
            min[across] = math.min(face - s * t0, face - s * t1);
            max[across] = math.max(face - s * t0, face - s * t1);
            min[along] = math.lerp(lo[along], hi[along], a0);
            max[along] = math.lerp(lo[along], hi[along], a1);
            min.y = lo.y + y0;
            max.y = lo.y + math.min(y1, S.StoreyHeight);
            var skip = (back && t0 <= 0f ? 1 << (across * 2 + (s > 0 ? 1 : 0)) : 0) | (y0 <= 0f ? 1 << 2 : 0) | (y1 >= S.StoreyHeight ? 1 << 3 : 0);
            if (top < 0) Box(min, max, slice, skip, false);
            else ShadedBox(min, max, slice, top, skip, lo.y);
            if (collide) B.Add(new HoldBox { Min = min, Max = max });
        }

        /// <summary>Box with its own top slice, darkened towards the floor (stones heaped on stones).</summary>
        void ShadedBox(float3 min, float3 max, int sides, int top, int skip, float floorY)
        {
            for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side < 2; side++)
            {
                if ((skip & (1 << (axis * 2 + side))) != 0) continue;
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                var corners = new float3x4();
                var ao = float4.zero;
                for (var k = 0; k < 4; k++)
                {
                    var q = min;
                    q[axis] = side == 1 ? max[axis] : min[axis];
                    q[a1] = k == 1 || k == 2 ? max[a1] : min[a1];
                    q[a2] = k >= 2 ? max[a2] : min[a2];
                    corners[k] = q;
                    ao[k] = 0.68f + 0.32f * math.saturate((q.y - floorY) / 1.8f);
                }
                var n = float3.zero;
                n[axis] = side == 1 ? 1f : -1f;
                Quad(corners, n, axis == 1 && side == 1 ? top : sides, ao, false);
            }
        }

        /// <summary>
        /// Tumbled stone against a collapse: a jagged stepped heap (wall-stone faces, gravel tops, darker low down),
        /// boulders on its shoulders, loose stones out on the floor, a snapped timber prop. The lower steps collide.
        /// </summary>
        void RubbleHeap(int3 c, int3 d, PaletteLayers p)
        {
            var stone = Slice(p.Wall);
            var gravel = Slice(p.Ceiling);
            var timber = Slice(p.Pillar);
            var w = G.ToWorld(c);
            var h = DepthMix(w, d.x * 3 + d.z * 7 + 11);
            var h2 = DepthMix(w, d.x * 5 + d.z * 13 + 29);
            var h3 = DepthMix(w, d.x * 11 + d.z * 17 + 53);
            var storey = S.StoreyHeight;
            // Steps, deep and low at the foot to shallow and high against the fall, each split into uneven blocks.
            float b1 = 0.28f + Unit(h3, 0) * 0.12f, b2 = 0.6f + Unit(h3, 1) * 0.14f;
            SideBox(c, d, 0f, 0.8f + Unit(h, 0) * 0.35f, 0.0f, b1, 0f, 0.32f + Unit(h, 1) * 0.2f, stone, true, true, gravel);
            SideBox(c, d, 0f, 0.95f + Unit(h, 2) * 0.3f, b1, b2, 0f, 0.4f + Unit(h, 3) * 0.22f, stone, true, true, gravel);
            SideBox(c, d, 0f, 0.75f + Unit(h, 4) * 0.35f, b2, 1f, 0f, 0.3f + Unit(h, 5) * 0.25f, stone, true, true, gravel);
            float c1 = 0.35f + Unit(h3, 2) * 0.2f;
            SideBox(c, d, 0f, 0.5f + Unit(h, 6) * 0.2f, 0.03f + Unit(h3, 3) * 0.08f, c1, 0.25f, 0.95f + Unit(h, 7) * 0.35f, stone, true, true, gravel);
            SideBox(c, d, 0f, 0.58f + Unit(h2, 0) * 0.2f, c1, 0.92f + Unit(h3, 4) * 0.08f, 0.25f, 1.1f + Unit(h2, 1) * 0.3f, stone, true, true, gravel);
            float e1 = 0.4f + Unit(h3, 5) * 0.25f;
            SideBox(c, d, 0f, 0.3f + Unit(h2, 2) * 0.12f, 0.1f + Unit(h3, 6) * 0.1f, e1, 0.9f, 1.75f + Unit(h2, 3) * 0.4f, stone, false, true, gravel);
            SideBox(c, d, 0f, 0.24f + Unit(h2, 4) * 0.14f, e1, 0.85f + Unit(h3, 7) * 0.12f, 0.9f, 2.05f + Unit(h2, 5) * 0.35f, stone, false, true, gravel);
            SideBox(c, d, 0f, 0.12f + Unit(h2, 6) * 0.08f, 0f, 1f, 1.7f, storey, stone, false, true, gravel);
            // Boulders on the heap's shoulders.
            for (var k = 0; k < 3; k++)
            {
                var size = 0.24f + Unit(h2, (3 + k) & 7) * 0.22f;
                var a = 0.08f + k * 0.3f + Unit(h3, k) * 0.1f;
                var t = 0.35f + Unit(h, (3 + k) & 7) * 0.3f;
                var y = k == 1 ? 1.0f : 0.45f;
                SideBox(c, d, t, t + size, a, a + size / S.CellSize, y, y + size * 0.8f, stone, false, false, gravel);
            }
            // Loose stones out on the floor.
            for (var k = 0; k < 5; k++)
            {
                var size = 0.1f + Unit(DepthMix(w, k + 40 + d.x + d.z * 2), 0) * 0.18f;
                var a = 0.06f + k * 0.18f + Unit(h2, k) * 0.07f;
                var t = 1.05f + Unit(h, (k + 2) & 7) * 0.5f;
                SideBox(c, d, t, t + size, a, a + size / S.CellSize, 0f, size * 0.7f, stone, false, false, gravel);
            }
            // A snapped timber prop: one half lying across the heap, its stump still standing to the side.
            var left = (h & 1u) == 0u;
            SideBox(c, d, 0.42f, 0.6f, left ? 0.02f : 0.32f, left ? 0.68f : 0.98f, 1.12f, 1.3f, timber, false, false);
            SideBox(c, d, 0.9f, 1.06f, left ? 0.04f : 0.88f, left ? 0.12f : 0.96f, 0f, 0.8f + Unit(h2, 7) * 0.5f, timber, false, false);
        }

        /// <summary>
        /// A broken timber rail on every side you can walk up to a drop's lip from: posts on the near floor's edge, a top
        /// and middle rail, each side broken its own way (hash of the cell): snapped and sagging, a post sheared off
        /// with its plank on the floor, or a plank hanging into the drop.
        /// </summary>
        void DropRail(int3 c, PaletteLayers p)
        {
            var post = Slice(p.Pillar);
            var plank = Slice(p.Trim);
            for (var i = 0; i < 4; i++)
            {
                var d = Dir(i);
                var nb = G.Get(c + d);
                if (!HoldRules.StepOff(nb)) continue;
                var how = DepthMix(G.ToWorld(c), i + 101) % 3u;
                // From the neighbour's side (t < 0 here is over the drop): posts just on its floor.
                const float p0 = -0.13f, p1 = -0.01f;
                var tallR = how == 1u ? 0.42f : 1.12f;
                SideBox(c, d, p0, p1, 0.05f, 0.11f, 0f, 1.12f, post, true, false);
                SideBox(c, d, p0, p1, 0.89f, 0.95f, 0f, tallR, post, how != 1u, false);
                SideBox(c, d, -0.16f, 0f, 0.02f, 0.98f, 0f, 0.07f, plank, false, false);   // sill: where the floor ends
                const float r0 = -0.19f, r1 = -0.13f;   // rails on the posts' far face, towards the walker
                switch (how)
                {
                    case 0u:   // top rail snapped: one stub level, the other sagged down; middle rail whole
                        SideBox(c, d, r0, r1, 0.08f, 0.44f, 0.96f, 1.08f, plank, false, false);
                        SideBox(c, d, r0, r1, 0.6f, 0.92f, 0.74f, 0.86f, plank, false, false);
                        SideBox(c, d, r0, r1, 0.08f, 0.92f, 0.5f, 0.6f, plank, false, false);
                        break;
                    case 1u:   // the far post sheared: a stub of rail, the rest on the floor
                        SideBox(c, d, r0, r1, 0.08f, 0.52f, 0.96f, 1.08f, plank, false, false);
                        SideBox(c, d, r0 - 0.3f, r1 - 0.3f + 0.08f, 0.3f, 0.86f, 0f, 0.05f, plank, false, false);
                        break;
                    default:   // middle rail gone, one plank hanging off the top rail into the drop
                        SideBox(c, d, r0, r1, 0.08f, 0.92f, 0.96f, 1.08f, plank, false, false);
                        SideBox(c, d, 0.0f, 0.05f, 0.42f, 0.48f, 0.05f, 0.98f, plank, false, false);
                        break;
                }
            }
        }

        /// <summary>
        /// Over a sealed shaft: a winch beam under the ceiling across the cell (bedded in the rock, or on a post on the
        /// floor where that end is open) with a pulley block; the hatch itself hangs from it (DepthKit.SealHatch).
        /// </summary>
        void SealBeam(int3 c, PaletteLayers p)
        {
            var timber = Slice(p.Pillar);
            var trim = Slice(p.Trim);
            // Along the axis whose ends are rock, if either is.
            var wallsX = (HoldRules.HasWall(G, c, new int3(1, 0, 0)) ? 1 : 0) + (HoldRules.HasWall(G, c, new int3(-1, 0, 0)) ? 1 : 0);
            var wallsZ = (HoldRules.HasWall(G, c, new int3(0, 0, 1)) ? 1 : 0) + (HoldRules.HasWall(G, c, new int3(0, 0, -1)) ? 1 : 0);
            var alongX = wallsX >= wallsZ;
            var side = alongX ? new int3(0, 0, 1) : new int3(1, 0, 0);   // SideBox's "side": beam runs along its a axis
            var top = S.StoreyHeight;
            SideBox(c, side, S.CellSize * 0.5f - 0.11f, S.CellSize * 0.5f + 0.11f, 0f, 1f, top - 0.3f, top, timber, false, false);
            SideBox(c, side, S.CellSize * 0.5f - 0.13f, S.CellSize * 0.5f + 0.13f, 0.44f, 0.56f, top - 0.5f, top - 0.3f, trim, false, false);
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var e = alongX ? new int3(sign, 0, 0) : new int3(0, 0, sign);
                if (HoldRules.HasWall(G, c, e)) continue;
                // A timber frame on the open end (posts in its corners, a lintel under the ceiling) carrying the beam:
                // the way in stays clear.
                SideBox(c, e, -0.18f, -0.02f, 0f, 0.07f, 0f, top - 0.3f, timber, true, false);
                SideBox(c, e, -0.18f, -0.02f, 0.93f, 1f, 0f, top - 0.3f, timber, true, false);
                SideBox(c, e, -0.2f, 0f, 0f, 1f, top - 0.3f, top, timber, false, false);
            }
        }
    }

    /// <summary>
    /// Kit meshes built at runtime (the Depth pieces that move or come and go): boxes in the hold's vertex layout
    /// (HoldMesher.Layout) with a texture-array slice each and one flat light value, for the HoldKit material.
    /// </summary>
    public sealed class DepthKit
    {
        readonly List<HoldVertex> _v = new();
        readonly List<int> _i = new();
        readonly float _cell;
        readonly Color32 _light;

        public DepthKit(float cellSize, float light)
        {
            _cell = math.max(0.1f, cellSize);
            var b = (byte)math.clamp(light * 255f, 0f, 255f);
            _light = new Color32(b, (byte)(b * 0.93f), (byte)(b * 0.85f), 255);
        }

        /// <summary>
        /// An axis-aligned box (local metres), outward faces except `skip` (bit axis * 2 + positive side). `fit`: each
        /// face shows its slice once, edge to edge; else tiled one per cell_size metres.
        /// </summary>
        public void Box(float3 min, float3 max, int slice, int skip = 0, bool fit = false) => Box(min, max, slice, skip, fit, new float4(0f, 0f, 1f, 1f));

        /// <summary>As Box, `fit` faces showing only the uv rectangle (u0, v0, u1, v1) of their slice.</summary>
        public void Box(float3 min, float3 max, int slice, int skip, bool fit, float4 rect)
        {
            for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side < 2; side++)
            {
                if ((skip & (1 << (axis * 2 + side))) != 0) continue;
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                var n = float3.zero;
                n[axis] = side == 1 ? 1f : -1f;
                var b = _v.Count;
                for (var k = 0; k < 4; k++)
                {
                    var q = min;
                    q[axis] = side == 1 ? max[axis] : min[axis];
                    q[a1] = k == 1 || k == 2 ? max[a1] : min[a1];
                    q[a2] = k >= 2 ? max[a2] : min[a2];
                    float2 uv;
                    if (fit)
                    {
                        // Horizontal faces: u along x, v along z; vertical: u along the face, v up.
                        var ua = axis == 1 ? 0 : axis == 0 ? 2 : 0;
                        var va = axis == 1 ? 2 : 1;
                        var u = (q[ua] - min[ua]) / math.max(1e-4f, max[ua] - min[ua]);
                        var v = (q[va] - min[va]) / math.max(1e-4f, max[va] - min[va]);
                        if (axis == 0 && side == 0 || axis == 2 && side == 1) u = 1f - u;
                        uv = math.lerp(rect.xy, rect.zw, new float2(u, v));
                    }
                    else if (axis == 1) uv = new float2(q.x, side == 1 ? q.z : -q.z) / _cell;
                    else
                    {
                        var right = math.cross(new float3(0f, 1f, 0f), -n);
                        uv = new float2(math.dot(q, right), q.y) / _cell;
                    }
                    _v.Add(new HoldVertex { Position = q, Normal = n, Color = _light, Uv = new float3(uv, slice) });
                }
                var p0 = _v[b].Position; var p1 = _v[b + 1].Position; var p2 = _v[b + 2].Position;
                if (math.dot(math.cross(p1 - p0, p2 - p0), n) >= 0f) { _i.Add(b); _i.Add(b + 1); _i.Add(b + 2); _i.Add(b); _i.Add(b + 2); _i.Add(b + 3); }
                else { _i.Add(b); _i.Add(b + 2); _i.Add(b + 1); _i.Add(b); _i.Add(b + 3); _i.Add(b + 2); }
            }
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertexBufferParams(_v.Count, HoldMesher.Layout);
            mesh.SetVertexBufferData(_v, 0, 0, _v.Count);
            mesh.SetIndexBufferParams(_i.Count, IndexFormat.UInt32);
            mesh.SetIndexBufferData(_i, 0, 0, _i.Count);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, _i.Count));
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Slices the runtime pieces use (HoldPalettes.Layer; 0 if a layer is missing).</summary>
        public struct Slices
        {
            public int Door, Panel, Band, Stone, Rune;

            public static Slices From(LuckyNumbers.Model.HoldTextures t, string stone)
            {
                int L(string name) => math.max(0, HoldPalettes.Layer(t, name));
                return new Slices
                {
                    Door = L("Surfaces/dlv_door1a"), Panel = L("Surfaces/dlv_metalpan3a"), Band = L("Surfaces/dlv_metalstr2a"),
                    Rune = L("Surfaces/dlv_keys1"),
                    Stone = string.IsNullOrEmpty(stone) ? 0 : L(stone),
                };
            }
        }

        /// <summary>Hatch footprint (metres, square) and height: covers the shaft mouth and the ladder top under it.</summary>
        public const float HatchSize = 1.94f, HatchHeight = 1.08f;

        /// <summary>
        /// A depth seal's hatch, origin at its bottom centre: a squat iron-banded cap, the great door's face on top,
        /// a lifting ring. Light: flat vertex light (0..1).
        /// </summary>
        public static Mesh SealHatch(float cellSize, Slices s, float light)
        {
            var k = new DepthKit(cellSize, light);
            var h = HatchSize * 0.5f;
            var body = HatchHeight - 0.08f;
            k.Box(new float3(-h + 0.02f, 0f, -h + 0.02f), new float3(h - 0.02f, body, h - 0.02f), s.Panel, 1 << 3);
            k.Box(new float3(-h, body, -h), new float3(h, HatchHeight, h), s.Door, 0, true);
            foreach (var y in new[] { 0.14f, 0.62f })
                k.Box(new float3(-h, y, -h), new float3(h, y + 0.12f, h), s.Band);
            k.Box(new float3(-0.16f, HatchHeight, -0.05f), new float3(0.16f, HatchHeight + 0.08f, 0.05f), s.Band);
            foreach (var x in new[] { -0.55f, 0.55f })
                k.Box(new float3(x - 0.06f, HatchHeight, -h + 0.1f), new float3(x + 0.06f, HatchHeight + 0.03f, h - 0.1f), s.Band);
            return k.Build("Seal hatch");
        }

        /// <summary>A chain link run, 1 m tall from its origin up (scale y to length).</summary>
        public static Mesh Chain(float cellSize, Slices s, float light)
        {
            var k = new DepthKit(cellSize, light);
            k.Box(new float3(-0.025f, 0f, -0.025f), new float3(0.025f, 1f, 0.025f), s.Band);
            return k.Build("Seal chain");
        }

        /// <summary>
        /// A warning plaque, facing -z, origin at its centre: the old clans' yellow danger rune (dlv_keys1's yellow
        /// quarter) on an iron plate, nailed to a timber board.
        /// </summary>
        public static Mesh WarningPlate(float cellSize, Slices s, float light)
        {
            var k = new DepthKit(cellSize, light);
            k.Box(new float3(-0.21f, -0.21f, -0.02f), new float3(0.21f, 0.21f, 0f), s.Rune, ~(1 << 4) & 63, true, new float4(0.5f, 0.5f, 1f, 1f));
            k.Box(new float3(-0.21f, -0.21f, -0.02f), new float3(0.21f, 0.21f, 0f), s.Band, (1 << 4) | (1 << 5));
            k.Box(new float3(-0.26f, -0.26f, 0f), new float3(0.26f, 0.26f, 0.03f), s.Panel);
            return k.Build("Drop warning");
        }

        /// <summary>A stone plinth the seal key lies on, origin at its bottom centre, back towards +z.</summary>
        public static Mesh KeyPlinth(float cellSize, Slices s, float light)
        {
            var k = new DepthKit(cellSize, light);
            k.Box(new float3(-0.3f, 0f, -0.22f), new float3(0.3f, 0.72f, 0.22f), s.Stone, 1 << 2);
            k.Box(new float3(-0.34f, 0.72f, -0.26f), new float3(0.34f, 0.82f, 0.26f), s.Stone);
            return k.Build("Key plinth");
        }
    }
}
