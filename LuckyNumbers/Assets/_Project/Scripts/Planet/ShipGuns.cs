using System;
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
    public sealed class ShipGuns : IDisposable, IBoltSink
    {
        readonly ShipView _ship;
        readonly BoltField _field;   // instanced, unlit, coloured per gun
        ShipGun[] _barrels = Array.Empty<ShipGun>();
        float[] _ready = Array.Empty<float>();
        float _firepower = 1f;
        int _next;

        /// <summary>Shots fired (the page's crosshair flashes on a change).</summary>
        public int Shots { get; private set; }

        public ShipGuns(ShipView ship)
        {
            _ship = ship;
            _field = new BoltField("Laser", prewarm: 0);   // Arm reserves per barrel
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
                else if (_barrels[i].Bolt == null) { Debug.LogWarning($"[Guns] gun '{stats.Guns[i]}' has no bolt (content/ships.txtpb)"); _barrels[i] = null; }
            }
            // Enough shapes for every barrel's bolts in flight (its bolt's prewarm, 4 if unset).
            var prewarm = 0;
            foreach (var g in _barrels) if (g != null) prewarm += g.Bolt.PrewarmOr(4);
            _field.Reserve(prewarm);
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
                var p = gun.Bolt;
                var (impact, impactLife) = p.Impact.Of(p.Width, 10f, 0.25f);
                _field.Fire(new BoltShot
                {
                    From = from, Direction = dir, Speed = p.Speed + Mathf.Max(0f, carry), Range = p.Range,
                    Length = p.Length, Width = p.Width, Color = Tint(p),
                    ImpactSize = impact, ImpactLife = impactLife, ImpactLift = 0.05f, Impulse = gun.Impulse,
                    Source = "ship", Ignore = _ship.Pivot, Sink = this,   // never your own hull
                });
                var (size, life) = p.Muzzle.Of(p.Width, 4f, 0.07f);
                _field.Flash(from, size, life, Tint(p));
                Shots++;
                return true;
            }
            return false;
        }

        public void Tick(float dt) => _field.Tick(dt);

        void IBoltSink.OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled) =>
            Debug.Log($"[Guns] bolt hit {hit.collider.name} after {travelled:0} m");

        static Color Tint(Projectile p) => p.Tint(new Color(2.6f, 0.35f, 0.3f));

        public void Dispose() => _field.Dispose();
    }
}
