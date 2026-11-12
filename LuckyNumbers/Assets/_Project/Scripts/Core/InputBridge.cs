using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using VContainer;
using VContainer.Unity;
using ModelInputAction = LuckyNumbers.Model.InputAction;
using ModelPlayerInput = LuckyNumbers.Model.PlayerInput;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Unity Input System -> model: button actions publish InputActionPerformed, axes feed PlayerInput.
    /// Bindings live in the project-wide actions asset (Settings/InputSystem_Actions).
    /// </summary>
    [Presenter]
    public sealed class InputBridge : IStartable, ITickable, IDisposable
    {
        static readonly (string path, ModelInputAction action)[] Buttons =
        {
            ("UI/Submit", ModelInputAction.Confirm),
            ("UI/Cancel", ModelInputAction.Cancel),
            ("Player/Jump", ModelInputAction.Jump),
            ("Player/Interact", ModelInputAction.Interact),
            ("Player/Sprint", ModelInputAction.Sprint),
            ("Player/Map", ModelInputAction.Map),
            ("Player/UseRelic", ModelInputAction.UseRelic),
            ("Player/SideView", ModelInputAction.SideView),
        };

        readonly IPublisher<InputActionPerformed> _publisher;
        readonly ModelPlayerInput _input;
        readonly GameSettings _settings;
        readonly List<IDisposable> _subscriptions = new();
        UnityEngine.InputSystem.InputAction _move, _look, _sprint, _jump, _crouch, _roll, _brake, _attack;

        /// <summary>A right stick pushed all the way turns like the mouse moving this many pixels a second.</summary>
        const float StickLookPixelsPerSecond = 900f;

        [Inject]
        public InputBridge(IPublisher<InputActionPerformed> publisher, ModelPlayerInput input, GameSettings settings)
        {
            _settings = settings;
            _publisher = publisher;
            _input = input;
        }

        public void Start()
        {
            var actions = InputSystem.actions;
            foreach (var (path, action) in Buttons)
            {
                var a = actions?.FindAction(path);
                if (a == null) continue;
                void Performed(UnityEngine.InputSystem.InputAction.CallbackContext _) =>
                    _publisher.Publish(new InputActionPerformed(action));
                a.performed += Performed;
                a.Enable();
                _subscriptions.Add(new Unsubscribe(() => a.performed -= Performed));
            }
            _subscriptions.Add(InputSystem.onAnyButtonPress.Call(_ =>
                _publisher.Publish(new InputActionPerformed(ModelInputAction.AnyButton))));

            _move = actions?.FindAction("Player/Move");
            _sprint = actions?.FindAction("Player/Sprint");
            _sprint?.Enable();
            _jump = actions?.FindAction("Player/Jump");
            _crouch = actions?.FindAction("Player/Crouch");
            _crouch?.Enable();
            _roll = actions?.FindAction("Player/Roll");
            _roll?.Enable();
            _brake = actions?.FindAction("Player/Brake");
            _brake?.Enable();
            _look = actions?.FindAction("Player/Look");
            _attack = actions?.FindAction("Player/Attack");   // a gamepad's (or the touch pad's) fire: counts as a press
            _attack?.Enable();
            _move?.Enable();
            _look?.Enable();
        }

        public void Tick()
        {
            if (_move != null) { var v = _move.ReadValue<UnityEngine.Vector2>(); _input.Move.Value = new Vec2(v.x, v.y); }
            _input.Sprinting.Value = _sprint != null && _sprint.IsPressed();
            _input.Lifting.Value = _jump != null && _jump.IsPressed();
            _input.Descending.Value = _crouch != null && _crouch.IsPressed();
            _input.Roll.Value = _roll != null ? _roll.ReadValue<float>() : 0f;
            _input.Braking.Value = _brake != null && _brake.IsPressed();
            // Look: the mouse's pixels (the action), plus any right stick as pixels a second (the action leaves the
            // sticks out: their units differ). A pushed stick steers without a press (PointerLock.StickLook).
            var look = _look != null ? _look.ReadValue<UnityEngine.Vector2>() : UnityEngine.Vector2.zero;
            var stick = UnityEngine.Vector2.zero;
            foreach (var pad in Gamepad.all)
            {
                var s = pad.rightStick.ReadValue();
                if (s.sqrMagnitude > stick.sqrMagnitude) stick = s;
            }
            if (stick.sqrMagnitude < 0.01f) stick = UnityEngine.Vector2.zero;   // dead zone
            PointerLock.StickLook = stick != UnityEngine.Vector2.zero;
            look += stick * (StickLookPixelsPerSecond * UnityEngine.Time.unscaledDeltaTime);
            // The player's look speed and invert (GameSettings): every scene reads Look, so they apply everywhere.
            look *= _settings.LookSensitivity.Value > 0f ? _settings.LookSensitivity.Value : 1f;
            if (_settings.InvertY.Value) look.y = -look.y;
            _input.Look.Value = new Vec2(look.x, look.y);

            var pointer = Pointer.current; // mouse, pen or touch
            if (pointer != null)
            {
                var pos = pointer.position.ReadValue();
                var delta = pointer.delta.ReadValue();
                _input.PointerPosition.Value = new Vec2(pos.x, pos.y);
                _input.PointerDelta.Value = new Vec2(delta.x, delta.y);
                // On touch the screen itself doesn't fire (the on-screen button does, as a trigger): a press there is
                // the sticks' or the page's, not an aim.
                _input.PointerPressed.Value = (!PointerLock.Touch && pointer.press.isPressed) || (_attack != null && _attack.IsPressed());
            }
            else _input.PointerPressed.Value = _attack != null && _attack.IsPressed();
            _input.PointerScroll.Value = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }

        sealed class Unsubscribe : IDisposable
        {
            Action _action;
            public Unsubscribe(Action action) => _action = action;
            public void Dispose() { _action?.Invoke(); _action = null; }
        }
    }
}
