using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// The vertical kit's texture slices (stair treads, hoist ironwork), set from HoldTextures by HoldPalettes.Resolve
    /// and read by the Burst mesher jobs through a SharedStatic. Unset or missing: -1 (the cell's palette is used).
    /// </summary>
    public static class VerticalKit
    {
        public const string StoneTread = "Surfaces/dlv_stonestp1a", WoodTread = "Surfaces/dlv_woodstp1a";
        public const string IronStrut = "Surfaces/dlv_metalstr2a", IronPanel = "Surfaces/dlv_metalpan3a";

        public struct Slices
        {
            // Slice + 1, so a never-configured (zeroed) SharedStatic reads as missing.
            public int Stone, Wood, Iron, Panel;
        }

        struct Key { }
        static readonly SharedStatic<Slices> Shared = SharedStatic<Slices>.GetOrCreate<Key>();

        public static void Configure(HoldTextures textures)
        {
            if (textures == null) return;
            Shared.Data = new Slices
            {
                Stone = HoldPalettes.Layer(textures, StoneTread) + 1, Wood = HoldPalettes.Layer(textures, WoodTread) + 1,
                Iron = HoldPalettes.Layer(textures, IronStrut) + 1, Panel = HoldPalettes.Layer(textures, IronPanel) + 1,
            };
        }

        public static int Stone => Shared.Data.Stone - 1;
        public static int Wood => Shared.Data.Wood - 1;
        public static int Iron => Shared.Data.Iron - 1;
        public static int Panel => Shared.Data.Panel - 1;
    }

    /// <summary>
    /// Stairs and great shafts (docs/holds.md, "Stairs, great shafts, hoists"): treads with a collider box per step,
    /// a lintel over the well's foot; in a great shaft corner posts, walings round the rock, the hoist's iron guide
    /// rails, wall lanterns and the headframe; on its landings railings with a gateway at the dock.
    /// </summary>
    partial struct ChunkMesher
    {
        const int AllFaces = 63;
        const float PostSide = 0.28f, WalingDepth = 0.18f, WalingHeight = 0.3f;
        const float GuideHalf = 0.08f, GuideDepth = 0.26f;
        const float RailInset = 0.02f, RailDepth = 0.08f, RailHeight = 1.1f;

        static int Face(int axis, bool positive) => 1 << (axis * 2 + (positive ? 1 : 0));
        static int Or(int slice, int fallback) => slice >= 0 ? slice : Slice(fallback);

        partial void KitVertical(int3 c, HoldCell cell, PaletteLayers p, bool floor, bool ceiling)
        {
            if (cell.Kind == CellKind.Stair)
            {
                if (StairCell.IsStep(cell)) Steps(c, cell, p);
                else if (StairCell.Index(cell) == 0) WellLintel(c, cell, p);
                return;
            }
            if (cell.Kind == CellKind.GreatShaft)
            {
                ShaftKit(c, cell, p, ceiling);
                return;
            }
            for (var i = 0; i < 4; i++)
            {
                var at = c + Dir(i);
                var nb = G.Get(at);
                if (nb.Kind == CellKind.GreatShaft) Railing(c, i, at, nb, p);
            }
        }

        // ---------------------------------------------------------------- stairs

        void Steps(int3 c, HoldCell cell, PaletteLayers p)
        {
            var d = Dir(StairCell.Dir(cell));
            int along = d.x != 0 ? 0 : 2;
            var up = d[along] > 0;
            var length = StairCell.Length(G, c, cell);
            var rise = S.StoreyHeight / (StairCell.TreadsPerCell * length);
            var tread = S.CellSize / StairCell.TreadsPerCell;
            var timber = StairCell.Timber(cell);
            var top = timber ? Or(VerticalKit.Wood, p.Trim) : Or(VerticalKit.Stone, p.Floor);
            var riser = timber ? Slice(p.Pillar) : Or(VerticalKit.Stone, p.Wall);
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var front = Face(along, !up);
            for (var j = 0; j < StairCell.TreadsPerCell; j++)
            {
                var k = StairCell.Index(cell) * StairCell.TreadsPerCell + j;
                var min = lo;
                var max = hi;
                min[along] = up ? lo[along] + j * tread : hi[along] - (j + 1) * tread;
                max[along] = min[along] + tread;
                max.y = lo.y + (k + 1) * rise;
                Box(min, max, top, AllFaces & ~PosY, false);
                Box(min, max, riser, AllFaces & ~front, false);
                B.Add(new HoldBox { Min = min, Max = max });
            }
        }

        /// <summary>A timber lintel where the lower storey's ceiling ends over the flight's foot.</summary>
        void WellLintel(int3 c, HoldCell cell, PaletteLayers p)
        {
            var d = Dir(StairCell.Dir(cell));
            int along = d.x != 0 ? 0 : 2, across = d.x != 0 ? 2 : 0;
            var up = d[along] > 0;
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var min = lo;
            var max = hi;
            if (up) max[along] = lo[along] + 0.3f;
            else min[along] = hi[along] - 0.3f;
            min.y = lo.y - 0.35f;
            max.y = lo.y;
            Box(min, max, Slice(p.Pillar), Face(across, false) | Face(across, true) | Face(along, !up), false);
        }

        // ---------------------------------------------------------------- great shafts

        void ShaftKit(int3 c, HoldCell cell, PaletteLayers p, bool ceiling)
        {
            var size = GreatShaftCell.Size(G, c, cell);
            int ix = GreatShaftCell.Ix(cell), iz = GreatShaftCell.Iz(cell), dock = GreatShaftCell.Dock(cell);
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var wood = Slice(p.Pillar);
            var iron = Or(VerticalKit.Iron, p.Trim);
            var panel = Or(VerticalKit.Panel, p.Trim);
            var mid = size / 2;
            var odd = (size & 1) == 1;

            // Corner posts: the shaft's four corners, storey on storey.
            for (var cx = 0; cx < 2; cx++)
            for (var cz = 0; cz < 2; cz++)
            {
                if (ix != (cx == 0 ? 0 : size - 1) || iz != (cz == 0 ? 0 : size - 1)) continue;
                var px = cx == 0 ? lo.x : hi.x - PostSide;
                var pz = cz == 0 ? lo.z : hi.z - PostSide;
                var min = new float3(px, lo.y, pz);
                var max = new float3(px + PostSide, hi.y, pz + PostSide);
                Box(min, max, wood, NegY | PosY, false);
                B.Add(new HoldBox { Min = min, Max = max });
            }

            // Walings: a timber under each storey's top, against the rock.
            for (var i = 0; i < 4; i++)
            {
                var d = Dir(i);
                if (!HoldRules.HasWall(G, c, d)) continue;
                var axis = d.x != 0 ? 0 : 2;
                var positive = d[axis] > 0;
                var wall = positive ? hi[axis] : lo[axis];
                var min = lo;
                var max = hi;
                min[axis] = math.min(wall, wall - d[axis] * WalingDepth);
                max[axis] = math.max(wall, wall - d[axis] * WalingDepth);
                min.y = hi.y - WalingHeight - 0.02f;
                max.y = hi.y - 0.02f;
                Box(min, max, wood, Face(axis, positive), false);
            }

            // The hoist's guide rails: iron, on the two walls square to the dock side, at their middle.
            int dockAxis = dock < 2 ? 0 : 2, guideAxis = dock < 2 ? 2 : 0;
            int dockIndex = dockAxis == 0 ? ix : iz, guideIndex = guideAxis == 0 ? ix : iz;
            if (dockIndex == mid)
                for (var sgn = -1; sgn <= 1; sgn += 2)
                {
                    if (guideIndex != (sgn < 0 ? 0 : size - 1)) continue;
                    var d = int3.zero;
                    d[guideAxis] = sgn;
                    if (!HoldRules.HasWall(G, c, d)) continue;
                    var centre = odd ? (lo[dockAxis] + hi[dockAxis]) * 0.5f : lo[dockAxis];
                    var wall = sgn > 0 ? hi[guideAxis] : lo[guideAxis];
                    var min = lo;
                    var max = hi;
                    min[dockAxis] = centre - GuideHalf;
                    max[dockAxis] = centre + GuideHalf;
                    min[guideAxis] = math.min(wall, wall - sgn * GuideDepth);
                    max[guideAxis] = math.max(wall, wall - sgn * GuideDepth);
                    Box(min, max, iron, NegY | PosY | Face(guideAxis, sgn > 0), false);
                }

            // Wall lanterns every other storey, on the wall facing the dock.
            var back = dock ^ 1;   // opposite side (0 <-> 1, 2 <-> 3)
            var backVec = Dir(back);
            var backIndex = backVec[dockAxis] > 0 ? size - 1 : 0;
            if (dockIndex == backIndex && guideIndex == mid && ((G.Origin.y + c.y) & 1) == 0 && HoldRules.HasWall(G, c, backVec))
            {
                var centre = odd ? (lo[guideAxis] + hi[guideAxis]) * 0.5f : lo[guideAxis];
                var s = backVec[dockAxis];
                var wall = s > 0 ? hi[dockAxis] : lo[dockAxis];
                var min = float3.zero;
                var max = float3.zero;
                min[guideAxis] = centre - 0.11f;
                max[guideAxis] = centre + 0.11f;
                min[dockAxis] = math.min(wall - s * 0.06f, wall - s * 0.28f);
                max[dockAxis] = math.max(wall - s * 0.06f, wall - s * 0.28f);
                min.y = lo.y + 1.95f;
                max.y = lo.y + 2.2f;
                Box(min, max, Slice(p.Trim), 0, true);
                min[guideAxis] = centre - 0.03f;
                max[guideAxis] = centre + 0.03f;
                min[dockAxis] = math.min(wall, wall - s * 0.2f);
                max[dockAxis] = math.max(wall, wall - s * 0.2f);
                min.y = max.y;
                max.y = min.y + 0.06f;
                Box(min, max, iron, Face(dockAxis, s > 0), false);
            }

            // The headframe under the top storey's ceiling: twin beams across the dock axis, the pulley between.
            if (ceiling && dockIndex == 0 && guideIndex == mid)
            {
                var centre = odd ? (lo[guideAxis] + hi[guideAxis]) * 0.5f : lo[guideAxis];
                var span = size * S.CellSize;
                for (var sgn = -1; sgn <= 1; sgn += 2)
                {
                    var min = lo;
                    var max = hi;
                    min[dockAxis] = lo[dockAxis];
                    max[dockAxis] = lo[dockAxis] + span;
                    min[guideAxis] = centre + sgn * 0.3f - 0.1f;
                    max[guideAxis] = centre + sgn * 0.3f + 0.1f;
                    min.y = hi.y - 0.4f;
                    max.y = hi.y;
                    Box(min, max, wood, PosY | Face(dockAxis, false) | Face(dockAxis, true), false);
                }
                var wheel = new float3();
                wheel[dockAxis] = lo[dockAxis] + span * 0.5f;
                wheel[guideAxis] = centre;
                var w0 = wheel;
                var w1 = wheel;
                w0[dockAxis] -= 0.3f; w1[dockAxis] += 0.3f;
                w0[guideAxis] -= 0.08f; w1[guideAxis] += 0.08f;
                w0.y = hi.y - 0.42f;
                w1.y = hi.y - 0.02f;
                Box(w0, w1, panel, 0, false);
                w0[guideAxis] = centre - 0.22f; w1[guideAxis] = centre + 0.22f;
                w0[dockAxis] = wheel[dockAxis] - 0.05f; w1[dockAxis] = wheel[dockAxis] + 0.05f;
                w0.y = hi.y - 0.25f; w1.y = hi.y - 0.19f;
                Box(w0, w1, iron, 0, false);
            }
        }

        /// <summary>
        /// A landing cell's edge onto the shaft at `at`: a timber railing (and its collider), or at the dock (the
        /// middle of the shaft's dock side) a gateway of two iron posts and a head; the hoist presenter closes it.
        /// </summary>
        void Railing(int3 c, int i, int3 at, HoldCell shaft, PaletteLayers p)
        {
            var d = Dir(i);
            var axis = d.x != 0 ? 0 : 2;
            var other = axis == 0 ? 2 : 0;
            var positive = d[axis] > 0;
            var size = GreatShaftCell.Size(G, at - new int3(GreatShaftCell.Ix(shaft), 0, 0), G.Get(at - new int3(GreatShaftCell.Ix(shaft), 0, 0)));
            var dock = GreatShaftCell.Dock(shaft);
            var along = other == 0 ? GreatShaftCell.Ix(shaft) : GreatShaftCell.Iz(shaft);
            var gate = math.all(Dir(dock) == -d) && along == size / 2;
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var edge = positive ? hi[axis] : lo[axis];
            var s = d[axis];
            float inner = edge - s * (RailInset + RailDepth), outer = edge - s * RailInset;

            float3 Bar(float a0, float a1, float y0, float y1, out float3 max)
            {
                var min = lo;
                max = hi;
                min[axis] = math.min(a0, a1);
                max[axis] = math.max(a0, a1);
                min.y = lo.y + y0;
                max.y = lo.y + y1;
                return min;
            }

            if (gate)
            {
                var iron = Or(VerticalKit.Iron, p.Trim);
                for (var e = 0; e < 2; e++)
                {
                    var min = Bar(edge - s * 0.02f, edge - s * 0.2f, 0f, 2.5f, out var max);
                    if (e == 0) max[other] = lo[other] + 0.18f; else min[other] = hi[other] - 0.18f;
                    Box(min, max, iron, NegY, false);
                    B.Add(new HoldBox { Min = min, Max = max });
                }
                var hMin = Bar(edge - s * 0.02f, edge - s * 0.2f, 2.5f, 2.68f, out var hMax);
                Box(hMin, hMax, iron, 0, false);
                return;
            }

            var wood = Slice(p.Pillar);
            var trim = Slice(p.Trim);
            for (var e = 0; e < 2; e++)
            {
                var min = Bar(inner - s * 0.02f, outer, 0f, RailHeight, out var max);
                if (e == 0) max[other] = lo[other] + 0.12f; else min[other] = hi[other] - 0.12f;
                Box(min, max, wood, NegY, false);
            }
            var rMin = Bar(inner, outer, RailHeight - 0.1f, RailHeight - 0.01f, out var rMax);
            Box(rMin, rMax, trim, 0, false);
            rMin = Bar(inner, outer, 0.5f, 0.58f, out rMax);
            Box(rMin, rMax, trim, 0, false);
            rMin = Bar(inner, outer, 0f, 0.16f, out rMax);
            Box(rMin, rMax, wood, NegY, false);
            var cMin = Bar(inner, outer, 0f, RailHeight + 0.1f, out var cMax);
            B.Add(new HoldBox { Min = cMin, Max = cMax });
        }
    }

    /// <summary>
    /// Meshes for the hoist (HoistPresenter, previews): HoldVertex boxes for the HoldKit material, in local metres,
    /// UVs in cell units like the hold's faces, one flat light level (the cage carries its own lamp).
    /// </summary>
    public static class HoistKit
    {
        /// <summary>Gap between the cage and the shaft's rock (posts and guides live in it).</summary>
        public const float Inset = 0.3f;
        /// <summary>Cage frame height over its floor (under the headframe at the top landing).</summary>
        public const float Height = 2.35f;
        public const float FloorThickness = 0.25f;
        public const float RailHeight = 1.1f;
        /// <summary>Top of the hook block the cable hangs from, over <see cref="Height"/>.</summary>
        public const float HookTop = 0.2f;
        /// <summary>Where the cable meets the headframe's pulley, under the shaft's top ceiling.</summary>
        public const float PulleyDrop = 0.22f;

        /// <summary>A box mesh builder (outward faces, a face mask to skip like ChunkMesher.Box).</summary>
        public sealed class Builder
        {
            readonly List<HoldVertex> _v = new();
            readonly List<int> _i = new();
            readonly float _uvScale;
            readonly Color32 _light;

            public Builder(float cellSize, float light)
            {
                _uvScale = 1f / math.max(0.1f, cellSize);
                var b = (byte)math.clamp(light * 255f, 0f, 255f);
                _light = new Color32(b, b, b, 255);
            }

            public void Box(float3 min, float3 max, int slice, int skip = 0)
            {
                slice = math.max(0, slice);
                for (var axis = 0; axis < 3; axis++)
                for (var side = 0; side < 2; side++)
                {
                    if ((skip & (1 << (axis * 2 + side))) != 0) continue;
                    int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                    var n = float3.zero;
                    n[axis] = side == 1 ? 1f : -1f;
                    var b = _v.Count;
                    var corners = new float3[4];
                    for (var k = 0; k < 4; k++)
                    {
                        var p = min;
                        p[axis] = side == 1 ? max[axis] : min[axis];
                        p[a1] = k == 1 || k == 2 ? max[a1] : min[a1];
                        p[a2] = k >= 2 ? max[a2] : min[a2];
                        corners[k] = p;
                        float2 uv;
                        if (axis == 1) uv = new float2(p.x, p.z);
                        else
                        {
                            var right = math.cross(new float3(0f, 1f, 0f), -n);
                            uv = new float2(math.dot(p, right), p.y);
                        }
                        _v.Add(new HoldVertex { Position = p, Normal = n, Color = _light, Uv = new float3(uv * _uvScale, slice) });
                    }
                    if (math.dot(math.cross(corners[1] - corners[0], corners[2] - corners[0]), n) >= 0f)
                    { _i.Add(b); _i.Add(b + 1); _i.Add(b + 2); _i.Add(b); _i.Add(b + 2); _i.Add(b + 3); }
                    else
                    { _i.Add(b); _i.Add(b + 2); _i.Add(b + 1); _i.Add(b); _i.Add(b + 3); _i.Add(b + 2); }
                }
            }

            public Mesh ToMesh(string name)
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
        }

        /// <summary>Cage of side `width` (metres), floor top at y 0, open on +x (rotate with <see cref="Facing"/>):
        /// iron-plate floor on timber, iron corner posts and top frame, railings on the three closed sides, a roof
        /// cross and the hook block the cable hangs from.</summary>
        public static Mesh Cage(float width, float cellSize, float light)
        {
            int wood = math.max(0, VerticalKit.Wood), iron = math.max(0, VerticalKit.Iron), panel = math.max(0, VerticalKit.Panel);
            var b = new Builder(cellSize, light);
            var h = width * 0.5f;
            const float post = 0.14f, bar = 0.09f;
            b.Box(new float3(-h, -FloorThickness, -h), new float3(h, -0.06f, h), wood);
            b.Box(new float3(-h + 0.05f, -0.06f, -h + 0.05f), new float3(h - 0.05f, 0f, h - 0.05f), panel, 1 << 2);
            for (var sx = -1; sx <= 1; sx += 2)
            for (var sz = -1; sz <= 1; sz += 2)
            {
                var c = new float3(sx * (h - post * 0.5f), 0f, sz * (h - post * 0.5f));
                b.Box(new float3(c.x - post * 0.5f, 0f, c.z - post * 0.5f), new float3(c.x + post * 0.5f, Height, c.z + post * 0.5f), iron, 1 << 2);
            }
            // Top frame.
            b.Box(new float3(-h, Height - 0.12f, -h), new float3(h, Height, -h + 0.12f), iron);
            b.Box(new float3(-h, Height - 0.12f, h - 0.12f), new float3(h, Height, h), iron);
            b.Box(new float3(-h, Height - 0.12f, -h), new float3(-h + 0.12f, Height, h), iron);
            b.Box(new float3(h - 0.12f, Height - 0.12f, -h), new float3(h, Height, h), iron);
            // Roof cross and hook block.
            b.Box(new float3(-h, Height - 0.1f, -0.07f), new float3(h, Height + 0.04f, 0.07f), iron);
            b.Box(new float3(-0.07f, Height - 0.1f, -h), new float3(0.07f, Height + 0.04f, h), iron);
            b.Box(new float3(-0.18f, Height + 0.04f, -0.18f), new float3(0.18f, Height + HookTop, 0.18f), panel);
            // Railings on -x, +z, -z (the +x side is the open door).
            Rail(b, new float3(-h, 0f, -h), new float3(-h + bar, 0f, h), wood, panel);
            Rail(b, new float3(-h, 0f, h - bar), new float3(h, 0f, h), wood, panel);
            Rail(b, new float3(-h, 0f, -h), new float3(h, 0f, -h + bar), wood, panel);
            return b.ToMesh("Hoist cage");
        }

        static void Rail(Builder b, float3 min, float3 max, int wood, int panel)
        {
            b.Box(new float3(min.x, RailHeight - 0.1f, min.z), new float3(max.x, RailHeight, max.z), wood);
            b.Box(new float3(min.x, 0.55f, min.z), new float3(max.x, 0.63f, max.z), wood);
            b.Box(new float3(min.x, 0f, min.z), new float3(max.x, 0.3f, max.z), panel, 1 << 2);
        }

        /// <summary>The cage's colliders in its local frame (open on +x): floor and the three railings.</summary>
        public static IEnumerable<Bounds> CageColliders(float width)
        {
            var h = width * 0.5f;
            yield return new Bounds(new Vector3(0f, -FloorThickness * 0.5f, 0f), new Vector3(width, FloorThickness, width));
            yield return new Bounds(new Vector3(-h + 0.05f, RailHeight * 0.5f, 0f), new Vector3(0.1f, RailHeight, width));
            yield return new Bounds(new Vector3(0f, RailHeight * 0.5f, h - 0.05f), new Vector3(width, RailHeight, 0.1f));
            yield return new Bounds(new Vector3(0f, RailHeight * 0.5f, -h + 0.05f), new Vector3(width, RailHeight, 0.1f));
        }

        /// <summary>A landing's gate across the dock gateway: two iron bars and a kick plate, `width` along local x,
        /// at y 0 the landing floor.</summary>
        public static Mesh Gate(float width, float cellSize, float light)
        {
            int iron = math.max(0, VerticalKit.Iron), panel = math.max(0, VerticalKit.Panel);
            var b = new Builder(cellSize, light);
            var h = width * 0.5f;
            b.Box(new float3(-h, 1.0f, -0.04f), new float3(h, 1.1f, 0.04f), iron);
            b.Box(new float3(-h, 0.55f, -0.04f), new float3(h, 0.63f, 0.04f), iron);
            b.Box(new float3(-h, 0.05f, -0.03f), new float3(h, 0.3f, 0.03f), panel);
            for (var x = -h + 0.3f; x < h - 0.2f; x += 0.4f)
                b.Box(new float3(x - 0.025f, 0.05f, -0.025f), new float3(x + 0.025f, 1.0f, 0.025f), iron);
            return b.ToMesh("Hoist gate");
        }

        /// <summary>A unit box (1 m cube, centred at the origin, bottom at y 0) for cables, scaled.</summary>
        public static Mesh Cable(float cellSize, float light) =>
            Unit(cellSize, light, math.max(0, VerticalKit.Iron));

        static Mesh Unit(float cellSize, float light, int slice)
        {
            var b = new Builder(cellSize, light);
            b.Box(new float3(-0.5f, 0f, -0.5f), new float3(0.5f, 1f, 0.5f), slice);
            return b.ToMesh("Hoist cable");
        }

        /// <summary>Yaw that turns the kit's +x (the cage's open side) to dock direction `dock` (0 +x, 1 -x, 2 +z, 3 -z).</summary>
        public static Quaternion Facing(int dock) => Quaternion.Euler(0f, dock switch { 0 => 0f, 1 => 180f, 2 => -90f, _ => 90f }, 0f);
    }
}
