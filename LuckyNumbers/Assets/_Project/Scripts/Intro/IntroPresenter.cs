using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LuckyNumbers.Model;
using LuckyNumbers.Space;
using LuckyNumbers.Title;
using MessagePipe;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Intro
{
    /// <summary>
    /// Plays content/intro.txtpb inside the player's generated universe (ISpaceDirector): the dice show
    /// their roll, the ship is pulled into "home" on a tightening spiral, crashes, then the dialogue line.
    /// Motion is all eased curves in the planet's moving frame, so it works for any seed.
    /// </summary>
    [Presenter]
    public sealed class IntroPresenter : IStartable, IDisposable
    {
        const string Target = "home";

        readonly ISpaceDirector _space;
        readonly IntroView _view;
        readonly ShipView _ship;
        readonly DiceView _dice;
        readonly DiceState _diceState;
        readonly IPublisher<SceneChangeRequested> _scenes;
        readonly IPublisher<CutsceneStepStarted> _stepStarted;
        readonly IPublisher<CutsceneFinished> _finished;
        readonly CancellationTokenSource _cts = new();

        Cutscene _cut;
        ShipPulledIn _pull;
        Random _rng;
        float3 _axisA, _axisB, _axisUp;   // spiral frame around the planet (fixed at start)
        Vector3 _camPos;                  // chase camera, smoothed
        Quaternion _camRot;
        float _shake;

        [Inject]
        public IntroPresenter(ISpaceDirector space, IntroView view, ShipView ship, DiceView dice, DiceState diceState,
                              IPublisher<SceneChangeRequested> scenes, IPublisher<CutsceneStepStarted> stepStarted,
                              IPublisher<CutsceneFinished> finished)
        {
            _space = space;
            _view = view;
            _ship = ship;
            _dice = dice;
            _diceState = diceState;
            _scenes = scenes;
            _stepStarted = stepStarted;
            _finished = finished;
        }

        public void Start() => PlayAsync(_cts.Token).Forget();

        async UniTaskVoid PlayAsync(CancellationToken ct)
        {
            _cut = GameContent.Intro;
            _view.Bind();
            _view.Flash(Color.black, 1f);
            _view.Dialogue(null, null, 0);
            _ship.Hide();
            // The universe arrives via the buffered lucky number; wait until the home planet exists.
            while (!_space.TryGetBody(Target, out _, out _)) await UniTask.Yield(ct);
            await UniTask.NextFrame(ct);   // let OrbitSystem place the bodies (they start at the origin)
            await UniTask.NextFrame(ct);

            _rng = new Random(_space.Seed | 1u);
            _pull = FindPull() ?? new ShipPulledIn { DurationSeconds = 6, StartDistance = 4, Turns = 1, BankDegrees = 50, CameraRelease = 0.2f };
            SetupSpiral();
            _space.TakeCamera();
            _space.TryGetBody(Target, out _, out var diameter);
            var length = diameter * math.max(0.005f, _cut.ShipSize);
            _ship.Show(_cut.ShipSkin, (int)(_space.Seed % (uint)math.max(1, _ship.SkinCount)), length);
            _space.Camera.nearClipPlane = math.max(0.002f, length * 0.2f);   // the camera sits a few ship-lengths away
            PoseShip(0f, 0f, out var shipPos, out var shipRot);
            SnapCamera(shipPos, shipRot);
            _space.TryGetBody(Target, out var homePos, out _);
            var near = _space.Nearest(_camPos, out var gap);
            Debug.Log($"[Intro] home {homePos} d={diameter:0.##}, ship {shipPos}, camera {_camPos} (nearest {near}, {gap:0.##} from its surface)");

            for (var i = 0; i < _cut.Steps.Count && !ct.IsCancellationRequested; i++)
            {
                var step = _cut.Steps[i];
                _stepStarted.Publish(new CutsceneStepStarted(_cut.Id, i));
                Debug.Log($"[Intro] step {i}: {step.Step}");
                switch (step.Step)
                {
                    case CutsceneStep.StepCase.DiceRoll: await DiceRollAsync(step.DiceRoll, ct); break;
                    case CutsceneStep.StepCase.ShipPulledIn: await PullInAsync(step.ShipPulledIn, ct); break;
                    case CutsceneStep.StepCase.CrashLanding: await CrashAsync(step.CrashLanding, ct); break;
                    case CutsceneStep.StepCase.Dialogue: await DialogueAsync(step.Dialogue, ct); break;
                    case CutsceneStep.StepCase.GoTo:
                        _finished.Publish(new CutsceneFinished(_cut.Id));
                        _space.Release();
                        _scenes.Publish(new SceneChangeRequested(step.GoTo.Scene));
                        return;
                }
            }
            _finished.Publish(new CutsceneFinished(_cut.Id));
        }

        ShipPulledIn FindPull()
        {
            foreach (var s in _cut.Steps) if (s.ShipPulledIn != null) return s.ShipPulledIn;
            return null;
        }

        // ---- Steps -------------------------------------------------------------------------------------

        /// <summary>Fade in on the ship drifting toward the planet; the player's dice tumble and land on their roll.</summary>
        async UniTask DiceRollAsync(DiceRoll roll, CancellationToken ct)
        {
            var dice = GameContent.Title.Dice;
            var faces = new List<int>();
            if (roll.Faces.Count > 0) faces.AddRange(roll.Faces);
            else faces.AddRange(_diceState.Faces);           // keep the player's universe
            var built = dice != null && dice.Dice.Count > 0;
            if (built)
            {
                _dice.Bind();
                _dice.Build(dice.Dice, null, null);
                _dice.SetLuckyNumber(dice.SeedFormat.Replace("{0}", _space.Seed.ToString()));
                _view.DiceVisible(true);
            }

            var duration = math.max(0.5f, roll.DurationSeconds);
            var tumbleEnd = duration * 0.55f;
            var nextFace = 0f;
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                _view.Flash(Color.black, 1f - math.smoothstep(0f, 0.8f, t));   // fade in from black
                if (built && t < tumbleEnd && t >= nextFace)
                {
                    nextFace = t + math.lerp(0.05f, 0.18f, t / tumbleEnd);      // tumbling slows down
                    for (var i = 0; i < _dice.Count; i++)
                        _dice.ShowFace(i, _rng.NextInt(1, 7), _rng.NextFloat(-40f, 40f) * (1f - t / tumbleEnd));
                }
                else if (built && t >= tumbleEnd)
                    for (var i = 0; i < _dice.Count && i < faces.Count; i++) _dice.ShowFace(i, faces[i], 0f);
                Drift(t);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            _view.DiceVisible(false);
        }

        /// <summary>Gravity takes the ship: a tightening, accelerating spiral; camera chases, then lets it fall.</summary>
        async UniTask PullInAsync(ShipPulledIn pull, CancellationToken ct)
        {
            var duration = math.max(0.5f, pull.DurationSeconds);
            var release = 1f - math.saturate(pull.CameraRelease);
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                var u = t / duration;
                PoseShip(u, t, out var pos, out var rot);
                _ship.SetHeat(math.smoothstep(0.6f, 1f, u), 1f + 1.5f * math.smoothstep(0f, 0.3f, u));
                _shake = math.smoothstep(0.55f, 1f, u) * 0.25f;                    // turbulence as it hits air
                if (u < release) Chase(pos, rot, math.lerp(1f, 0.6f, u / release));
                else WatchFall(pos);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
        }

        /// <summary>Impact: white flash, big shake that dies down, fade to black.</summary>
        async UniTask CrashAsync(CrashLanding crash, CancellationToken ct)
        {
            _ship.Hide();
            var duration = math.max(0.3f, crash.DurationSeconds);
            var flash = math.max(0.02f, crash.FlashSeconds);
            Debug.Log("[Intro] impact");
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                _shake = crash.CameraShake * math.pow(1f - t / duration, 2f);
                if (t < flash) _view.Flash(Color.white, 1f);
                else _view.Flash(Color.Lerp(Color.white, Color.black, math.saturate((t - flash) / 0.35f)),
                                 math.max(1f - (t - flash) / 0.25f, math.smoothstep(0.35f, 1f, t / duration)));
                ApplyCamera(_camPos, _camRot);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            _shake = 0f;
            _view.Flash(Color.black, 1f);
        }

        /// <summary>On black: the line types out, holds, clears.</summary>
        async UniTask DialogueAsync(Dialogue line, CancellationToken ct)
        {
            var text = line.Text ?? "";
            var duration = math.max(0.5f, line.DurationSeconds);
            var cps = line.CharsPerSecond;
            for (var t = 0f; t < duration; t += Time.deltaTime)
            {
                _view.Dialogue(line.Speaker, text, cps > 0f ? (int)(t * cps) : text.Length);
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate, ct);
            }
            _view.Dialogue(line.Speaker, text, text.Length);
            await UniTask.Delay(400, cancellationToken: ct);
            _view.Dialogue(null, null, 0);
        }

        // ---- Ship path ---------------------------------------------------------------------------------

        /// <summary>
        /// Spiral frame, solved backwards: the fall ends ~30 degrees from the sun-facing point, so the ship
        /// stays over the lit side (visible against the planet) and crashes in daylight.
        /// </summary>
        void SetupSpiral()
        {
            _space.TryGetBody(Target, out var home, out _);
            var toSun = _space.TryGetBody("sun", out var sun, out _) ? math.normalizesafe(sun - home, new float3(1, 0, 0)) : new float3(1, 0, 0);
            var up = math.normalizesafe(new float3(0, 1, 0) - toSun * math.dot(toSun, new float3(0, 1, 0)), new float3(0, 0, 1));
            var end = (float3)(Quaternion.AngleAxis(30f, (Vector3)up) * (Vector3)toSun);
            var sweep = _pull.Turns * 360f;
            _axisUp = up;
            var best = float.MinValue;
            float3 bestA = end, bestB = math.cross(up, end);
            foreach (var sign in new[] { 1f, -1f })   // pick the winding whose path ends on `end`
            {
                _axisA = math.normalizesafe((float3)(Quaternion.AngleAxis(sign * sweep, (Vector3)up) * (Vector3)end));
                _axisB = math.normalizesafe(math.cross(up, _axisA));
                var score = math.dot(math.normalizesafe(Offset(1f, 1f)), end);
                if (score > best) { best = score; bestA = _axisA; bestB = _axisB; }
            }
            _axisA = bestA;
            _axisB = bestB;
        }

        /// <summary>Local offset from the planet centre at path progress u (0 start .. 1 surface).</summary>
        float3 Offset(float u, float diameter)
        {
            var r0 = diameter * math.max(1f, _pull.StartDistance);
            var surface = diameter * 0.5f;
            var fall = math.pow(math.saturate(u), 2.2f);                       // slow, then the plunge
            var r = math.lerp(r0, surface * 0.98f, fall);
            var theta = _pull.Turns * 2f * math.PI * math.pow(math.saturate(u), 1.5f); // spins up as it falls
            var height = r0 * 0.12f * (1f - fall);
            return _axisA * (r * math.cos(theta)) + _axisB * (r * math.sin(theta)) + _axisUp * height;
        }

        void PoseShip(float u, float time, out Vector3 position, out Quaternion rotation)
        {
            _space.TryGetBody(Target, out var home, out var diameter);
            var p = home + Offset(u, diameter);
            var ahead = home + Offset(u + 0.004f, diameter);
            var forward = math.normalizesafe(ahead - p, _axisB);
            var toCentre = math.normalizesafe(home - p);
            var bank = math.radians(_pull.BankDegrees) * math.smoothstep(0f, 0.7f, u);
            var up = math.normalizesafe(_axisUp * math.cos(bank) + toCentre * math.sin(bank));
            // Turbulence: small noisy pitch/yaw growing with the fall, plus a lazy idle bob.
            var wob = 8f * math.smoothstep(0.5f, 1f, u);
            var noise = new Vector3(Mathf.PerlinNoise(time * 3.1f, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, time * 2.7f) - 0.5f, 0f) * wob;
            rotation = Quaternion.LookRotation(forward, up) * Quaternion.Euler(noise.x, noise.y, 2f * math.sin(time * 1.3f));
            position = p;
            _ship.Pose(position, rotation);
        }

        /// <summary>Before the pull: the ship coasts in slowly at the spiral start.</summary>
        void Drift(float t)
        {
            PoseShip(0f, t, out var pos, out var rot);
            _ship.SetHeat(0f, 1f + 0.15f * math.sin(t * 6f));
            Chase(pos, rot, 1.15f);
        }

        // ---- Camera ------------------------------------------------------------------------------------

        const float ShipMaxOffCentreDegrees = 14f;   // keep the ship in frame (FOV 45) while looking toward the planet

        /// <summary>
        /// Over-the-shoulder shot from the far side: the ship in front, the planet looming behind it. Aims
        /// between the two without letting the ship leave the frame. Lags so turns feel weighty.
        /// </summary>
        void Chase(Vector3 shipPos, Quaternion shipRot, float distanceScale)
        {
            _space.TryGetBody(Target, out var home, out var diameter);
            var length = diameter * math.max(0.005f, _cut.ShipSize);
            var outward = ((Vector3)shipPos - (Vector3)home).normalized;
            // Over the shoulder: out beyond the ship (planet behind it) and a little behind its heading.
            var wantPos = shipPos + outward * (length * 3.6f * distanceScale)
                        + shipRot * Vector3.back * (length * 1.6f * distanceScale) + (Vector3)_axisUp * (length * 0.9f);
            var toShip = (shipPos + shipRot * Vector3.forward * length - wantPos).normalized;
            var toHome = ((Vector3)home - wantPos).normalized;
            var angle = Vector3.Angle(toShip, toHome);
            var aim = Vector3.Slerp(toShip, toHome, angle > 0.01f ? math.min(0.6f, ShipMaxOffCentreDegrees / angle) : 0f);
            var wantRot = Quaternion.LookRotation(aim, (Vector3)_axisUp);
            var k = 1f - math.exp(-8f * Time.deltaTime);
            _camPos = Vector3.Lerp(_camPos, wantPos, k);
            _camRot = Quaternion.Slerp(_camRot, wantRot, k);
            KeepInFrame(shipPos, 18f);
            ApplyCamera(_camPos, _camRot);
        }

        /// <summary>Camera stops, keeps the ship in frame as it falls away into the planet.</summary>
        void WatchFall(Vector3 shipPos)
        {
            var k = 1f - math.exp(-4f * Time.deltaTime);
            _camRot = Quaternion.Slerp(_camRot, Quaternion.LookRotation(shipPos - _camPos, _camRot * Vector3.up), k);
            KeepInFrame(shipPos, 15f);
            ApplyCamera(_camPos, _camRot);
        }

        /// <summary>Lag is nice until the subject leaves the frame: turn just enough to keep it within maxDegrees.</summary>
        void KeepInFrame(Vector3 subject, float maxDegrees)
        {
            var forward = _camRot * Vector3.forward;
            var toSubject = (subject - _camPos).normalized;
            var off = Vector3.Angle(forward, toSubject);
            if (off <= maxDegrees) return;
            var fix = Quaternion.FromToRotation(forward, Vector3.Slerp(forward, toSubject, 1f - maxDegrees / off));
            _camRot = fix * _camRot;
        }

        void SnapCamera(Vector3 shipPos, Quaternion shipRot)
        {
            _camPos = shipPos;
            _camRot = shipRot;
            for (var i = 0; i < 60; i++) Chase(shipPos, shipRot, 1.15f); // settle the smoothing
        }

        void ApplyCamera(Vector3 position, Quaternion rotation)
        {
            var t = Time.time;
            var shake = _shake * new Vector3(Mathf.PerlinNoise(t * 23f, 1.7f) - 0.5f, Mathf.PerlinNoise(4.2f, t * 21f) - 0.5f,
                                             Mathf.PerlinNoise(t * 19f, 8.9f) - 0.5f) * 6f;
            _space.Camera.transform.SetPositionAndRotation(position, rotation * Quaternion.Euler(shake));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
