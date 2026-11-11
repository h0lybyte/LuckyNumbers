using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>A Projectile (content: a gun's `bolt`) with the shooter's defaults filled in for anything left unset.</summary>
    public static class Projectiles
    {
        /// <summary>Its colour, or `fallback` when the content left it black.</summary>
        public static Color Tint(this Projectile p, Color fallback)
        {
            var c = p?.Color ?? default;
            return c.R + c.G + c.B > 0f ? new Color(c.R, c.G, c.B, 1f) : fallback;
        }

        /// <summary>A flash's (diameter in metres, seconds): its content, else `size` widths for `life` seconds.</summary>
        public static (float size, float life) Of(this ProjectileFlash f, float width, float size, float life) =>
            (width * (f is { Size: > 0f } ? f.Size : size), f is { Life: > 0f } ? f.Life : life);

        /// <summary>Its prewarm, else `fallback`.</summary>
        public static int PrewarmOr(this Projectile p, int fallback) => p is { Prewarm: > 0 } ? p.Prewarm : fallback;
    }
}
