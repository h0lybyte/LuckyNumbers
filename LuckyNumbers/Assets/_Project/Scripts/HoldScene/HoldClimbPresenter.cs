using System;
using LuckyNumbers.Hold;
using LuckyNumbers.Model;
using LuckyNumbers.Planet;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// Climbing shaft ladders (HoldMesher draws them; HoldRules.LadderSide says which wall). In a shaft cell (ShaftUp,
    /// or the ShaftDown above it, falling in) within Reach of the ladder, facing it, W grabs it
    /// (falling into the shaft facing the ladder, you catch it). On it
    /// (FirstPersonPresenter.Climbing: no gravity, no walking): W up / S down at climb_speed, look free; at the top you
    /// step off onto the floor beside the shaft (opposite the ladder if there's floor there, else to a side); at the
    /// bottom S puts you on the floor; Space lets go (you fall, and can't grab again until you land).
    /// </summary>
    [Presenter]
    public sealed class HoldClimbPresenter : IStartable, ITickable, IDisposable
    {
        const string ClimbHint = "W/S climb · Space let go";
        const float Reach = 0.8f;            // metres from the body's axis to the ladder's front
        const float FacingCos = 0.6f;        // within ~53 degrees of looking at the wall
        const float Standoff = 0.12f;        // gap between the body's capsule and the ladder's front
        const float StepLift = 0.15f;        // feet over the upper floor when stepping off
        const float StepSpeed = 2.5f;
        const float StepTimeout = 2f;

        enum State { Off, On, Stepping }

        readonly HoldWorld _world;
        readonly FirstPersonPresenter _firstPerson;
        readonly PlayerView _player;
        readonly PlayerInput _input;
        readonly ISubscriber<InputActionPerformed> _actions;
        IDisposable _sub;
        bool _jump, _letGo, _hasExit, _waitW;
        State _state;
        int3 _shaft, _side, _exit;   // world cells: the ShaftUp cell, the ladder wall direction, where to step off
        float _stepFor;

        [Inject]
        public HoldClimbPresenter(HoldWorld world, FirstPersonPresenter firstPerson, PlayerView player, PlayerInput input,
                                  ISubscriber<InputActionPerformed> actions)
        {
            _world = world;
            _firstPerson = firstPerson;
            _player = player;
            _input = input;
            _actions = actions;
        }

        public void Start() => _sub = _actions.Subscribe(e => { if (e.Action == InputAction.Jump) _jump = true; });

        float ClimbSpeed => _world.Config.ClimbSpeed > 0f ? _world.Config.ClimbSpeed : 2.2f;
        float Storey => _world.CellSize.y;

        public void Tick()
        {
            var jump = _jump;
            _jump = false;
            if (!_firstPerson.Active) return;   // a menu or a fade has you: hold on where you are
            var body = _player.Body;
            var pos = (float3)body.transform.position;
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            switch (_state)
            {
                case State.Off:
                    if (body.isGrounded) _letGo = false;
                    if (_letGo || !FindLadder(pos, out var shaft, out var side)) return;
                    var forward = (float3)body.transform.forward;
                    if (Front(pos, shaft, side) > Reach || math.dot(math.normalizesafe(forward.xz), new float2(side.x, side.z)) < FacingCos) return;
                    _world.Prompt = ClimbHint;
                    // On your feet, W takes hold; falling in (stepped or dropped into the shaft) you catch it, and
                    // W does nothing until you let go of it (else walking in with W held climbs you straight back out).
                    if (!body.isGrounded) { Grab(shaft, side); _waitW = true; }
                    else if (_input.Move.Value.Y > 0.5f) Grab(shaft, side);
                    return;

                case State.On:
                    _world.Prompt = ClimbHint;
                    if (jump) { Release(); _letGo = true; return; }
                    Climb(body, pos, dt);
                    return;

                case State.Stepping:
                    if (jump) { Release(); _letGo = true; return; }
                    StepOff(body, pos, dt);
                    return;
            }
        }

        /// <summary>The shaft (its ShaftUp cell) and ladder wall at a position in its ShaftUp or ShaftDown cell.</summary>
        bool FindLadder(float3 pos, out int3 shaft, out int3 side)
        {
            side = int3.zero;
            shaft = _world.CellOf(pos + new float3(0f, 0.2f, 0f));
            if (!_world.TryCell(shaft, out var cell)) return false;
            if (cell.Kind == CellKind.ShaftDown)
            {
                shaft -= new int3(0, 1, 0);
                if (!_world.TryCell(shaft, out cell)) return false;
            }
            if (cell.Kind != CellKind.ShaftUp) return false;
            // The ladder is the ShaftUp cell's grid's (HoldMesher asks the same grid, so we agree on the wall).
            if (!_world.TryGrid(_world.RegionOfCell(shaft), out var grid)) return false;
            side = HoldRules.LadderSide(grid, shaft - grid.Origin);
            return math.any(side != 0);
        }

        /// <summary>Horizontal metres from a point to the ladder's front face (towards the ladder wall).</summary>
        float Front(float3 pos, int3 shaft, int3 side)
        {
            var axis = side.x != 0 ? 0 : 2;
            var s = side[axis];
            var wall = (shaft[axis] + (s > 0 ? 1 : 0)) * _world.CellSize[axis];
            return s * (wall - s * HoldRules.LadderDepth - pos[axis]);
        }

        /// <summary>Where the body's feet stay while on the ladder (x, z): the wall's middle, standing off the rungs.</summary>
        float2 HoldPoint(int3 shaft, int3 side)
        {
            var axis = side.x != 0 ? 0 : 2;
            var s = side[axis];
            var centre = _world.CellFloor(shaft);
            var wall = (shaft[axis] + (s > 0 ? 1 : 0)) * _world.CellSize[axis];
            centre[axis] = wall - s * (HoldRules.LadderDepth + _player.Body.radius + Standoff);
            return centre.xz;
        }

        void Grab(int3 shaft, int3 side)
        {
            _state = State.On;
            _shaft = shaft;
            _side = side;
            _firstPerson.Climbing = true;
            _waitW = false;
            // Step-off cell on the upper level: away from the ladder first, then either side.
            var upper = shaft + new int3(0, 1, 0);
            _hasExit = false;
            foreach (var d in new[] { -side, new int3(side.z, 0, -side.x), new int3(-side.z, 0, side.x) })
            {
                if (!_world.TryCell(upper + d, out var c) || !HoldRules.StepOff(c)) continue;
                _exit = upper + d;
                _hasExit = true;
                break;
            }
            Debug.Log($"[Hold] on the ladder of shaft {shaft} (wall {side}, step off {(_hasExit ? _exit.ToString() : "none")})");
        }

        void Climb(CharacterController body, float3 pos, float dt)
        {
            var floorY = _shaft.y * Storey;
            var topY = floorY + Storey + StepLift;
            var w = math.clamp(_input.Move.Value.Y, -1f, 1f);
            if (_waitW && w <= 0.1f) _waitW = false;
            var climb = (_waitW ? 0f : w) * ClimbSpeed;
            var to = HoldPoint(_shaft, _side) - pos.xz;
            var dxz = to * (1f - math.exp(-12f * dt));
            var y = math.min(pos.y + climb * dt, topY);
            body.Move(new Vector3(dxz.x, y - pos.y, dxz.y));

            var now = body.transform.position.y;
            if (climb > 0f && now >= topY - 0.01f && _hasExit) { _state = State.Stepping; _stepFor = 0f; return; }
            if (climb < 0f && ((body.collisionFlags & CollisionFlags.Below) != 0 || now <= floorY + 0.02f)) Release();   // on the floor
        }

        /// <summary>Over the top: walk off the ladder onto the exit cell, then gravity again (a short drop onto it).</summary>
        void StepOff(CharacterController body, float3 pos, float dt)
        {
            _stepFor += dt;
            var target = _world.CellFloor(_exit).xz;
            var to = target - pos.xz;
            var step = math.normalizesafe(to) * math.min(math.length(to), StepSpeed * dt);
            var topY = _shaft.y * Storey + Storey + StepLift;
            body.Move(new Vector3(step.x, topY - pos.y, step.y));
            if (ClearOfShaft(body.transform.position, body.radius) || _stepFor > StepTimeout) Release();
        }

        /// <summary>The body's footprint is wholly outside the shaft's column (so it lands on floor, not back in).</summary>
        bool ClearOfShaft(float3 p, float radius)
        {
            var size = _world.CellSize;
            float2 lo = new float2(_shaft.x, _shaft.z) * size.xz, hi = lo + size.xz;
            var gap = math.max(math.max(lo - p.xz, p.xz - hi), 0f);
            return math.length(gap) >= radius + 0.05f;
        }

        void Release()
        {
            _state = State.Off;
            _firstPerson.Climbing = false;
        }

        public void Dispose()
        {
            _sub?.Dispose();
            if (_state != State.Off) _firstPerson.Climbing = false;
        }
    }
}
