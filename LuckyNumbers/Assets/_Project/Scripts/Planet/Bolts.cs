using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Energy bolts fired from your hands (CombatPresenter): fly from a point toward where you aim, skip your own
    /// body, and on a hit tell an IDamageable (with the shot's source) and flash. Unlit, tinted per shot.
    /// </summary>
    public sealed class Bolts : IDisposable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        struct Bolt
        {
            public Shape View;
            public Vector3 Position, Direction;
            public float Speed, Left, Length, Width;
            public Color Color;
            public string Source;
        }

        readonly Transform _ignore;
        readonly List<Bolt> _bolts = new();
        readonly ShapePool _capsules;
        readonly Flashes _flashes;
        readonly RaycastHit[] _hits = new RaycastHit[8];
        int _logged;

        public Bolts(Material material, Transform ignore)
        {
            _ignore = ignore;
            // Pooled, warmed up front: the first shot of a fight shouldn't be the one that builds GameObjects.
            _capsules = new ShapePool(PrimitiveType.Capsule, material, "Bolt", BaseColor, ColorId, prewarm: 8);
            _flashes = new Flashes(new ShapePool(PrimitiveType.Sphere, material, "Bolt flash", BaseColor, ColorId, prewarm: 8), ownsPool: true);
        }

        public void Fire(Vector3 from, Vector3 target, float speed, float range, float length, float width, Color color, string source)
        {
            var dir = target - from;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            var view = _capsules.Get(from, Quaternion.FromToRotation(Vector3.up, dir), new Vector3(width, length * 0.5f, width), color);
            _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Speed = Mathf.Max(1f, speed), Left = range,
                                  Length = length, Width = width, Color = color, Source = source });
            _flashes.Add(from, width * 3f, 0.06f, color);
        }

        public void Tick(float dt)
        {
            for (var i = _bolts.Count - 1; i >= 0; i--)
            {
                var b = _bolts[i];
                var step = Mathf.Min(b.Speed * dt, b.Left);
                if (Cast(b.Position, b.Direction, step, out var hit))
                {
                    var target = hit.collider.GetComponentInParent<IDamageable>();
                    if (_logged++ < 6) Debug.Log($"[Bolts] {b.Source} hit {hit.collider.name}{(target != null ? " (damageable)" : "")}");
                    target?.Hit(b.Source, hit.point, b.Direction);
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        hit.rigidbody.AddForceAtPosition(b.Direction * 3f, hit.point, ForceMode.Impulse);
                    _flashes.Add(hit.point + hit.normal * 0.03f, b.Width * 6f, 0.2f, b.Color);
                    Remove(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                b.View.Transform.position = b.Position - b.Direction * (b.Length * 0.5f);
                if (b.Left <= 0f) { Remove(i); continue; }
                _bolts[i] = b;
            }
            _flashes.Tick(dt);
        }

        /// <summary>Back to the pool; swap-remove (the last bolt, already ticked this frame, takes its slot).</summary>
        void Remove(int i)
        {
            _capsules.Release(_bolts[i].View);
            _bolts[i] = _bolts[^1];
            _bolts.RemoveAt(_bolts.Count - 1);
        }

        bool Cast(Vector3 from, Vector3 dir, float distance, out RaycastHit best)
        {
            best = default;
            best.distance = float.MaxValue;
            var n = Physics.RaycastNonAlloc(from, dir, _hits, distance, ~0, QueryTriggerInteraction.Ignore);
            var found = false;
            for (var i = 0; i < n; i++)
            {
                var h = _hits[i];
                if (_ignore != null && h.collider.transform.IsChildOf(_ignore)) continue;
                if (h.distance < best.distance) { best = h; found = true; }
            }
            return found;
        }

        public void Dispose()
        {
            _bolts.Clear();
            _flashes.Dispose();
            _capsules.Dispose();
        }
    }
}
