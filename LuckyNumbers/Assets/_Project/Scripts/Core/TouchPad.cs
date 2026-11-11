using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using VContainer.Unity;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// The page's touch controls (web/src/touch.tsx: two sticks and context buttons) as a virtual Gamepad. Every
    /// input update, before the Input System processes events, it reads the page's sticks and button bits
    /// (TouchPad.jslib, a copy into a fixed buffer: no strings, no garbage) and queues them as that gamepad's state,
    /// so the actions' gamepad bindings drive the game: nothing downstream knows it's touch.
    /// While the controls are up it also sets <see cref="PointerLock.Touch"/>.
    /// </summary>
    [Presenter]
    public sealed class TouchPad : IStartable, IDisposable
    {
        /// <summary>The page's button bits (web/src/touch.tsx Btn): one per on-screen button.</summary>
        [Flags]
        public enum Buttons
        {
            None = 0,
            Jump = 1 << 0,      // A / south: jump, lift (hold)
            Descend = 1 << 1,   // right stick press: crouch, descend (hold)
            Interact = 1 << 2,  // Y / north: E
            Fire = 1 << 3,      // right trigger
            Brake = 1 << 4,     // left trigger
            Sprint = 1 << 5,    // left stick press: run, boost
            Map = 1 << 6,       // select: M
            Cancel = 1 << 7,    // start: Esc
            RollLeft = 1 << 8,  // left shoulder
            RollRight = 1 << 9, // right shoulder
            Relic = 1 << 10,    // dpad up: R
            View = 1 << 11,     // dpad down: V
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int LN_TouchRead(float[] axes);
#else
        static int LN_TouchRead(float[] axes) => -1;
#endif

        readonly float[] _axes = new float[4];
        Gamepad _pad;
        bool _on;

        public void Start() => InputSystem.onBeforeUpdate += Read;

        void Read()
        {
            var bits = LN_TouchRead(_axes);
            var on = bits >= 0;
            if (on != _on)
            {
                _on = on;
                PointerLock.Touch = on;
                if (on && _pad == null) _pad = InputSystem.AddDevice<Gamepad>("LNTouchPad");
                Debug.Log($"[Touch] controls {(on ? "on" : "off")}");
                if (!on && _pad != null) InputSystem.QueueStateEvent(_pad, new GamepadState());   // let go of everything
            }
            if (!on || _pad == null) return;
            var b = (Buttons)bits;
            var state = new GamepadState
            {
                leftStick = new Vector2(_axes[0], _axes[1]),
                rightStick = new Vector2(_axes[2], _axes[3]),
                leftTrigger = (b & Buttons.Brake) != 0 ? 1f : 0f,
                rightTrigger = (b & Buttons.Fire) != 0 ? 1f : 0f,
            };
            if ((b & Buttons.Jump) != 0) state = state.WithButton(GamepadButton.South);
            if ((b & Buttons.Descend) != 0) state = state.WithButton(GamepadButton.RightStick);
            if ((b & Buttons.Interact) != 0) state = state.WithButton(GamepadButton.North);
            if ((b & Buttons.Sprint) != 0) state = state.WithButton(GamepadButton.LeftStick);
            if ((b & Buttons.Map) != 0) state = state.WithButton(GamepadButton.Select);
            if ((b & Buttons.Cancel) != 0) state = state.WithButton(GamepadButton.Start);
            if ((b & Buttons.RollLeft) != 0) state = state.WithButton(GamepadButton.LeftShoulder);
            if ((b & Buttons.RollRight) != 0) state = state.WithButton(GamepadButton.RightShoulder);
            if ((b & Buttons.Relic) != 0) state = state.WithButton(GamepadButton.DpadUp);
            if ((b & Buttons.View) != 0) state = state.WithButton(GamepadButton.DpadDown);
            InputSystem.QueueStateEvent(_pad, state);
        }

        public void Dispose()
        {
            InputSystem.onBeforeUpdate -= Read;
            if (_pad != null) InputSystem.RemoveDevice(_pad);
            _pad = null;
            PointerLock.Touch = false;
        }
    }
}
