using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Render layers (ProjectSettings/TagManager). `Local`: the scene's own things outside the station (the hull,
    /// ships, satellite, bolts), drawn by the flight camera only; the space camera, parked far off at the station's
    /// point in the universe, skips them (else it draws a second, turned copy of everything near the origin).
    /// </summary>
    public static class Layers
    {
        public const int Local = 10;

        /// <summary>`go` and everything under it onto `layer`.</summary>
        public static void Set(GameObject go, int layer)
        {
            if (go == null) return;
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }
    }
}
