using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Monsters' thrown flasks (CombatPresenter, MonsterType.ranged), in flight and where they burst. A
    /// <see cref="BoltField"/> with gravity and the Glow look flies and draws them: each a glowing core with a trail
    /// on its arc, passing through its thrower (BoltShot.Ignore). On impact (the ground, you, anything solid) the field
    /// puffs (its impact flash, a swelling toxic cloud), this adds a bright pop and leaves a stain on the ground a few
    /// seconds (the blob shadows' multiply shader, tinted), and tells `Burst` where, so the presenter rolls the splash.
    /// The stains are one instanced draw (a flat quad each, on the surface it hit); this is per scene, as the field.
    /// </summary>
    public sealed class Flasks : IDisposable, IBoltSink
    {
        const int Batch = 64;
        /// <summary>A stain: how long it lasts (s), how wide over the splash, and its strength at first.</summary>
        const float StainLife = 4f, StainSpread = 1.5f, StainStrength = 0.85f;

        static readonly int BendId = Shader.PropertyToID("_BlobBend");
        static readonly int ShadowColor = Shader.PropertyToID("_ShadowColor");
        static Material _stainMaterial;
        static Mesh _stainMesh;

        /// <summary>A flask burst at `point` on `what`; `direct`: it struck what `Direct` names (you). `tag`: the throw's.</summary>
        public Action<Vector3, Collider, bool, object> Burst;
        /// <summary>A direct hit is on something under this (your body).</summary>
        public Transform Direct;

        struct Stain
        {
            public Matrix4x4 At;
            public float Age;
        }

        readonly BoltField _field;
        readonly List<Stain> _stains = new();
        readonly Matrix4x4[] _matrices = new Matrix4x4[Batch];
        readonly Vector4[] _bend = new Vector4[Batch];
        readonly MaterialPropertyBlock _block = new();
        readonly Runner _runner;

        sealed class Runner : MonoBehaviour
        {
            public Flasks Flasks;
            void LateUpdate() => Flasks?.DrawStains();
        }

        public Flasks(int prewarm)
        {
            _field = new BoltField("Flask", prewarm, false, BoltLook.Glow);
            if (_stainMaterial == null)
            {
                var blob = Resources.Load<Material>("MonsterBlob");
                if (blob != null)
                {
                    _stainMaterial = new Material(blob) { name = "FlaskStain", enableInstancing = true };
                    _stainMaterial.SetColor(ShadowColor, new Color(0.35f, 0.55f, 0.18f, 1f));   // a sickly green-black scorch
                }
            }
            _stainMesh ??= Quad();
            _runner = new GameObject("[Flasks]").AddComponent<Runner>();
            _runner.Flasks = this;
        }

        public int Count => _field.Count;

        /// <summary>Lob a flask from `from` at `velocity` under `gravity`; `thrower` (its body) is passed through.</summary>
        public void Throw(Vector3 from, Vector3 velocity, float gravity, Color color, float size, float splash, Transform thrower, object tag)
        {
            var speed = velocity.magnitude;
            _field.Fire(new BoltShot
            {
                From = from, Direction = velocity, Speed = speed, Range = 120f, Gravity = gravity,
                Length = Mathf.Max(0.4f, speed * 0.09f), Width = size, Color = color,
                ImpactSize = splash * 2.2f, ImpactLife = 0.7f, ImpactLift = 0.25f, Impulse = 1.5f,
                Source = "flask", Ignore = thrower, Sink = this, Tag = tag,
            });
        }

        /// <summary>A wind-up's charge in the thrower's hand, this frame (grows to `size` as `t` goes 0..1).</summary>
        public void Charge(Vector3 at, float size, float t, Color color)
        {
            var flicker = 0.85f + 0.15f * Mathf.Sin(Time.time * 37f + at.x);
            _field.Spark(at, size * Mathf.Lerp(0.3f, 1f, t) * flicker, color * Mathf.Lerp(0.4f, 1f, t));
        }

        public void Tick(float dt)
        {
            _field.Tick(dt);
            for (var i = _stains.Count - 1; i >= 0; i--)
            {
                var s = _stains[i];
                s.Age += dt;
                if (s.Age >= StainLife)
                {
                    _stains[i] = _stains[^1];
                    _stains.RemoveAt(_stains.Count - 1);
                    continue;
                }
                _stains[i] = s;
            }
        }

        void IBoltSink.OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled)
        {
            var direct = Direct != null && hit.collider.transform.IsChildOf(Direct);
            _field.Flash(hit.point + hit.normal * 0.15f, shot.ImpactSize * 0.45f, 0.18f, shot.Color * 1.8f);   // the pop
            if (!direct && _stainMaterial != null)
            {
                var up = hit.normal.sqrMagnitude > 0.5f ? hit.normal : Vector3.up;
                var size = shot.ImpactSize * 0.5f * StainSpread;
                var turn = Quaternion.AngleAxis(hit.point.x * 37f + hit.point.z * 11f, up);   // each lies its own way
                _stains.Add(new Stain { At = Matrix4x4.TRS(hit.point + up * 0.03f, turn * Quaternion.FromToRotation(Vector3.up, up), new Vector3(size, 1f, size)) });
            }
            Burst?.Invoke(hit.point, hit.collider, direct, shot.Tag);
        }

        void DrawStains()
        {
            if (_stains.Count == 0 || _stainMaterial == null) return;
            var count = 0;
            var lo = Vector3.positiveInfinity;
            var hi = Vector3.negativeInfinity;
            var reach = 0f;
            foreach (var s in _stains)
            {
                var t = s.Age / StainLife;
                _matrices[count] = s.At;
                _bend[count] = new Vector4(0f, 0f, 0f, StainStrength * (1f - t * t));
                Vector3 at = s.At.GetColumn(3);
                lo = Vector3.Min(lo, at);
                hi = Vector3.Max(hi, at);
                reach = math.max(reach, s.At.GetColumn(0).magnitude);
                if (++count == Batch) Flush();
            }
            Flush();

            void Flush()
            {
                if (count == 0) return;
                _block.Clear();
                _block.SetVectorArray(BendId, _bend);
                Graphics.RenderMeshInstanced(new RenderParams(_stainMaterial)
                {
                    worldBounds = new Bounds((lo + hi) * 0.5f, hi - lo + Vector3.one * (2f * reach + 1f)),
                    shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, matProps = _block,
                }, _stainMesh, 0, _matrices, count);
                count = 0;
                lo = Vector3.positiveInfinity;
                hi = Vector3.negativeInfinity;
                reach = 0f;
            }
        }

        /// <summary>A flat quad in the XZ plane, -0.5..0.5 (what the blob shader expects, without its bend grid).</summary>
        static Mesh Quad()
        {
            var m = new Mesh { name = "FlaskStain" };
            m.SetVertices(new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) });
            m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            m.RecalculateBounds();
            return m;
        }

        public void Dispose()
        {
            _field.Dispose();
            _stains.Clear();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
