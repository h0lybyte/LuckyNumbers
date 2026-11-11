using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The mouse pointer lock, in one place. Browsers lock it only on a user gesture, so locking is always "a press
    /// locks it"; Esc (the browser) or Release frees it. Who may lock it is the caller's business (on foot: only while
    /// no ControlLock is held; a lock that frees the pointer releases it).
    ///
    /// On a touch screen (<see cref="Touch"/>, the page's on-screen sticks, TouchPad) there is no pointer to lock: the
    /// game always has the controls, so it counts as locked and never asks the browser for a lock. A gamepad's right
    /// stick steers too (<see cref="StickLook"/>), without any press.
    /// </summary>
    public static class PointerLock
    {
        /// <summary>The page's touch controls are up (TouchPad): no pointer lock, the sticks steer.</summary>
        public static bool Touch { get; set; }

        /// <summary>A right stick is being pushed this frame (InputBridge): look applies without a press.</summary>
        public static bool StickLook { get; set; }

        public static bool Locked => Touch || Cursor.lockState == CursorLockMode.Locked;

        /// <summary>
        /// A press locks the pointer. True while look should steer: locked (or touch), a stick pushed, or the mouse
        /// held down (where the browser refuses the lock, or headless, you steer by dragging).
        /// </summary>
        public static bool Steer(bool pressed)
        {
            if (Touch) return true;
            if (pressed && Cursor.lockState != CursorLockMode.Locked) Cursor.lockState = CursorLockMode.Locked;
            return Cursor.lockState == CursorLockMode.Locked || pressed || StickLook;
        }

        public static void Release()
        {
            if (!Touch) Cursor.lockState = CursorLockMode.None;
        }
    }
}
