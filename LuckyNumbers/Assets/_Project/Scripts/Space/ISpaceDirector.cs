using Unity.Mathematics;
using UnityEngine;

namespace LuckyNumbers.Space
{
    /// <summary>
    /// Lets a cutscene work inside the running universe: read where bodies are, and take the camera
    /// (the world, orbits and lighting keep running). Implemented by SpacePresenter.
    /// </summary>
    public interface ISpaceDirector
    {
        /// <summary>Current lucky number (0 before the first roll).</summary>
        uint Seed { get; }
        Camera Camera { get; }
        /// <summary>World position and diameter of a body by id (e.g. "home").</summary>
        bool TryGetBody(string id, out float3 position, out float diameter);
        /// <summary>Closest body to a point (surface distance), for keeping shots clear of other bodies.</summary>
        string Nearest(float3 point, out float surfaceDistance);
        /// <summary>Stop the orbit camera; the caller moves Camera until Release.</summary>
        void TakeCamera();
        void Release();
    }
}
