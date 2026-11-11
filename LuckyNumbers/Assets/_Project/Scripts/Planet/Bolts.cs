using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

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

        sealed class Bolt
        {
            public Transform View;
            public Vector3 Position, Direction;
            public float Speed, Left, Length, Width;
            public Color Color;
            public string Source;
        }

        readonly Material _material;
        readonly Transform _ignore;
        readonly List<Bolt> _bolts = new();
        readonly List<(Transform view, float age, float life, float size)> _flashes = new();
        readonly RaycastHit[] _hits = new RaycastHit[8];
        readonly MaterialPropertyBlock _block = new();
        int _logged;

        public Bolts(Material material, Transform ignore)
        {
            _material = material;
            _ignore = ignore;
        }

        public void Fire(Vector3 from, Vector3 target, float speed, float range, float length, float width, Color color, string source)
        {
            var dir = target - from;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
            var view = Shape(PrimitiveType.Capsule, from, new Vector3(width, length * 0.5f, width), color);
            view.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Speed = Mathf.Max(1f, speed), Left = range,
                                  Length = length, Width = width, Color = color, Source = source });
            Flash(from, width * 3f, 0.06f, color);
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
                    Flash(hit.point + hit.normal * 0.03f, b.Width * 6f, 0.2f, b.Color);
                    Object.Destroy(b.View.gameObject);
                    _bolts.RemoveAt(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                b.View.position = b.Position - b.Direction * (b.Length * 0.5f);
                if (b.Left <= 0f) { Object.Destroy(b.View.gameObject); _bolts.RemoveAt(i); }
            }
            for (var i = _flashes.Count - 1; i >= 0; i--)
            {
                var (view, age, life, size) = _flashes[i];
                age += dt;
                if (age >= life) { Object.Destroy(view.gameObject); _flashes.RemoveAt(i); continue; }
                view.localScale = Vector3.one * (size * Mathf.Lerp(0.4f, 1f, age / life));
                _flashes[i] = (view, age, life, size);
            }
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

        void Flash(Vector3 at, float size, float life, Color color)
        {
            var view = Shape(PrimitiveType.Sphere, at, Vector3.one * size * 0.4f, color);
            _flashes.Add((view, 0f, life, size));
        }

        Transform Shape(PrimitiveType type, Vector3 at, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Bolt";
            Object.Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (_material != null) r.sharedMaterial = _material;
            _block.SetColor(BaseColor, color);
            _block.SetColor(ColorId, color);
            r.SetPropertyBlock(_block);
            go.transform.SetPositionAndRotation(at, Quaternion.identity);
            go.transform.localScale = scale;
            return go.transform;
        }

        public void Dispose()
        {
            foreach (var b in _bolts) if (b.View != null) Object.Destroy(b.View.gameObject);
            foreach (var f in _flashes) if (f.view != null) Object.Destroy(f.view.gameObject);
            _bolts.Clear();
            _flashes.Clear();
        }
    }
}
