using System;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Energy bolts fired from your hands (CombatPresenter): fly from a point toward where you aim, skip your own
    /// body, and on a hit tell an IDamageable (with the shot's source) and flash. Unlit, tinted per shot. A
    /// <see cref="BoltField"/> flies them.
    /// </summary>
    public sealed class Bolts : IDisposable, IBoltSink
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        readonly Transform _ignore;
        readonly BoltField _field;
        int _logged;

        public Bolts(Material material, Transform ignore)
        {
            _ignore = ignore;
            // Pooled, warmed up front: the first shot of a fight shouldn't be the one that builds GameObjects.
            // As many as the busiest hand gun asks for (content/combat.txtpb weapons' bolt prewarm), 8 if none says.
            var prewarm = 0;
            var weapons = GameContent.Combat?.Weapons;
            if (weapons != null) foreach (var w in weapons) if (w.Bolt != null) prewarm = Mathf.Max(prewarm, w.Bolt.PrewarmOr(0));
            if (prewarm <= 0) prewarm = 8;
            _field = new BoltField(material, "Bolt", BaseColor, ColorId, prewarm);
        }

        /// <summary>Shoot `p` (a hand gun's bolt) from `from` toward `target`; hits tell IDamageables `source` shot them.</summary>
        public void Fire(Vector3 from, Vector3 target, Projectile p, string source)
        {
            var width = Mathf.Max(0.02f, p.Width);
            var color = p.Tint(new Color(0.4f, 1.6f, 2.6f));
            var (muzzle, muzzleLife) = p.Muzzle.Of(width, 3f, 0.06f);
            var (impact, impactLife) = p.Impact.Of(width, 6f, 0.2f);
            _field.Fire(new BoltShot
            {
                From = from, Direction = target - from, Speed = Mathf.Max(1f, p.Speed), Range = Mathf.Max(5f, p.Range),
                Length = Mathf.Max(0.2f, p.Length), Width = width, Color = color,
                ImpactSize = impact, ImpactLife = impactLife, ImpactLift = 0.03f, Impulse = 3f,
                Source = source, Ignore = _ignore, Sink = this,
            });
            _field.Flash(from, muzzle, muzzleLife, color);
        }

        public void Tick(float dt) => _field.Tick(dt);

        void IBoltSink.OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled)
        {
            if (_logged++ < 6) Debug.Log($"[Bolts] {shot.Source} hit {hit.collider.name}{(target != null ? " (damageable)" : "")}");
        }

        public void Dispose() => _field.Dispose();
    }
}
