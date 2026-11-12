using System;
using System.Runtime.InteropServices;
using LuckyNumbers.Model;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// One hold vertex, one stream, 40 bytes: position float3, normal float3, colour UNorm8 x4 (baked light rgb, a = 1),
    /// TEXCOORD0 float3 (u, v, texture-array slice). UVs are world-aligned in cell units (1 tile per cell_size metres).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HoldVertex
    {
        public float3 Position;
        public float3 Normal;
        public Color32 Color;
        public float3 Uv;
    }

    /// <summary>An axis-aligned collider box in world space.</summary>
    public struct HoldBox
    {
        public float3 Min, Max;
        public readonly Bounds Bounds => new((Min + Max) * 0.5f, Max - Min);
    }

    /// <summary>The HoldConfig numbers the mesher needs, as plain data for the jobs.</summary>
    public struct HoldMeshSettings
    {
        public float CellSize, StoreyHeight;
        public int ChunkCells;
        public float LanternRadius, Ambient;
        public float3 LanternColor;

        public readonly float3 Scale => new(CellSize, StoreyHeight, CellSize);

        public static HoldMeshSettings From(HoldConfig c) => new()
        {
            CellSize = c.CellSize > 0f ? c.CellSize : 2f,
            StoreyHeight = c.StoreyHeight > 0f ? c.StoreyHeight : 3f,
            ChunkCells = c.ChunkCells > 0 ? c.ChunkCells : 8,
            LanternRadius = math.max(0f, c.LanternRadius),
            Ambient = c.Ambient,
            LanternColor = new float3(c.LanternR, c.LanternG, c.LanternB),
        };
    }

    /// <summary>One built chunk: chunk_cells x chunk_cells columns of cells, all levels. Owner destroys the Mesh.</summary>
    public sealed class HoldChunk
    {
        /// <summary>Chunk coordinates in the grid (x east, y north): cells [Index * chunk_cells, +chunk_cells).</summary>
        public int2 Index;
        public Mesh Mesh;
        /// <summary>World-space boxes: merged wall runs (whole Solid cells), floor slabs, pillars.</summary>
        public Bounds[] Colliders;
    }

    /// <summary>
    /// Turns a HoldGrid into chunk meshes (docs/holds.md, Mesh): one Burst job per chunk writes vertices, indices and
    /// collider boxes into lists, then the main thread copies them into Meshes through Mesh.MeshData.
    /// </summary>
    public static class HoldMesher
    {
        /// <summary>The vertex layout of every hold mesh (see <see cref="HoldVertex"/>).</summary>
        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
        };

        public static int2 ChunkCount(int3 size, int chunkCells) => (size.xz + chunkCells - 1) / math.max(1, chunkCells);

        /// <summary>Build every non-empty chunk now (schedules the jobs and waits).</summary>
        public static HoldChunk[] Build(in HoldGrid grid, NativeArray<PaletteLayers> palettes, HoldConfig config)
        {
            using var build = Schedule(grid, palettes, HoldMeshSettings.From(config));
            return build.Complete();
        }

        /// <summary>
        /// Schedule one job per chunk. The grid and palettes must stay alive until the build is completed or disposed.
        /// </summary>
        public static HoldMeshBuild Schedule(in HoldGrid grid, NativeArray<PaletteLayers> palettes, HoldMeshSettings settings,
            JobHandle dependsOn = default)
        {
            settings.ChunkCells = math.max(1, settings.ChunkCells);
            var counts = ChunkCount(grid.Size, settings.ChunkCells);
            var k = counts.x * counts.y;
            var build = new HoldMeshBuild(k);
            var handles = new NativeArray<JobHandle>(k, Allocator.Temp);
            for (var i = 0; i < k; i++)
            {
                var chunk = new int2(i % counts.x, i / counts.x);
                build.Chunks[i] = chunk;
                build.Vertices[i] = new NativeList<HoldVertex>(512, Allocator.Persistent);
                build.Indices[i] = new NativeList<int>(768, Allocator.Persistent);
                build.Boxes[i] = new NativeList<HoldBox>(32, Allocator.Persistent);
                handles[i] = new HoldChunkJob
                {
                    Cells = grid.Cells, Palettes = palettes, Size = grid.Size, Origin = grid.Origin, Chunk = chunk,
                    Settings = settings, Vertices = build.Vertices[i], Indices = build.Indices[i], Boxes = build.Boxes[i],
                }.Schedule(dependsOn);
            }
            JobHandle.ScheduleBatchedJobs();
            build.Handle = JobHandle.CombineDependencies(handles);
            handles.Dispose();
            return build;
        }
    }

    /// <summary>The face rules, shared by the mesher jobs and anything that must agree with the mesh (tests, navigation).</summary>
    public static class HoldRules
    {
        /// <summary>Lower and upper are one space: no floor/ceiling between them (OpenAbove, or a shaft).</summary>
        public static bool Linked(HoldCell lower, HoldCell upper) =>
            lower.Walkable && upper.Walkable &&
            ((lower.Flags & CellFlags.OpenAbove) != 0 || upper.Kind == CellKind.ShaftDown || lower.Kind == CellKind.ShaftUp);

        /// <summary>Walkable cell c has a wall on side dir (horizontal unit): Solid there, except a Gate's way out.</summary>
        public static bool HasWall(in HoldGrid g, int3 c, int3 dir)
        {
            var n = c + dir;
            if (g.Get(n).Walkable) return false;
            return !(g.Get(c).Kind == CellKind.Gate && !g.InBounds(n));
        }

        /// <summary>
        /// No floor between linked cells. A ShaftDown on the grid's bottom level (or a ShaftUp on its top) opens into
        /// the region next door, which this grid can't see: the shaft stays open there too.
        /// </summary>
        public static bool HasFloor(in HoldGrid g, int3 c)
        {
            var below = c - new int3(0, 1, 0);
            if (!g.InBounds(below) && g.Get(c).Kind == CellKind.ShaftDown) return false;
            return !Linked(g.Get(below), g.Get(c));
        }

        public static bool HasCeiling(in HoldGrid g, int3 c)
        {
            var above = c + new int3(0, 1, 0);
            if (!g.InBounds(above) && g.Get(c).Kind == CellKind.ShaftUp) return false;
            return !Linked(g.Get(c), g.Get(above));
        }

        /// <summary>Metres from the wall to the ladder's front face (rails' depth).</summary>
        public const float LadderDepth = 0.12f;
        /// <summary>How far the rails run on above the upper floor (something to hold stepping off).</summary>
        public const float LadderAbove = 1f;

        /// <summary>A cell you can step off a ladder onto (or stand in beside a shaft).</summary>
        public static bool StepOff(HoldCell c) => c.Walkable && c.Kind != CellKind.ShaftDown && (c.Flags & CellFlags.Pillar) == 0;

        /// <summary>
        /// The wall (horizontal unit direction) the ladder of the shaft whose bottom is ShaftUp cell c stands against;
        /// zero if c is not ShaftUp. The whole ladder (both storeys) belongs to the ShaftUp cell's grid, so the mesher
        /// and the climbing code ask the same grid and always agree. Best side: Solid behind on the lower storey (4),
        /// on the upper (2), a floor to step off onto opposite it on the upper level (1); ties (and the side order)
        /// from a hash of the cell's world coordinates. A shaft on the grid's top level (its upper half in the
        /// region above) is judged on the lower storey alone.
        /// </summary>
        public static int3 LadderSide(in HoldGrid g, int3 c)
        {
            if (g.Get(c).Kind != CellKind.ShaftUp) return int3.zero;
            var upper = c + new int3(0, 1, 0);
            var seen = g.InBounds(upper);
            var w = g.ToWorld(c);
            var start = (int)(Mix((uint)w.x * 0x9E3779B1u ^ (uint)w.y * 0x85EBCA77u ^ (uint)w.z * 0xC2B2AE3Du) & 3u);
            var best = int3.zero;
            var bestScore = -1;
            for (var i = 0; i < 4; i++)
            {
                var d = Around(start + i);
                var score = 0;
                if (!g.Get(c + d).Walkable) score += 4;
                if (seen && !g.Get(upper + d).Walkable) score += 2;
                if (seen && StepOff(g.Get(upper - d))) score += 1;
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return best;
        }

        /// <summary>The four horizontal directions, anticlockwise from east (i mod 4).</summary>
        public static int3 Around(int i) => (i & 3) switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(0, 0, 1), 2 => new int3(-1, 0, 0), _ => new int3(0, 0, -1),
        };

        static uint Mix(uint h)
        {
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Scheduled chunk jobs; <see cref="Complete"/> turns them into meshes. Dispose if never completed.</summary>
    public sealed class HoldMeshBuild : IDisposable
    {
        public JobHandle Handle;
        public readonly int2[] Chunks;
        public readonly NativeList<HoldVertex>[] Vertices;
        public readonly NativeList<int>[] Indices;
        public readonly NativeList<HoldBox>[] Boxes;

        internal HoldMeshBuild(int k)
        {
            Chunks = new int2[k];
            Vertices = new NativeList<HoldVertex>[k];
            Indices = new NativeList<int>[k];
            Boxes = new NativeList<HoldBox>[k];
        }

        public bool IsCompleted => Handle.IsCompleted;

        /// <summary>Wait for the jobs and write the non-empty chunks into new Meshes (main thread).</summary>
        public HoldChunk[] Complete()
        {
            Handle.Complete();
            var used = 0;
            for (var i = 0; i < Chunks.Length; i++) if (Vertices[i].IsCreated && Vertices[i].Length > 0) used++;
            var result = new HoldChunk[used];
            if (used == 0) { Dispose(); return result; }

            var data = Mesh.AllocateWritableMeshData(used);
            var meshes = new Mesh[used];
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds;
            for (int i = 0, j = 0; i < Chunks.Length; i++)
            {
                var v = Vertices[i];
                if (!v.IsCreated || v.Length == 0) continue;
                var ix = Indices[i];
                var md = data[j];
                md.SetVertexBufferParams(v.Length, HoldMesher.Layout);
                md.GetVertexData<HoldVertex>().CopyFrom(v.AsArray());
                md.SetIndexBufferParams(ix.Length, IndexFormat.UInt32);
                md.GetIndexData<int>().CopyFrom(ix.AsArray());
                md.subMeshCount = 1;
                md.SetSubMesh(0, new SubMeshDescriptor(0, ix.Length), flags);

                var c = Chunks[i];
                meshes[j] = new Mesh { name = $"HoldChunk {c.x},{c.y}" };
                var boxes = Boxes[i];
                var colliders = new Bounds[boxes.Length];
                for (var b = 0; b < boxes.Length; b++) colliders[b] = boxes[b].Bounds;
                result[j] = new HoldChunk { Index = c, Mesh = meshes[j], Colliders = colliders };
                j++;
            }
            Mesh.ApplyAndDisposeWritableMeshData(data, meshes, flags);
            foreach (var m in meshes) m.RecalculateBounds();
            Dispose();
            return result;
        }

        public void Dispose()
        {
            Handle.Complete();
            for (var i = 0; i < Chunks.Length; i++)
            {
                if (Vertices[i].IsCreated) Vertices[i].Dispose();
                if (Indices[i].IsCreated) Indices[i].Dispose();
                if (Boxes[i].IsCreated) Boxes[i].Dispose();
            }
        }
    }

    /// <summary>One chunk's faces, kit pieces and collider boxes.</summary>
    [BurstCompile]
    public struct HoldChunkJob : IJob
    {
        [ReadOnly] public NativeArray<HoldCell> Cells;
        [ReadOnly] public NativeArray<PaletteLayers> Palettes;
        public int3 Size, Origin;
        public int2 Chunk;
        public HoldMeshSettings Settings;
        public NativeList<HoldVertex> Vertices;
        public NativeList<int> Indices;
        public NativeList<HoldBox> Boxes;

        public void Execute()
        {
            // The mesher holds the grid and scratch lists as plain fields (not job fields, which the scheduler checks).
            var m = new ChunkMesher
            {
                G = new HoldGrid { Size = Size, Origin = Origin, Cells = Cells },
                Pal = Palettes, S = Settings, Scale = Settings.Scale, V = Vertices, I = Indices, B = Boxes,
            };
            m.Run(Chunk);
        }
    }

    struct ChunkMesher
    {
        public HoldGrid G;
        public NativeArray<PaletteLayers> Pal;
        public HoldMeshSettings S;
        public float3 Scale;
        public NativeList<HoldVertex> V;
        public NativeList<int> I;
        public NativeList<HoldBox> B;
        NativeList<float3> _lanterns;

        // Box face bits: axis * 2 + (positive side ? 1 : 0).
        const int NegX = 1 << 0, PosX = 1 << 1, NegY = 1 << 2, PosY = 1 << 3, NegZ = 1 << 4, PosZ = 1 << 5;
        const float PillarHalf = 0.3f, TrimHalf = 0.4f, TrimHeight = 0.3f;
        const float JambWidth = 0.3f, FrameDepth = 0.4f, DoorHeadroom = 0.6f;
        const float LanternHalf = 0.15f, LanternDrop = 0.75f, LanternBody = 0.3f, ChainHalf = 0.03f;
        // Ladder: rail centres +-LadderHalfWidth from the wall's middle; rungs between them, set back off the rails'
        // front (RungNear..RungFar from the wall) so there's a toe-hold.
        const float LadderHalfWidth = 0.3f, RailHalf = 0.04f, RungNear = 0.035f, RungFar = 0.095f, RungThick = 0.05f;
        const float FirstRung = 0.3f, RungPitch = 0.35f;

        public void Run(int2 chunk)
        {
            var n = S.ChunkCells;
            var min = new int3(chunk.x * n, 0, chunk.y * n);
            var max = math.min(new int3(min.x + n, G.Size.y, min.z + n), G.Size);
            _lanterns = new NativeList<float3>(16, Allocator.Temp);
            GatherLanterns(min, max);

            for (var y = min.y; y < max.y; y++)
            for (var z = min.z; z < max.z; z++)
            for (var x = min.x; x < max.x; x++)
            {
                var c = new int3(x, y, z);
                var cell = G.Get(c);
                if (cell.Walkable) EmitCell(c, cell);
            }
            Colliders(min, max);
            _lanterns.Dispose();
        }

        static int3 Dir(int i) => i switch
        {
            0 => new int3(1, 0, 0), 1 => new int3(-1, 0, 0), 2 => new int3(0, 0, 1), _ => new int3(0, 0, -1),
        };

        float3 World(float3 local) => ((float3)G.Origin + local) * Scale;
        int3 CellOf(float3 world) => (int3)math.floor(world / Scale) - G.Origin;

        PaletteLayers Palette(HoldCell c) => Pal.Length == 0 ? default : Pal[math.min(c.Palette, Pal.Length - 1)];
        static int Slice(int s) => math.max(s, 0);

        void EmitCell(int3 c, HoldCell cell)
        {
            var p = Palette(cell);
            var floor = HoldRules.HasFloor(G, c);
            var ceiling = HoldRules.HasCeiling(G, c);
            if (floor) Shell(c, new int3(0, -1, 0), Slice(p.Floor));
            if (ceiling) Shell(c, new int3(0, 1, 0), Slice(p.Ceiling));
            for (var i = 0; i < 4; i++)
                if (HoldRules.HasWall(G, c, Dir(i))) Shell(c, Dir(i), Slice(p.Wall));

            if (cell.Kind == CellKind.Door) DoorFrame(c, Slice(p.Trim), floor, ceiling);
            if ((cell.Flags & CellFlags.Pillar) != 0) Pillar(c, Slice(p.Pillar), Slice(p.Trim), floor, ceiling);
            if ((cell.Flags & CellFlags.Lantern) != 0) Lantern(c, Slice(p.Trim), ceiling);
            if (cell.Kind == CellKind.ShaftUp) Ladder(c, Slice(p.Pillar), Slice(p.Trim), floor);
        }

        // ---------------------------------------------------------------- shell faces

        /// <summary>The face of cell c on side o (unit axis), facing into c, with per-corner AO.</summary>
        void Shell(int3 c, int3 o, int slice)
        {
            var axis = o.x != 0 ? 0 : o.y != 0 ? 1 : 2;
            int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
            var corners = new float3x4();
            var ao = float4.zero;
            for (var k = 0; k < 4; k++)
            {
                var u = k == 1 || k == 2 ? 1 : 0;
                var v = k >= 2 ? 1 : 0;
                var local = (float3)c;
                local[axis] += o[axis] > 0 ? 1f : 0f;
                local[a1] += u;
                local[a2] += v;
                corners[k] = World(local);
                var t1 = int3.zero; t1[a1] = u == 1 ? 1 : -1;
                var t2 = int3.zero; t2[a2] = v == 1 ? 1 : -1;
                ao[k] = Ao(c, t1, t2);
            }
            Quad(corners, -(float3)o, slice, ao, false);
        }

        bool Blocked(int3 c, int3 t) =>
            t.y < 0 ? HoldRules.HasFloor(G, c) : t.y > 0 ? HoldRules.HasCeiling(G, c) : HoldRules.HasWall(G, c, t);

        /// <summary>Corner darkening (voxel-style): sides closed along both tangents, plus the diagonal for floors/ceilings.</summary>
        float Ao(int3 c, int3 t1, int3 t2)
        {
            var s1 = Blocked(c, t1);
            var s2 = Blocked(c, t2);
            var corner = false;
            if (t1.y == 0 && t2.y == 0)
            {
                var d = c + t1 + t2;
                corner = !G.Get(d).Walkable && (G.InBounds(d) || G.Get(c).Kind != CellKind.Gate);
            }
            var occ = s1 && s2 ? 3 : (s1 ? 1 : 0) + (s2 ? 1 : 0) + (corner ? 1 : 0);
            return occ switch { 0 => 1f, 1 => 0.8f, 2 => 0.65f, _ => 0.5f };
        }

        // ---------------------------------------------------------------- kit pieces

        void DoorFrame(int3 c, int trim, bool floor, bool ceiling)
        {
            var wx = HoldRules.HasWall(G, c, new int3(1, 0, 0)) && HoldRules.HasWall(G, c, new int3(-1, 0, 0));
            var wz = HoldRules.HasWall(G, c, new int3(0, 0, 1)) && HoldRules.HasWall(G, c, new int3(0, 0, -1));
            if (wx == wz) return;   // not passing through a single wall line
            int across = wx ? 0 : 2, along = wx ? 2 : 0;
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var mid = (lo[along] + hi[along]) * 0.5f;
            var open = lo.y + math.max(1f, S.StoreyHeight - DoorHeadroom);
            int negA = 1 << (across * 2), posA = 1 << (across * 2 + 1);
            var bottom = floor ? NegY : 0;

            var jMin = lo; var jMax = hi;
            jMin[along] = mid - FrameDepth * 0.5f; jMax[along] = mid + FrameDepth * 0.5f;
            jMax.y = open;
            var left = jMax; left[across] = lo[across] + JambWidth;
            Box(jMin, left, trim, negA | bottom | PosY, false);
            var rightMin = jMin; rightMin[across] = hi[across] - JambWidth;
            Box(rightMin, jMax, trim, posA | bottom | PosY, false);

            var lMin = jMin; lMin.y = open;
            var lMax = jMax; lMax.y = hi.y;
            Box(lMin, lMax, trim, negA | posA | (ceiling ? PosY : 0), false);
        }

        void Pillar(int3 c, int pillar, int trim, bool floor, bool ceiling)
        {
            var centre = World((float3)c + new float3(0.5f, 0f, 0.5f));
            var bottom = centre.y;
            var top = centre.y + S.StoreyHeight;
            var t = new float3(TrimHalf, 0f, TrimHalf);
            var s = new float3(PillarHalf, 0f, PillarHalf);
            Box(new float3(centre.x - t.x, bottom, centre.z - t.z), new float3(centre.x + t.x, bottom + TrimHeight, centre.z + t.z),
                trim, floor ? NegY : 0, false);
            Box(new float3(centre.x - t.x, top - TrimHeight, centre.z - t.z), new float3(centre.x + t.x, top, centre.z + t.z),
                trim, ceiling ? PosY : 0, false);
            var sMin = new float3(centre.x - s.x, bottom + TrimHeight, centre.z - s.z);
            var sMax = new float3(centre.x + s.x, top - TrimHeight, centre.z + s.z);
            Box(sMin, sMax, pillar, NegY | PosY, false);
            B.Add(new HoldBox { Min = new float3(sMin.x, bottom, sMin.z), Max = new float3(sMax.x, top, sMax.z) });
        }

        /// <summary>
        /// A timber ladder against the shaft's ladder wall (HoldRules.LadderSide): two rails from this cell's floor up
        /// through the cell above to LadderAbove over its floor, rungs every RungPitch. All of it is this cell's,
        /// even the part in the storey above (which may be another region's grid).
        /// </summary>
        void Ladder(int3 c, int rail, int rung, bool floor)
        {
            var d = HoldRules.LadderSide(G, c);
            if (math.all(d == 0)) return;
            int across = d.x != 0 ? 0 : 2, along = d.x != 0 ? 2 : 0;
            var s = d[across];
            var lo = World(c);
            var hi = World((float3)c + 1f);
            var wall = s > 0 ? hi[across] : lo[across];
            var mid = (lo[along] + hi[along]) * 0.5f;
            float y0 = lo.y, y1 = lo.y + S.StoreyHeight, y2 = y1 + HoldRules.LadderAbove;
            var up = new int3(0, 1, 0);
            var wallLow = !G.Get(c + d).Walkable;
            var wallHigh = !G.InBounds(c + up) || !G.Get(c + up + d).Walkable;
            var back = 1 << (across * 2 + (s > 0 ? 1 : 0));
            var acrossLo = math.min(wall, wall - s * HoldRules.LadderDepth);
            var acrossHi = math.max(wall, wall - s * HoldRules.LadderDepth);

            for (var r = -1; r <= 1; r += 2)
            {
                var min = float3.zero; var max = float3.zero;
                min[across] = acrossLo; max[across] = acrossHi;
                min[along] = mid + r * LadderHalfWidth - RailHalf; max[along] = mid + r * LadderHalfWidth + RailHalf;
                min.y = y0; max.y = y1;
                Box(min, max, rail, (floor ? NegY : 0) | PosY | (wallLow ? back : 0), false);
                min.y = y1; max.y = y2;
                Box(min, max, rail, NegY | (wallHigh ? back : 0), false);
            }

            var rMin = float3.zero; var rMax = float3.zero;
            rMin[across] = math.min(wall - s * RungNear, wall - s * RungFar);
            rMax[across] = math.max(wall - s * RungNear, wall - s * RungFar);
            rMin[along] = mid - LadderHalfWidth + RailHalf; rMax[along] = mid + LadderHalfWidth - RailHalf;
            var ends = (1 << (along * 2)) | (1 << (along * 2 + 1));
            for (var y = y0 + FirstRung; y + RungThick <= y2 - 0.1f; y += RungPitch)
            {
                rMin.y = y; rMax.y = y + RungThick;
                Box(rMin, rMax, rung, ends, false);
            }
        }

        float3 LanternPos(int3 c) =>
            World((float3)c + new float3(0.5f, 0f, 0.5f)) + new float3(0f, S.StoreyHeight - LanternDrop + LanternBody * 0.5f, 0f);

        void Lantern(int3 c, int trim, bool ceiling)
        {
            var p = LanternPos(c);
            var top = World(c).y + S.StoreyHeight;
            var h = new float3(LanternHalf, LanternBody * 0.5f, LanternHalf);
            Box(p - h, p + h, trim, 0, true);
            Box(new float3(p.x - ChainHalf, p.y + h.y, p.z - ChainHalf), new float3(p.x + ChainHalf, top, p.z + ChainHalf),
                trim, NegY | (ceiling ? PosY : 0), false);
        }

        /// <summary>An axis-aligned box, outward faces, minus the faces in `skip` (hidden against wall/floor/ceiling).</summary>
        void Box(float3 min, float3 max, int slice, int skip, bool glow)
        {
            for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side < 2; side++)
            {
                if ((skip & (1 << (axis * 2 + side))) != 0) continue;
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                var corners = new float3x4();
                for (var k = 0; k < 4; k++)
                {
                    var p = min;
                    p[axis] = side == 1 ? max[axis] : min[axis];
                    p[a1] = k == 1 || k == 2 ? max[a1] : min[a1];
                    p[a2] = k >= 2 ? max[a2] : min[a2];
                    corners[k] = p;
                }
                var n = float3.zero;
                n[axis] = side == 1 ? 1f : -1f;
                Quad(corners, n, slice, 1f, glow);
            }
        }

        // ---------------------------------------------------------------- quads and light

        /// <summary>Four corners in loop order; winding is fixed up so the face is front-facing along n (Unity: clockwise).</summary>
        void Quad(float3x4 p, float3 n, int slice, float4 ao, bool glow)
        {
            var centre = (p.c0 + p.c1 + p.c2 + p.c3) * 0.25f;
            var b = V.Length;
            for (var k = 0; k < 4; k++)
            {
                var light = glow ? math.saturate(S.LanternColor) : Light(p[k], n, centre) * ao[k];
                var col = math.saturate(light) * 255f + 0.5f;
                V.Add(new HoldVertex
                {
                    Position = p[k],
                    Normal = n,
                    Color = new Color32((byte)col.x, (byte)col.y, (byte)col.z, 255),
                    Uv = new float3(Uv(p[k], n), slice),
                });
            }
            if (math.dot(math.cross(p.c1 - p.c0, p.c2 - p.c0), n) >= 0f)
            {
                I.Add(b); I.Add(b + 1); I.Add(b + 2);
                I.Add(b); I.Add(b + 2); I.Add(b + 3);
            }
            else
            {
                I.Add(b); I.Add(b + 2); I.Add(b + 1);
                I.Add(b); I.Add(b + 3); I.Add(b + 2);
            }
        }

        /// <summary>World-aligned, in cell units: floors/ceilings by (x, z); walls by (along the wall to the viewer's right, y).</summary>
        float2 Uv(float3 p, float3 n)
        {
            if (math.abs(n.y) > 0.5f) return new float2(p.x, n.y > 0f ? p.z : -p.z) / S.CellSize;
            var right = math.cross(new float3(0f, 1f, 0f), -n);
            return new float2(math.dot(p, right), p.y) / S.CellSize;
        }

        void GatherLanterns(int3 min, int3 max)
        {
            if (S.LanternRadius <= 0f) return;
            var r = new int3((int)math.ceil(S.LanternRadius / S.CellSize), (int)math.ceil(S.LanternRadius / S.StoreyHeight),
                (int)math.ceil(S.LanternRadius / S.CellSize)) + 1;
            var lo = math.max(min - r, 0);
            var hi = math.min(max + r, G.Size);
            for (var y = lo.y; y < hi.y; y++)
            for (var z = lo.z; z < hi.z; z++)
            for (var x = lo.x; x < hi.x; x++)
            {
                var c = new int3(x, y, z);
                var cell = G.Get(c);
                if (cell.Walkable && (cell.Flags & CellFlags.Lantern) != 0) _lanterns.Add(LanternPos(c));
            }
        }

        /// <summary>Ambient plus every unoccluded lantern in reach: smooth falloff, soft Lambert.</summary>
        float3 Light(float3 p, float3 n, float3 centre)
        {
            var sum = new float3(S.Ambient);
            var r2 = S.LanternRadius * S.LanternRadius;
            // Sample just off the face, nudged toward its centre, so corner vertices land in the face's own cell.
            var at = math.lerp(p, centre, 0.05f) + n * 0.02f;
            for (var i = 0; i < _lanterns.Length; i++)
            {
                var to = _lanterns[i] - at;
                var d2 = math.lengthsq(to);
                if (d2 >= r2) continue;
                var f = 1f - d2 / r2;
                var lambert = 0.3f + 0.7f * math.saturate(math.dot(n, to * math.rsqrt(math.max(d2, 1e-6f))));
                if (Occluded(_lanterns[i], at)) continue;
                sum += S.LanternColor * (f * f * lambert);
            }
            return sum;
        }

        /// <summary>A Solid cell (or a floor between storeys) on the straight line, at cell resolution.</summary>
        bool Occluded(float3 from, float3 to)
        {
            var step = 0.25f * math.cmin(Scale);
            var steps = (int)math.ceil(math.distance(from, to) / step);
            var prev = CellOf(from);
            for (var i = 1; i <= steps; i++)
            {
                var cur = CellOf(math.lerp(from, to, (float)i / steps));
                if (math.all(cur == prev)) continue;
                if (!G.Get(cur).Walkable) return true;
                if (cur.y != prev.y)
                {
                    if (math.abs(cur.y - prev.y) > 1) return true;
                    var lower = new int3(cur.x, math.min(cur.y, prev.y), cur.z);
                    if (!HoldRules.Linked(G.Get(lower), G.Get(lower + new int3(0, 1, 0)))) return true;
                }
                prev = cur;
            }
            return false;
        }

        // ---------------------------------------------------------------- colliders

        void Colliders(int3 min, int3 max)
        {
            // Walls are whole Solid cells; the chunks on the grid's border also own the outside ring (reads as Solid).
            var x0 = min.x == 0 ? -1 : min.x;
            var z0 = min.z == 0 ? -1 : min.z;
            var x1 = max.x == G.Size.x ? G.Size.x + 1 : max.x;
            var z1 = max.z == G.Size.z ? G.Size.z + 1 : max.z;
            int w = x1 - x0, d = z1 - z0;
            var mask = new NativeArray<byte>(w * d, Allocator.Temp);
            for (var y = min.y; y < max.y; y++)
            {
                var floorY = (G.Origin.y + y) * S.StoreyHeight;
                for (var z = z0; z < z1; z++)
                for (var x = x0; x < x1; x++)
                {
                    var c = new int3(x, y, z);
                    if (G.Get(c).Walkable) continue;
                    for (var i = 0; i < 4; i++)
                    {
                        var nb = c + Dir(i);
                        if (G.InBounds(nb) && G.Get(nb).Walkable && HoldRules.HasWall(G, nb, -Dir(i))) { mask[(x - x0) + (z - z0) * w] = 1; break; }
                    }
                }
                Merge(mask, w, d, x0, z0, floorY, floorY + S.StoreyHeight);

                // Floor slabs: thick over rock (nothing falls through), thin over a room below.
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var z = min.z; z < max.z; z++)
                    for (var x = min.x; x < max.x; x++)
                    {
                        var c = new int3(x, y, z);
                        if (!G.Get(c).Walkable || !HoldRules.HasFloor(G, c)) continue;
                        var overRock = !G.Get(c - new int3(0, 1, 0)).Walkable;
                        if (overRock == (pass == 0)) mask[(x - x0) + (z - z0) * w] = 1;
                    }
                    Merge(mask, w, d, x0, z0, floorY - (pass == 0 ? 1f : 0.1f), floorY);
                }
            }
            mask.Dispose();
        }

        /// <summary>Greedy rectangles over a set mask (cleared as it goes), each one box spanning yLo..yHi.</summary>
        void Merge(NativeArray<byte> mask, int w, int d, int x0, int z0, float yLo, float yHi)
        {
            for (var z = 0; z < d; z++)
            for (var x = 0; x < w; x++)
            {
                if (mask[x + z * w] == 0) continue;
                var ex = x + 1;
                while (ex < w && mask[ex + z * w] != 0) ex++;
                var ez = z + 1;
                for (; ez < d; ez++)
                {
                    var full = true;
                    for (var i = x; i < ex && full; i++) full = mask[i + ez * w] != 0;
                    if (!full) break;
                }
                for (var j = z; j < ez; j++)
                for (var i = x; i < ex; i++) mask[i + j * w] = 0;
                B.Add(new HoldBox
                {
                    Min = new float3((G.Origin.x + x0 + x) * S.CellSize, yLo, (G.Origin.z + z0 + z) * S.CellSize),
                    Max = new float3((G.Origin.x + x0 + ex) * S.CellSize, yHi, (G.Origin.z + z0 + ez) * S.CellSize),
                });
            }
        }
    }
}
