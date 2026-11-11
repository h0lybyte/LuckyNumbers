using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Your fighter's guns (content/ships.txtpb guns; one per barrel, ShipStats.guns): click while flying and the
    /// barrels fire in turn, laser bolts from each muzzle (ShipView.Muzzle) straight down the nose, carried along at
    /// the ship's speed, pushing what they hit. The skin's firepower shortens the cooldown. LandingPresenter drives it.
    /// </summary>
    public sealed class ShipGuns : IDisposable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int Color = Shader.PropertyToID("_Color");

        sealed class Bolt
        {
            public Transform View;
            public Vector3 Position, Direction;
            public float Speed, Left;
            public ShipGun Gun;
        }

        readonly ShipView _ship;
        readonly Material _material;   // unlit (the wreck beacon's), coloured per gun
        readonly List<Bolt> _bolts = new();
        readonly List<(Transform view, float age, float life, float size)> _flashes = new();
        readonly RaycastHit[] _hits = new RaycastHit[8];
        MaterialPropertyBlock _block;
        ShipGun[] _barrels = Array.Empty<ShipGun>();
        float[] _ready = Array.Empty<float>();
        float _firepower = 1f;
        int _next;

        /// <summary>Shots fired (the page's crosshair flashes on a change).</summary>
        public int Shots { get; private set; }

        public ShipGuns(ShipView ship, Material material)
        {
            _ship = ship;
            _material = material;
        }

        /// <summary>Mount the skin's guns, one per barrel (missing ids leave that barrel empty).</summary>
        public void Arm(ShipStats stats)
        {
            var count = _ship.MuzzleCount;
            _barrels = new ShipGun[count];
            _ready = new float[count];
            for (var i = 0; i < count && i < stats.Guns.Count; i++)
            {
                _barrels[i] = PlayerShipStats.Gun(stats.Guns[i]);
                if (_barrels[i] == null) Debug.LogWarning($"[Guns] no gun '{stats.Guns[i]}' (content/ships.txtpb guns)");
            }
            _firepower = Mathf.Max(0.1f, stats.Firepower);
            Debug.Log($"[Guns] {stats.Name}: {string.Join(", ", stats.Guns)} (firepower {_firepower:0.00})");
        }

        /// <summary>Fire the next ready barrel along the ship's nose, carried at `carry` m/s (the ship's forward speed).</summary>
        public bool TryFire(float carry)
        {
            for (var tries = 0; tries < _barrels.Length; tries++)
            {
                var i = _next;
                _next = (_next + 1) % Math.Max(1, _barrels.Length);
                var gun = _barrels[i];
                if (gun == null || Time.time < _ready[i]) continue;
                // Each barrel has its own cooldown; the pair alternate, so together they fire twice as often.
                _ready[i] = Time.time + Mathf.Max(0.02f, gun.Cooldown) / _firepower;
                // Don't let the other barrel fire in the same instant: stagger it half a cooldown.
                var other = (i + 1) % _barrels.Length;
                if (other != i) _ready[other] = Mathf.Max(_ready[other], Time.time + Mathf.Max(0.02f, gun.Cooldown) / _firepower * 0.5f);
                var from = _ship.Muzzle(i);
                var dir = _ship.Pivot.forward;
                var view = Shape(PrimitiveType.Capsule, from, new Vector3(gun.Width, gun.Length * 0.5f, gun.Width), gun);
                view.rotation = Quaternion.FromToRotation(Vector3.up, dir);
                _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Speed = gun.Speed + Mathf.Max(0f, carry), Left = gun.Range, Gun = gun });
                Flash(from, gun.Width * 4f, 0.07f, gun);
                Shots++;
                return true;
            }
            return false;
        }

        public void Tick(float dt)
        {
            for (var i = _bolts.Count - 1; i >= 0; i--)
            {
                var b = _bolts[i];
                var step = Mathf.Min(b.Speed * dt, b.Left);
                if (Cast(b.Position, b.Direction, step, out var hit))
                {
                    hit.collider.GetComponentInParent<IDamageable>()?.Hit("ship", hit.point, b.Direction);
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        hit.rigidbody.AddForceAtPosition(b.Direction * b.Gun.Impulse, hit.point, ForceMode.Impulse);
                    Flash(hit.point + hit.normal * 0.05f, b.Gun.Width * 10f, 0.25f, b.Gun);
                    Debug.Log($"[Guns] bolt hit {hit.collider.name} after {b.Gun.Range - b.Left + hit.distance:0} m");
                    Object.Destroy(b.View.gameObject);
                    _bolts.RemoveAt(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                b.View.position = b.Position - b.Direction * (b.Gun.Length * 0.5f);   // the tail trails the head
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
                if (h.collider.transform.IsChildOf(_ship.Pivot)) continue;   // never your own hull
                if (h.distance < best.distance) { best = h; found = true; }
            }
            return found;
        }

        void Flash(Vector3 at, float size, float life, ShipGun gun)
        {
            var view = Shape(PrimitiveType.Sphere, at, Vector3.one * size * 0.4f, gun);
            _flashes.Add((view, 0f, life, size));
        }

        Transform Shape(PrimitiveType type, Vector3 at, Vector3 scale, ShipGun gun)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Laser";
            Object.Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (_material != null) r.sharedMaterial = _material;
            var g = gun.Color;
            var c = g.R + g.G + g.B > 0f ? new UnityEngine.Color(g.R, g.G, g.B, 1f) : new UnityEngine.Color(2.6f, 0.35f, 0.3f);
            _block ??= new MaterialPropertyBlock();
            _block.SetColor(BaseColor, c);
            _block.SetColor(Color, c);
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
