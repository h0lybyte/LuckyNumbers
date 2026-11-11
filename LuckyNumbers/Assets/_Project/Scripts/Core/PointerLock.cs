using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The mouse pointer lock, in one place. Browsers lock it only on a user gesture, so locking is always "a press
    /// locks it"; Esc (the browser) or Release frees it. Who may lock it is the caller's business (on foot: only while
    /// no ControlLock is held; a lock that frees the pointer releases it).
    /// </summary>
    public static class PointerLock
    {
        public static bool Locked => Cursor.lockState == CursorLockMode.Locked;

        /// <summary>
        /// A press locks the pointer. True while the mouse steers: locked, or held down (where the browser refuses the
        /// lock, or headless, you steer by dragging).
        /// </summary>
        public static bool Steer(bool pressed)
        {
            if (pressed && !Locked) Cursor.lockState = CursorLockMode.Locked;
            return Locked || pressed;
        }

        public static void Release() => Cursor.lockState = CursorLockMode.None;
    }
}
