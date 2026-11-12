using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using Unity.Collections;
using Unity.Mathematics;

namespace LuckyNumbers.Hold
{
    /// <summary>What fills a cell. Everything but Solid is walkable.</summary>
    public enum CellKind : byte
    {
        Solid = 0,
        /// <summary>Room floor (halls, workshops, storerooms).</summary>
        Floor,
        Corridor,
        /// <summary>A doorway: walkable, framed with trim on the two walls it passes through.</summary>
        Door,
        /// <summary>The way in from outside (cliff face).</summary>
        Gate,
        /// <summary>A shaft or stair down to the level below (that cell, one level down, is ShaftUp).</summary>
        ShaftDown,
        ShaftUp,
        /// <summary>Mine tunnel or chamber: rough walls, timber supports.</summary>
        Mine,
    }

    [Flags]
    public enum CellFlags : byte
    {
        None = 0,
        /// <summary>A lantern hangs here: its light is baked into the vertex colours around it.</summary>
        Lantern = 1 << 0,
        /// <summary>A pillar (or timber prop in mines) stands in this cell's centre; the cell is not walkable through its middle.</summary>
        Pillar = 1 << 1,
        /// <summary>Open to the storey above (no ceiling here; the cell above is part of the same space, e.g. a two-storey hall).</summary>
        OpenAbove = 1 << 2,
    }

    /// <summary>One 2 m x 3 m cell. 4 bytes, blittable, so grids go straight into Burst jobs.</summary>
    public struct HoldCell
    {
        public CellKind Kind;
        /// <summary>Index into HoldTextures.palettes (see HoldPalettes).</summary>
        public byte Palette;
        public CellFlags Flags;
        public byte Reserved;

        public bool Walkable => Kind != CellKind.Solid;
        public static readonly HoldCell Solid = default;
    }

    /// <summary>
    /// A box of cells: Size.x east, Size.y levels (storeys, up), Size.z north. Origin is the world cell of (0,0,0), so
    /// a grid knows where it sits: world position of cell c's floor corner = (Origin + c) * (cell, storey, cell).
    /// Bounded areas (gate, hall) and mine regions are both HoldGrids; the mesher doesn't care which.
    /// Reads outside the box are Solid, so a grid's border always gets walls.
    /// </summary>
    public struct HoldGrid : IDisposable
    {
        public int3 Size;
        public int3 Origin;
        public NativeArray<HoldCell> Cells;

        public HoldGrid(int3 size, int3 origin, Allocator allocator)
        {
            Size = size;
            Origin = origin;
            Cells = new NativeArray<HoldCell>(size.x * size.y * size.z, allocator, NativeArrayOptions.ClearMemory);
        }

        public readonly bool IsCreated => Cells.IsCreated;
        public readonly int Index(int3 c) => c.x + Size.x * (c.z + Size.z * c.y);
        public readonly bool InBounds(int3 c) => math.all(c >= 0) && math.all(c < Size);
        public readonly HoldCell Get(int3 c) => InBounds(c) ? Cells[Index(c)] : HoldCell.Solid;
        public void Set(int3 c, HoldCell cell) { if (InBounds(c)) Cells[Index(c)] = cell; }

        /// <summary>World cell coordinates (for hashing, matching neighbours) of a local cell.</summary>
        public readonly int3 ToWorld(int3 local) => Origin + local;

        public void Dispose() { if (Cells.IsCreated) Cells.Dispose(); }
    }

    /// <summary>A palette resolved to texture-array slices (HoldTextures.layers indices); -1 = layer missing.</summary>
    public struct PaletteLayers
    {
        public int Floor, Wall, Ceiling, Trim, Pillar;
    }

    /// <summary>Palette names -> indices, and palettes -> array slices, from HoldTextures content.</summary>
    public static class HoldPalettes
    {
        /// <summary>Index of a palette by name (HoldCell.Palette), or 0 when there's no such palette.</summary>
        public static byte Find(HoldTextures textures, string name)
        {
            for (var i = 0; i < textures.Palettes.Count; i++)
                if (textures.Palettes[i].Name == name) return (byte)i;
            return 0;
        }

        /// <summary>Every palette's layers as slice indices, in palette order (for the mesher job).</summary>
        public static NativeArray<PaletteLayers> Resolve(HoldTextures textures, Allocator allocator)
        {
            var slice = new Dictionary<string, int>();
            for (var i = 0; i < textures.Layers.Count; i++) slice[textures.Layers[i]] = i;
            int Of(string layer) => !string.IsNullOrEmpty(layer) && slice.TryGetValue(layer, out var s) ? s : -1;
            var result = new NativeArray<PaletteLayers>(math.max(1, textures.Palettes.Count), allocator);
            for (var i = 0; i < textures.Palettes.Count; i++)
            {
                var p = textures.Palettes[i];
                result[i] = new PaletteLayers
                {
                    Floor = Of(p.Floor), Wall = Of(p.Wall), Ceiling = Of(p.Ceiling), Trim = Of(p.Trim), Pillar = Of(p.Pillar),
                };
            }
            return result;
        }
    }
}
