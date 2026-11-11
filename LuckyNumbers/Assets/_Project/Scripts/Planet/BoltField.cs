using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
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
    /// Every bolt of one kind of shooter in flight (hand bolts, ship lasers, held guns), as data, and their flashes.
    /// A frame: a Burst job turns each bolt into this frame's ray (<see cref="BoltRayJob"/>), Unity's batched physics
    /// casts them all on worker threads (RaycastCommand), then the main thread answers the hits (IDamageable, a push,
    /// a flash, the sink) and moves the rest.
    ///
    /// Nothing is a GameObject: a second Burst job (<see cref="BoltDrawJob"/>) writes each bolt's matrix and the
    /// runner draws them all with one GPU-instanced call (LuckyNumbers/Bolt, colour per instance), flashes likewise
    /// per layer. The runner draws in LateUpdate every frame, whoever ticks the field and whenever.
    ///
    /// Hot data (position, direction, speed, range left, look) is a NativeList the jobs read; what jobs can't touch
    /// (the shot's source, sink and filters) is a parallel managed list at the same index. Both swap-remove. The
    /// field lives in the active scene: its runner frees the native memory when the scene goes, and ticks it when
    /// nobody else does (`selfTick`: guns lying around share one field).
    /// </summary>
    public sealed class BoltField : IDisposable
    {
        /// <summary>Hits kept per ray: room for the shooter's own colliders before the real target.</summary>
        const int MaxHits = 8;   // as many as the old RaycastNonAlloc buffers held
        /// <summary>Instances per draw call: matrix + colour each stays inside WebGL2's 16 KB uniform buffer.</summary>
        const int Batch = 64;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static Material _material;
        static Mesh _capsule, _sphere;

        struct Bolt
        {
            public float3 Position, Direction;
            public float Speed, Left, Range, Length;
            public quaternion Rotation;   // capsule +Y down the direction (fixed for the bolt's life)
            public float3 Scale;
            public float4 Color;
        }

        struct Glow
        {
            public Vector3 Position;
            public float Age, Life, Size;
            public Vector4 Color;
            public int Layer;
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

        /// <summary>
        /// Each bolt's capsule, along it and behind its head (so it never pokes through what it's about to hit), but
        /// never behind the muzzle: at first it sticks out ahead, as a fresh bolt always did.
        /// </summary>
        [BurstCompile]
        struct BoltDrawJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Bolt> Bolts;
            [WriteOnly] public NativeArray<Matrix4x4> Matrices;

            public void Execute(int i)
            {
                var b = Bolts[i];
                var back = math.min(b.Length * 0.5f, b.Range - b.Left);
                Matrices[i] = float4x4.TRS(b.Position - b.Direction * back, b.Rotation, b.Scale);
            }
        }

        sealed class Runner : MonoBehaviour
        {
            public BoltField Field;
            public bool SelfTick;
            void Update() { if (SelfTick) Field?.Tick(Time.deltaTime); }
            void LateUpdate() => Field?.Draw();
            void OnDestroy() => Field?.FreeNative();
        }

        readonly Runner _runner;
        readonly List<BoltShot> _shots = new();
        readonly List<Glow> _flashes = new();
        NativeList<Bolt> _bolts;
        NativeArray<RaycastCommand> _rays;
        NativeArray<RaycastHit> _hits;
        NativeArray<Matrix4x4> _matrices;
        readonly QueryParameters _query = new(~0, false, QueryTriggerInteraction.Ignore, false);
        // Draw scratch: a batch's colours, flash matrices, and one property block per batch in a frame (each draw
        // keeps the colours it was given).
        readonly Vector4[] _colors = new Vector4[Batch];
        readonly Matrix4x4[] _flashMatrices = new Matrix4x4[Batch];
        readonly List<MaterialPropertyBlock> _blocks = new();
        readonly List<int> _layers = new();

        /// <param name="selfTick">Tick every frame by itself (else the owner calls Tick).</param>
        public BoltField(string name, int prewarm, bool selfTick = false)
        {
            _material ??= Resources.Load<Material>("Bolt");
            if (_material == null) Debug.LogError("[Bolts] Resources/Bolt material missing: bolts won't show");
            else _material.enableInstancing = true;
            _capsule ??= PrimitiveMesh(PrimitiveType.Capsule);
            _sphere ??= PrimitiveMesh(PrimitiveType.Sphere);
            _bolts = new NativeList<Bolt>(math.max(8, prewarm), Allocator.Persistent);
            _runner = new GameObject($"[Bolts] {name}").AddComponent<Runner>();
            _runner.Field = this;
            _runner.SelfTick = selfTick;
            Reserve(prewarm);
        }

        /// <summary>A primitive's mesh, kept (the GameObject that brought it goes at once).</summary>
        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return mesh;
        }

        /// <summary>False once its scene is gone (a shared field is then made again).</summary>
        public bool Alive => _runner != null && _bolts.IsCreated;

        public int Count => _shots.Count;

        /// <summary>Room for `count` bolts and flashes up front, so a fight's first shots allocate nothing.</summary>
        public void Reserve(int count)
        {
            if (!_bolts.IsCreated || count <= 0) return;
            if (_bolts.Capacity < count) _bolts.Capacity = count;
            if (_shots.Capacity < count) _shots.Capacity = count;
            if (_flashes.Capacity < count * 2) _flashes.Capacity = count * 2;
            Grow(count);
        }

        public void Fire(in BoltShot shot)
        {
            if (!_bolts.IsCreated) return;
            var dir = shot.Direction.sqrMagnitude > 1e-6f ? shot.Direction.normalized : Vector3.forward;
            var s = shot;
            s.Direction = dir;
            _bolts.Add(new Bolt
            {
                Position = shot.From, Direction = dir,
                Speed = math.max(0.01f, shot.Speed), Left = shot.Range, Range = shot.Range, Length = shot.Length,
                Rotation = Quaternion.FromToRotation(Vector3.up, dir),
                Scale = new float3(shot.Width, shot.Length * 0.5f, shot.Width),
                Color = (Vector4)shot.Color,
            });
            _shots.Add(s);
        }

        /// <summary>A flash (muzzle, or anything else that should look like this field's): grows from 40% to `size`.</summary>
        public void Flash(Vector3 at, float size, float life, Color color, int layer = 0) =>
            _flashes.Add(new Glow { Position = at, Size = size, Life = math.max(0.01f, life), Color = color, Layer = layer });

        public void Tick(float dt)
        {
            if (!_bolts.IsCreated || dt <= 0f) return;
            var n = _bolts.Length;
            if (n > 0)
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
                }
            }
            for (var i = _flashes.Count - 1; i >= 0; i--)
            {
                var f = _flashes[i];
                f.Age += dt;
                if (f.Age >= f.Life)
                {
                    _flashes[i] = _flashes[^1];
                    _flashes.RemoveAt(_flashes.Count - 1);
                    continue;
                }
                _flashes[i] = f;
            }
        }

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
            if (shot.ImpactSize > 0f) Flash(hit.point + hit.normal * shot.ImpactLift, shot.ImpactSize, shot.ImpactLife, shot.Color);
            var sink = shot.Sink;
            if (sink is Object o && o == null) return;   // its gun was destroyed while the bolt flew
            sink?.OnBoltHit(shot, hit, target, travelled);
        }

        /// <summary>Swap-remove (the last bolt, already ticked this frame, takes its slot).</summary>
        void Remove(int i)
        {
            var last = _shots.Count - 1;
            _bolts.RemoveAtSwapBack(i);
            _shots[i] = _shots[last];
            _shots.RemoveAt(last);
        }

        /// <summary>Every bolt (one instanced draw per batch) and every flash (per layer), this frame.</summary>
        void Draw()
        {
            if (_material == null || !_bolts.IsCreated) return;
            var block = 0;
            var n = _bolts.Length;
            if (n > 0)
            {
                Grow(n);
                var matrices = _matrices.GetSubArray(0, n);
                new BoltDrawJob { Bolts = _bolts.AsArray(), Matrices = matrices }.Schedule(n, 32).Complete();
                for (var start = 0; start < n; start += Batch)
                {
                    var count = math.min(Batch, n - start);
                    var lo = (Vector3)_bolts[start].Position;
                    var hi = lo;
                    var reach = 0f;
                    for (var k = 0; k < count; k++)
                    {
                        var b = _bolts[start + k];
                        _colors[k] = b.Color;
                        lo = Vector3.Min(lo, b.Position);
                        hi = Vector3.Max(hi, b.Position);
                        reach = math.max(reach, b.Length + b.Scale.x);
                    }
                    var bounds = new Bounds((lo + hi) * 0.5f, hi - lo + Vector3.one * (2f * reach));
                    Graphics.RenderMeshInstanced(Params(0, bounds, Block(block++, count)), _capsule, 0, matrices, count, start);
                }
            }
            if (_flashes.Count == 0) return;
            _layers.Clear();
            foreach (var f in _flashes) if (!_layers.Contains(f.Layer)) _layers.Add(f.Layer);
            foreach (var layer in _layers)
            {
                var count = 0;
                var lo = Vector3.positiveInfinity;
                var hi = Vector3.negativeInfinity;
                var reach = 0f;
                for (var i = 0; i < _flashes.Count; i++)
                {
                    var f = _flashes[i];
                    if (f.Layer != layer) continue;
                    var size = f.Size * Mathf.Lerp(0.4f, 1f, f.Age / f.Life);
                    _flashMatrices[count] = Matrix4x4.TRS(f.Position, Quaternion.identity, Vector3.one * size);
                    _colors[count] = f.Color;
                    lo = Vector3.Min(lo, f.Position);
                    hi = Vector3.Max(hi, f.Position);
                    reach = math.max(reach, size);
                    if (++count == Batch) Flush();
                }
                if (count > 0) Flush();

                void Flush()
                {
                    if (count == 0) return;
                    var bounds = new Bounds((lo + hi) * 0.5f, hi - lo + Vector3.one * (2f * reach));
                    Graphics.RenderMeshInstanced(Params(layer, bounds, Block(block++, count)), _sphere, 0, _flashMatrices, count);
                    count = 0;
                    lo = Vector3.positiveInfinity;
                    hi = Vector3.negativeInfinity;
                    reach = 0f;
                }
            }
        }

        /// <summary>The frame's `i`th property block, holding the first `count` of the scratch colours.</summary>
        MaterialPropertyBlock Block(int i, int count)
        {
            while (_blocks.Count <= i) _blocks.Add(new MaterialPropertyBlock());
            var b = _blocks[i];
            b.Clear();
            b.SetVectorArray(BaseColor, _colors);   // whole array (a block's array size is fixed by its first set); the draw reads `count`
            return b;
        }

        static RenderParams Params(int layer, Bounds bounds, MaterialPropertyBlock block) => new(_material)
        {
            layer = layer,
            worldBounds = bounds,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            matProps = block,
        };

        void Grow(int n)
        {
            if (_rays.IsCreated && _rays.Length >= n) return;
            var size = math.max(16, math.ceilpow2(n));
            if (_rays.IsCreated) _rays.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            if (_matrices.IsCreated) _matrices.Dispose();
            _rays = new NativeArray<RaycastCommand>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _hits = new NativeArray<RaycastHit>(size * MaxHits, Allocator.Persistent);
            _matrices = new NativeArray<Matrix4x4>(size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Drop every bolt and flash, keeping the field.</summary>
        public void Clear()
        {
            _shots.Clear();
            _flashes.Clear();
            if (_bolts.IsCreated) _bolts.Clear();
        }

        void FreeNative()
        {
            if (_bolts.IsCreated) _bolts.Dispose();
            if (_rays.IsCreated) _rays.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            if (_matrices.IsCreated) _matrices.Dispose();
            _shots.Clear();
            _flashes.Clear();
        }

        public void Dispose()
        {
            FreeNative();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
