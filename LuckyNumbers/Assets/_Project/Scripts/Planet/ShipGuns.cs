using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using UnityEngine;

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
        static readonly int ColorId = Shader.PropertyToID("_Color");

        struct Bolt
        {
            public Shape View;
            public Vector3 Position, Direction;
            public float Speed, Left;
            public ShipGun Gun;
        }

        readonly ShipView _ship;
        readonly List<Bolt> _bolts = new();
        readonly ShapePool _capsules;   // unlit (the wreck beacon's material), coloured per gun
        readonly Flashes _flashes;
        readonly RaycastHit[] _hits = new RaycastHit[8];
        ShipGun[] _barrels = Array.Empty<ShipGun>();
        float[] _ready = Array.Empty<float>();
        float _firepower = 1f;
        int _next;

        /// <summary>Shots fired (the page's crosshair flashes on a change).</summary>
        public int Shots { get; private set; }

        public ShipGuns(ShipView ship, Material material)
        {
            _ship = ship;
            _capsules = new ShapePool(PrimitiveType.Capsule, material, "Laser", BaseColor, ColorId, prewarm: 8);
            _flashes = new Flashes(new ShapePool(PrimitiveType.Sphere, material, "Laser flash", BaseColor, ColorId, prewarm: 8), ownsPool: true);
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
                var view = _capsules.Get(from, Quaternion.FromToRotation(Vector3.up, dir), new Vector3(gun.Width, gun.Length * 0.5f, gun.Width), Tint(gun));
                _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Speed = gun.Speed + Mathf.Max(0f, carry), Left = gun.Range, Gun = gun });
                _flashes.Add(from, gun.Width * 4f, 0.07f, Tint(gun));
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
                    _flashes.Add(hit.point + hit.normal * 0.05f, b.Gun.Width * 10f, 0.25f, Tint(b.Gun));
                    Debug.Log($"[Guns] bolt hit {hit.collider.name} after {b.Gun.Range - b.Left + hit.distance:0} m");
                    Remove(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                b.View.Transform.position = b.Position - b.Direction * (b.Gun.Length * 0.5f);   // the tail trails the head
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
                if (h.collider.transform.IsChildOf(_ship.Pivot)) continue;   // never your own hull
                if (h.distance < best.distance) { best = h; found = true; }
            }
            return found;
        }

        static Color Tint(ShipGun gun)
        {
            var g = gun.Color;
            return g.R + g.G + g.B > 0f ? new Color(g.R, g.G, g.B, 1f) : new Color(2.6f, 0.35f, 0.3f);
        }

        public void Dispose()
        {
            _bolts.Clear();
            _flashes.Dispose();
            _capsules.Dispose();
        }
    }
}
