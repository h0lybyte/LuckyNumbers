using Unity.Mathematics;

namespace LuckyNumbers.Hold
{
    /// <summary>
    /// Passes over a dug mine region (MineGenerator.Region calls Run after the dig is validated), each in its own
    /// file, in this order:
    /// - Vertical (MinePasses.Vertical.cs): stairs between storeys, great shafts through many regions with hoists.
    /// - Puzzles (MinePasses.Puzzles.cs): locked doors and their keys, portcullises and their levers.
    /// - Depth (MinePasses.Depth.cs): depth seals every few regions down, drop shafts, collapsed tunnels.
    /// Each pass is a pure function of (seed, region, rules, the grid so far): deterministic, region-local, and
    /// responsible for leaving the region solvable: every walkable cell reachable from every opening and shaft with
    /// doors/gates open (HoldNav.Flood), and every key/lever reachable without passing what it opens. Anything a
    /// neighbouring region must agree on (a great shaft's column, a seal's band) comes from hashes of shared
    /// coordinates (MineGenerator.Hash), never from this region's dig. Unimplemented passes compile away.
    /// </summary>
    public static partial class MinePasses
    {
        public static void Run(uint seed, int3 region, in MineParams p, ref HoldGrid grid)
        {
            Vertical(seed, region, p, ref grid);
            Puzzles(seed, region, p, ref grid);
            Depth(seed, region, p, ref grid);
        }

        static partial void Vertical(uint seed, int3 region, in MineParams p, ref HoldGrid grid);
        static partial void Puzzles(uint seed, int3 region, in MineParams p, ref HoldGrid grid);
        static partial void Depth(uint seed, int3 region, in MineParams p, ref HoldGrid grid);
    }
}
