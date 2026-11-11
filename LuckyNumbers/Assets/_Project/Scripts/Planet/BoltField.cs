using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>Told when one of its bolts hits (after the field has hurt, pushed and flashed): logs, counters.</summary>
    public interface IBoltSink
    {
        void OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled);
    }

    /// <summary>One bolt to fire: where, how fast and far, its look, and what it does on a hit.</summary>
    public struct BoltShot
    {
        public Vector3 From, Direction;
        public float Speed, Range, Length, Width;
        public Color Color;
        /// <summary>The impact flash: diameter (m), seconds, how far off the surface (m).</summary>
        public float ImpactSize, ImpactLife, ImpactLift;
        /// <summary>Push on what it hits (N s).</summary>
        public float Impulse;
        /// <summary>Who shot it, for IDamageable.Hit ("ship", an item id, "default").</summary>
        public string Source;
        /// <summary>Colliders under these are passed through (the shooter, the gun).</summary>
        public Transform Ignore, IgnoreAlso;
        public IBoltSink Sink;
        /// <summary>The owner's own data for this shot (the gun it came from); the field never reads it.</summary>
        public object Tag;
    }

    /// <summary>
    /// Every bolt of one kind of shooter in flight (hand bolts, ship lasers, held guns), as data. A frame: a Burst
    /// job turns each bolt into this frame's ray (<see cref="BoltRayJob"/>), Unity's batched physics casts them all
    /// on worker threads (RaycastCommand), then the main thread answers the hits (IDamageable, a push, a flash, the
    /// sink) and moves the rest. Shapes come from pools (Core/ShapePool), so firing builds and frees nothing.
    ///
    /// Hot data (position, direction, speed, range left) is a NativeList the jobs read; what jobs can't touch (the
    /// shape, the shot's source, sink and filters) is a parallel managed list at the same index. Both swap-remove.
    /// The field lives in the active scene: its root's runner frees the native memory when the scene goes, and ticks
    /// it when nobody else does (`selfTick`: guns lying around share one field).
    /// </summary>
    public sealed class BoltField : IDisposable
    {
        /// <summary>Hits kept per ray: room for the shooter's own colliders before the real target.</summary>
        const int MaxHits = 8;   // as many as the old RaycastNonAlloc buffers held

        struct Bolt
        {
            public float3 Position, Direction;
            public float Speed, Left, Length;
        }

        [BurstCompile]
        struct BoltRayJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Bolt> Bolts;
            public float Dt;
            public QueryParameters Query;
            [WriteOnly] public NativeArray<RaycastCommand> Rays;

            public void Execute(int i)
            {
                var b = Bolts[i];
                Rays[i] = new RaycastCommand(b.Position, b.Direction, Query, math.min(b.Speed * Dt, b.Left));
            }
        }

        sealed class Runner : MonoBehaviour
        {
            public BoltField Field;
            public bool SelfTick;
            void Update() { if (SelfTick) Field?.Tick(Time.deltaTime); }
            void OnDestroy() => Field?.FreeNative();
        }

        readonly ShapePool _capsules;
        readonly Flashes _flashes;
        readonly Runner _runner;
        readonly List<BoltShot> _shots = new();
        readonly List<Shape> _views = new();
        NativeList<Bolt> _bolts;
        NativeArray<RaycastCommand> _rays;
        NativeArray<RaycastHit> _hits;
        readonly QueryParameters _query = new(~0, false, QueryTriggerInteraction.Ignore, false);

        /// <param name="colorA">Shader property the tint goes to (colorB too, if the shader has a second).</param>
        /// <param name="selfTick">Tick every frame by itself (else the owner calls Tick).</param>
        public BoltField(Material material, string name, int colorA, int colorB, int prewarm, bool selfTick = false)
        {
            _capsules = new ShapePool(PrimitiveType.Capsule, material, name, colorA, colorB, prewarm);
            _flashes = new Flashes(new ShapePool(PrimitiveType.Sphere, material, name + " flash", colorA, colorB, prewarm), ownsPool: true);
            _bolts = new NativeList<Bolt>(math.max(8, prewarm), Allocator.Persistent);
            _runner = new GameObject($"[Bolts] {name}").AddComponent<Runner>();
            _runner.Field = this;
            _runner.SelfTick = selfTick;
        }

        /// <summary>False once its scene is gone (a shared field is then made again).</summary>
        public bool Alive => _runner != null && _bolts.IsCreated;

        public int Count => _shots.Count;

        /// <summary>Make enough bolt and flash shapes up front that `count` of each exist in all.</summary>
        public void Reserve(int count)
        {
            _capsules.Reserve(count);
            _flashes.Reserve(count);
        }

        public void Fire(in BoltShot shot)
        {
            var dir = shot.Direction.sqrMagnitude > 1e-6f ? shot.Direction.normalized : Vector3.forward;
            var s = shot;
            s.Direction = dir;
            var view = _capsules.Get(shot.From, Quaternion.FromToRotation(Vector3.up, dir),
                                     new Vector3(shot.Width, shot.Length * 0.5f, shot.Width), shot.Color);
            _bolts.Add(new Bolt { Position = shot.From, Direction = dir, Speed = math.max(0.01f, shot.Speed), Left = shot.Range, Length = shot.Length });
            _shots.Add(s);
            _views.Add(view);
        }

        /// <summary>A flash (muzzle, or anything else that should look like this field's).</summary>
        public void Flash(Vector3 at, float size, float life, Color color, int layer = 0) => _flashes.Add(at, size, life, color, layer);

        public void Tick(float dt)
        {
            if (!_bolts.IsCreated) return;
            var n = _bolts.Length;
            if (n > 0 && dt > 0f)
            {
                Grow(n);
                var rays = _rays.GetSubArray(0, n);
                var hits = _hits.GetSubArray(0, n * MaxHits);
                var job = new BoltRayJob { Bolts = _bolts.AsArray(), Dt = dt, Query = _query, Rays = rays }.Schedule(n, 16);
                RaycastCommand.ScheduleBatch(rays, hits, 8, MaxHits, job).Complete();

                for (var i = n - 1; i >= 0; i--)
                {
                    var b = _bolts[i];
                    var step = math.min(b.Speed * dt, b.Left);
                    if (Nearest(hits, i, _shots[i], out var hit))
                    {
                        Answer(_shots[i], hit, _shots[i].Range - b.Left + hit.distance);
                        Remove(i);
                        continue;
                    }
                    b.Position += b.Direction * step;
                    b.Left -= step;
                    if (b.Left <= 0f) { Remove(i); continue; }
                    _bolts[i] = b;
                    var view = _views[i];
                    if (view.Alive) view.Transform.position = TailOf(b.Position, b.Direction, b.Length);
                }
            }
            _flashes.Tick(dt);
        }

        /// <summary>The bolt's capsule sits behind its head, so it never pokes through what it is about to hit.</summary>
        static Vector3 TailOf(float3 head, float3 dir, float length) => head - dir * (length * 0.5f);

        /// <summary>The closest hit of ray `i` that isn't the shooter's own.</summary>
        static bool Nearest(NativeArray<RaycastHit> hits, int i, in BoltShot shot, out RaycastHit best)
        {
            best = default;
            var found = false;
            var closest = float.MaxValue;
            for (var k = i * MaxHits; k < (i + 1) * MaxHits; k++)
            {
                var h = hits[k];
                var c = h.collider;
                if (c == null) break;   // results are packed: the rest are empty
                var t = c.transform;
                if (shot.Ignore != null && t.IsChildOf(shot.Ignore)) continue;
                if (shot.IgnoreAlso != null && t.IsChildOf(shot.IgnoreAlso)) continue;
                if (h.distance < closest) { closest = h.distance; best = h; found = true; }
            }
            return found;
        }

        void Answer(in BoltShot shot, in RaycastHit hit, float travelled)
        {
            var target = hit.collider.GetComponentInParent<IDamageable>();
            target?.Hit(shot.Source, hit.point, shot.Direction);
            if (shot.Impulse > 0f && hit.rigidbody != null && !hit.rigidbody.isKinematic)
                hit.rigidbody.AddForceAtPosition(shot.Direction * shot.Impulse, hit.point, ForceMode.Impulse);
            if (shot.ImpactSize > 0f) _flashes.Add(hit.point + hit.normal * shot.ImpactLift, shot.ImpactSize, shot.ImpactLife, shot.Color);
            var sink = shot.Sink;
            if (sink is Object o && o == null) return;   // its gun was destroyed while the bolt flew
            sink?.OnBoltHit(shot, hit, target, travelled);
        }

        /// <summary>Back to the pool; swap-remove (the last bolt, already ticked this frame, takes its slot).</summary>
        void Remove(int i)
        {
            _capsules.Release(_views[i]);
            var last = _shots.Count - 1;
            _bolts.RemoveAtSwapBack(i);
            _shots[i] = _shots[last];
            _shots.RemoveAt(last);
            _views[i] = _views[last];
            _views.RemoveAt(last);
        }

        void Grow(int n)
        {
            if (_rays.IsCreated && _rays.Length >= n) return;
            var size = math.max(16, math.ceilpow2(n));
            if (_rays.IsCreated) _rays.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            _rays = new NativeArray<RaycastCommand>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _hits = new NativeArray<RaycastHit>(size * MaxHits, Allocator.Persistent);
        }

        /// <summary>Drop every bolt in flight (their shapes back to the pool), keeping the field.</summary>
        public void Clear()
        {
            foreach (var v in _views) _capsules.Release(v);
            _views.Clear();
            _shots.Clear();
            if (_bolts.IsCreated) _bolts.Clear();
            _flashes.Clear();
        }

        void FreeNative()
        {
            if (_bolts.IsCreated) _bolts.Dispose();
            if (_rays.IsCreated) _rays.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            _shots.Clear();
            _views.Clear();
        }

        public void Dispose()
        {
            Clear();
            FreeNative();
            _flashes.Dispose();
            _capsules.Dispose();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
