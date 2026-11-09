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
        };

        readonly IPublisher<InputActionPerformed> _publisher;
        readonly ModelPlayerInput _input;
        readonly List<IDisposable> _subscriptions = new();
        UnityEngine.InputSystem.InputAction _move, _look, _sprint;

        [Inject]
        public InputBridge(IPublisher<InputActionPerformed> publisher, ModelPlayerInput input)
        {
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
            _look = actions?.FindAction("Player/Look");
            _move?.Enable();
            _look?.Enable();
        }

        public void Tick()
        {
            if (_move != null) { var v = _move.ReadValue<UnityEngine.Vector2>(); _input.Move.Value = new Vec2(v.x, v.y); }
            _input.Sprinting.Value = _sprint != null && _sprint.IsPressed();
            if (_look != null) { var v = _look.ReadValue<UnityEngine.Vector2>(); _input.Look.Value = new Vec2(v.x, v.y); }

            var pointer = Pointer.current; // mouse, pen or touch
            if (pointer != null)
            {
                var pos = pointer.position.ReadValue();
                var delta = pointer.delta.ReadValue();
                _input.PointerPosition.Value = new Vec2(pos.x, pos.y);
                _input.PointerDelta.Value = new Vec2(delta.x, delta.y);
                _input.PointerPressed.Value = pointer.press.isPressed;
            }
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
