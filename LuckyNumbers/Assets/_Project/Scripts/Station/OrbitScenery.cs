using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Space round the station, alive (content/orbit.txtpb life; looks only, nothing here can be hit or hits you):
    /// traffic ships crossing far off (copies of the fighter in its skins, a small pool, no hitboxes), debris
    /// tumbling in a shell round the hull (PLACEHOLDER_debris: one instanced draw of cubes), the hull's navigation
    /// lights blinking in turn and its radar mast turning (OrbitView.Dressing). No per-frame allocations.
    /// </summary>
    public sealed class OrbitScenery : IDisposable
    {
        sealed class Ship
        {
            public GameObject Go;
            public ShipView View;
            public Vector3 From, Dir;
            public float Speed, Age, Life, Roll;
            public bool Active;
        }

        readonly OrbitLife _c;
        readonly List<Ship> _ships = new();
        readonly OrbitView _view;
        readonly GameObject _template;
        readonly Vector3 _centre;
        readonly float _length, _hullRadius, _range;
        readonly int _layer;
        Random _rng;
        float _nextShip, _time;

        // Debris: rest positions round the centre (orbiting slowly about its up), tumble axes and rates, sizes.
        readonly Vector3[] _debris;
        readonly Vector3[] _axes;
        readonly float[] _spins, _sizes, _drift;
        readonly Matrix4x4[] _matrices;
        readonly Material _debrisMaterial;
        readonly Mesh _cube;
        readonly GameObject _cubeSource;

        /// <summary>`cfg` null: nothing moves (Tick does nothing).</summary>
        public OrbitScenery(OrbitLife cfg, OrbitView view, GameObject shipTemplate, IReadOnlyList<Bounds> hull, Vector3 centre,
                         float length, float range, uint seed, int layer)
        {
            _c = cfg;
            _view = view;
            _template = shipTemplate;
            _centre = centre;
            _length = math.max(1f, length);
            _range = math.max(5f, range) * _length;
            _layer = layer;
            _rng = new Random((seed ^ 0x51A7105Eu) | 1u);
            _hullRadius = 0f;
            foreach (var b in hull)
            {
                var far = new Vector3(math.max(math.abs(b.min.x - centre.x), math.abs(b.max.x - centre.x)), 0f,
                                      math.max(math.abs(b.min.z - centre.z), math.abs(b.max.z - centre.z)));
                _hullRadius = math.max(_hullRadius, far.magnitude);
            }
            _nextShip = _rng.NextFloat(2f, 6f);   // the first one soon after launch

            var count = math.clamp(cfg?.Debris ?? 0, 0, 1023);
            _debris = new Vector3[count];
            _axes = new Vector3[count];
            _spins = new float[count];
            _sizes = new float[count];
            _drift = new float[count];
            _matrices = new Matrix4x4[count];
            var deep = math.max(1f, cfg?.DebrisRadius ?? 0f) * _length;
            var size = math.max(0.01f, cfg?.DebrisSize ?? 0f) * _length;
            var spin = math.max(0f, cfg?.DebrisSpin ?? 0f);
            for (var i = 0; i < count; i++)
            {
                var dir = _rng.NextFloat3Direction();
                dir.y *= 0.45f;   // a flattened shell: the hull is wide and low
                var r = _hullRadius * 1.15f + _length + _rng.NextFloat() * deep;
                _debris[i] = (Vector3)(math.normalizesafe(dir) * r);
                _axes[i] = (Vector3)_rng.NextFloat3Direction();
                _spins[i] = _rng.NextFloat(0.2f, 1f) * spin * (_rng.NextBool() ? 1f : -1f);
                _sizes[i] = _rng.NextFloat(0.15f, 1f) * size;
                _drift[i] = _rng.NextFloat(0.3f, 1.2f) * (_rng.NextBool() ? 1f : -1f);   // degrees a second round the station
            }
            if (count > 0 && view.HullMaterial != null)
            {
                _debrisMaterial = new Material(view.HullMaterial) { name = "PLACEHOLDER_debris", enableInstancing = true };
                _cubeSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cubeSource.name = "PLACEHOLDER_debris (mesh)";
                Object.Destroy(_cubeSource.GetComponent<Collider>());
                _cube = _cubeSource.GetComponent<MeshFilter>().sharedMesh;
                _cubeSource.SetActive(false);
            }
        }

        OrbitLife C => _c;

        public void Tick(float dt)
        {
            if (C == null || dt <= 0f) return;
            _time += dt;
            Traffic(dt);
            Debris();
            var blink = math.max(0.2f, C.BlinkSeconds);
            var phase = _time / blink % 2f;   // one set flashes, then the other
            _view.Blink(phase % 1f < 0.18f ? (int)phase : -1);
            _view.Radar(_time * C.RadarSpin);
        }

        void Traffic(float dt)
        {
            if (C.Traffic <= 0 || _template == null) return;
            _nextShip -= dt;
            if (_nextShip <= 0f)
            {
                _nextShip = math.max(1f, C.TrafficSeconds) * _rng.NextFloat(0.5f, 1.5f);
                Launch();
            }
            foreach (var s in _ships)
            {
                if (!s.Active) continue;
                s.Age += dt;
                if (s.Age >= s.Life)
                {
                    s.Active = false;
                    s.View.EnginesOff(instant: true);
                    s.Go.SetActive(false);
                    continue;
                }
                var at = s.From + s.Dir * (s.Speed * s.Age);
                // A lazy roll as it goes, so it isn't a cutout sliding by.
                var roll = s.Roll * math.sin(s.Age * 0.6f);
                s.View.Pose(at, Quaternion.LookRotation(s.Dir, Vector3.up) * Quaternion.Euler(0f, 0f, roll));
            }
        }

        /// <summary>One crossing: a straight run past the station, its nearest pass `traffic_distance` beyond the hull.</summary>
        void Launch()
        {
            Ship ship = null;
            foreach (var s in _ships) if (!s.Active) { ship = s; break; }
            if (ship == null)
            {
                if (_ships.Count >= C.Traffic) return;
                var go = Object.Instantiate(_template);
                go.name = $"Traffic {_ships.Count}";
                go.SetActive(true);
                var view = go.GetComponent<ShipView>();
                if (view == null) { Object.Destroy(go); return; }
                var skin = view.SkinCount > 0 ? view.SkinAt(_rng.NextInt(view.SkinCount)) : null;
                view.Show(skin, _length * _rng.NextFloat(0.9f, 1.3f));
                Layers.Set(go, _layer);   // its flames too (built by Show)
                ship = new Ship { Go = go, View = view };
                _ships.Add(ship);
            }
            var angle = _rng.NextFloat(0f, math.PI * 2f);
            var dir = new Vector3(math.cos(angle), _rng.NextFloat(-0.12f, 0.12f), math.sin(angle)).normalized;
            var side = Vector3.Cross(Vector3.up, dir).normalized * (_rng.NextBool() ? 1f : -1f);
            var pass = _centre + side * (_hullRadius + math.max(2f, C.TrafficDistance) * _length * _rng.NextFloat(1f, 1.8f))
                     + Vector3.up * (_rng.NextFloat(-0.4f, 0.6f) * _hullRadius);
            var span = _range * 0.7f;
            ship.From = pass - dir * span;
            ship.Dir = dir;
            ship.Speed = math.max(5f, C.TrafficSpeed) * _rng.NextFloat(0.75f, 1.25f);
            ship.Age = 0f;
            ship.Life = span * 2f / ship.Speed;
            ship.Roll = _rng.NextFloat(-25f, 25f);
            ship.Active = true;
            ship.Go.SetActive(true);
            ship.View.Pose(ship.From, Quaternion.LookRotation(dir, Vector3.up));
            ship.View.SetHeat(0f, 1.2f);
            ship.View.SetThrust(new Vector3(0f, 0.15f, 1f), Vector3.zero, 1.2f);
        }

        void Debris()
        {
            if (_debrisMaterial == null || _debris.Length == 0) return;
            for (var i = 0; i < _debris.Length; i++)
            {
                var orbit = Quaternion.AngleAxis(_time * _drift[i], Vector3.up);
                var tumble = Quaternion.AngleAxis(_time * _spins[i], _axes[i]);
                _matrices[i] = Matrix4x4.TRS(_centre + orbit * _debris[i], tumble, new Vector3(_sizes[i], _sizes[i] * 0.6f, _sizes[i] * 0.8f));
            }
            var reach = _hullRadius * 1.3f + _length * 2f + math.max(1f, C.DebrisRadius) * _length;
            Graphics.RenderMeshInstanced(new RenderParams(_debrisMaterial)
            {
                layer = _layer, worldBounds = new Bounds(_centre, Vector3.one * (reach * 2f)),
                shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false,
            }, _cube, 0, _matrices, _debris.Length);
        }

        public void Dispose()
        {
            foreach (var s in _ships) if (s.Go != null) Object.Destroy(s.Go);
            _ships.Clear();
            if (_cubeSource != null) Object.Destroy(_cubeSource);
            if (_debrisMaterial != null) Object.Destroy(_debrisMaterial);
        }
    }
}
