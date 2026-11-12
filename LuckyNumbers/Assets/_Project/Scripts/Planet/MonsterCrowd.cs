using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Every VAT monster in the scene (MonsterView's model path when its MonsterRig has a <see cref="MonsterVat"/>),
    /// drawn GPU-instanced: one RenderMeshInstanced per model per batch, LuckyNumbers/MonsterVat playing each one's clips
    /// from the bake's textures in the vertex stage. No Animator, no SkinnedMeshRenderer, no renderer per monster: a
    /// monster is its GameObject (moved by CombatPresenter, hit through its BoxCollider) and a <see cref="Body"/> here.
    ///
    /// A frame (LateUpdate, after everyone has moved): a Burst job over the monsters' transforms
    /// (IJobParallelForTransform) advances each body's two clips (the one playing and the one it's fading from, as
    /// MonsterRig's mixer), its hit flash, writes its matrix and shader frames and tests its clips' baked bounds against
    /// the main camera's frustum; the main thread packs the visible ones and draws them in batches that keep a batch's
    /// per-instance data (two matrices + two vectors) inside WebGL2's 16 KB uniform buffer, as BoltField does.
    /// One crowd per scene: its runner lives in the active scene and frees the native memory when the scene goes.
    /// </summary>
    public sealed class MonsterCrowd
    {
        /// <summary>Instances per draw: 64 x (2 matrices + 2 vectors) = 10 KB of WebGL2's 16 KB uniform buffer.</summary>
        const int Batch = 64;

        /// <summary>
        /// Cast shadows (the shader has a ShadowCaster pass). Off like the skinned model's renderers: the game's
        /// shaders don't sample URP's shadow maps, so a caster would only cost a shadow pass.
        /// </summary>
        public static bool CastShadows = false;

        static readonly int VatA = Shader.PropertyToID("_VatA");
        static readonly int VatB = Shader.PropertyToID("_VatB");
        static MonsterCrowd _current;

        /// <summary>This scene's crowd (made on first use, and again once its scene is gone).</summary>
        public static MonsterCrowd Current => _current != null && _current.Alive ? _current : _current = new MonsterCrowd();

        internal struct ClipInfo
        {
            public int Start, Frames;
            public float Length;
            public float3 Center, Extents;
        }

        /// <summary>One monster's animation: clip A playing (weight `Weight`), clip B the one it's fading from.</summary>
        internal struct Body
        {
            public int ClipA, ClipB;   // -1: none
            public float TimeA, TimeB, RateA, RateB;
            public int OnceA, OnceB;   // play once and hold the last frame (else loop)
            public float Weight, Fade; // A's weight, rising at Fade per second
            public float Flash;        // hit flash, seconds left
            public float Lift;         // the model's origin under the root: its feet on the bottom of the collider
            public int Hidden;
        }

        [BurstCompile]
        struct AnimateJob : IJobParallelForTransform
        {
            public NativeArray<Body> Bodies;
            [ReadOnly] public NativeArray<ClipInfo> Clips;
            [ReadOnly] public NativeArray<float4> Planes;
            public float Dt, FlashTime;
            public bool Cull;
            [WriteOnly] public NativeArray<Matrix4x4> Matrices;
            [WriteOnly] public NativeArray<float4> A, B;
            [WriteOnly] public NativeArray<float3> Lo, Hi;
            [WriteOnly] public NativeArray<byte> Visible;

            public void Execute(int i, TransformAccess t)
            {
                var b = Bodies[i];
                if (b.ClipA >= 0) b.TimeA = Advance(b.TimeA, b.RateA, b.OnceA != 0, Clips[b.ClipA].Length, Dt);
                if (b.ClipB >= 0) b.TimeB = Advance(b.TimeB, b.RateB, b.OnceB != 0, Clips[b.ClipB].Length, Dt);
                b.Weight = math.min(1f, b.Weight + Dt * b.Fade);
                if (b.Weight >= 1f) b.ClipB = -1;
                b.Flash = math.max(0f, b.Flash - Dt);
                Bodies[i] = b;
                if (b.ClipA < 0 || b.Hidden != 0 || !t.isValid) { Visible[i] = 0; return; }

                float4x4 m = math.mul((float4x4)t.localToWorldMatrix, float4x4.Translate(new float3(0f, b.Lift, 0f)));
                var ca = Clips[b.ClipA];
                var lo = ca.Center - ca.Extents;
                var hi = ca.Center + ca.Extents;
                var fa = Frames(ca.Start, ca.Frames, ca.Length, b.TimeA);
                var fb = fa;
                if (b.ClipB >= 0)
                {
                    var cb = Clips[b.ClipB];
                    lo = math.min(lo, cb.Center - cb.Extents);
                    hi = math.max(hi, cb.Center + cb.Extents);
                    fb = Frames(cb.Start, cb.Frames, cb.Length, b.TimeB);
                }
                // World AABB of the model-space box.
                var center = math.transform(m, (lo + hi) * 0.5f);
                var half = (hi - lo) * 0.5f;
                var r = new float3x3(m.c0.xyz, m.c1.xyz, m.c2.xyz);
                var extents = math.abs(r.c0) * half.x + math.abs(r.c1) * half.y + math.abs(r.c2) * half.z;
                if (Cull)
                    for (var p = 0; p < 6; p++)
                    {
                        var plane = Planes[p];
                        if (math.dot(plane.xyz, center) + plane.w + math.dot(math.abs(plane.xyz), extents) < 0f) { Visible[i] = 0; return; }
                    }
                Visible[i] = 1;
                Matrices[i] = m;
                Lo[i] = center - extents;
                Hi[i] = center + extents;
                A[i] = new float4(fa.x, fa.y, fa.z, b.ClipB >= 0 ? b.Weight : 1f);
                B[i] = new float4(fb.x, fb.y, fb.z, b.Flash / FlashTime);
            }
        }

        static float Advance(float time, float rate, bool once, float length, float dt)
        {
            if (length <= 0f) return 0f;
            time += rate * dt;
            if (once) return math.min(time, length);   // holds the last frame
            return time >= length ? time % length : time;
        }

        /// <summary>
        /// The shader's frame pair for a clip at `time` s: (first frame, next frame, blend between them), absolute
        /// frames, clamped to the clip.
        /// </summary>
        public static float3 Frames(int start, int frames, float length, float time)
        {
            var last = math.max(0, frames - 1);
            var f = length > 0f ? math.saturate(time / length) * last : 0f;
            var f0 = math.min((int)f, last);
            var f1 = math.min(f0 + 1, last);
            return new float3(start + f0, start + f1, f - f0);
        }

        /// <summary>Every monster of one model.</summary>
        internal sealed class Herd
        {
            public MonsterVat Vat;
            public NativeArray<ClipInfo> Clips;
            public NativeList<Body> Bodies;
            public TransformAccessArray Transforms;
            public readonly List<MonsterView> Owners = new();
            // Per body, written by the job; then the visible ones packed for the draw.
            public NativeArray<Matrix4x4> Matrices, Drawn;
            public NativeArray<float4> A, B, DrawnA, DrawnB;
            public NativeArray<float3> Lo, Hi;
            public NativeArray<byte> Visible;
            public int Capacity;

            public void Grow(int n)
            {
                if (Capacity >= n) return;
                Free(false);
                Capacity = math.max(16, math.ceilpow2(n));
                Matrices = new NativeArray<Matrix4x4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Drawn = new NativeArray<Matrix4x4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                A = new NativeArray<float4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                B = new NativeArray<float4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                DrawnA = new NativeArray<float4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                DrawnB = new NativeArray<float4>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Lo = new NativeArray<float3>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Hi = new NativeArray<float3>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Visible = new NativeArray<byte>(Capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            public void Free(bool all)
            {
                if (Matrices.IsCreated) Matrices.Dispose();
                if (Drawn.IsCreated) Drawn.Dispose();
                if (A.IsCreated) A.Dispose();
                if (B.IsCreated) B.Dispose();
                if (DrawnA.IsCreated) DrawnA.Dispose();
                if (DrawnB.IsCreated) DrawnB.Dispose();
                if (Lo.IsCreated) Lo.Dispose();
                if (Hi.IsCreated) Hi.Dispose();
                if (Visible.IsCreated) Visible.Dispose();
                Capacity = 0;
                if (!all) return;
                if (Clips.IsCreated) Clips.Dispose();
                if (Bodies.IsCreated) Bodies.Dispose();
                if (Transforms.isCreated) Transforms.Dispose();
                Owners.Clear();
            }
        }

        sealed class Runner : MonoBehaviour
        {
            public MonsterCrowd Crowd;
            void LateUpdate() => Crowd?.Draw();
            void OnDestroy() => Crowd?.FreeNative();
        }

        /// <summary>A monster in the crowd: its model and its slot there (MonsterView keeps one).</summary>
        public sealed class Member
        {
            internal MonsterCrowd Crowd;
            internal Herd Herd;
            internal int Slot = -1;
            public MonsterVat Vat => Herd?.Vat;
        }

        readonly Runner _runner;
        readonly Dictionary<MonsterVat, Herd> _herds = new();
        readonly List<Herd> _order = new();   // draw order: as they joined
        readonly Plane[] _planes = new Plane[6];
        NativeArray<float4> _nativePlanes;
        readonly Vector4[] _a = new Vector4[Batch], _b = new Vector4[Batch];
        readonly List<MaterialPropertyBlock> _blocks = new();
        bool _freed;

        MonsterCrowd()
        {
            _runner = new GameObject("[MonsterCrowd]").AddComponent<Runner>();
            _runner.Crowd = this;
            _nativePlanes = new NativeArray<float4>(6, Allocator.Persistent);
        }

        bool Alive => _runner != null && !_freed;

        /// <summary>How many monsters are in it, and how many it drew last frame (dev readouts).</summary>
        public int Count
        {
            get { var n = 0; foreach (var h in _order) n += h.Owners.Count; return n; }
        }
        public int DrawnLastFrame { get; private set; }

        /// <summary>`view` joins, drawn as `vat` with its model `lift` m under its root (its feet on its collider's bottom).</summary>
        public Member Add(MonsterView view, MonsterVat vat, float lift)
        {
            if (!_herds.TryGetValue(vat, out var herd))
            {
                herd = new Herd
                {
                    Vat = vat,
                    Clips = new NativeArray<ClipInfo>(vat.Clips.Length, Allocator.Persistent),
                    Bodies = new NativeList<Body>(16, Allocator.Persistent),
                    Transforms = new TransformAccessArray(16),
                };
                for (var i = 0; i < vat.Clips.Length; i++)
                {
                    var c = vat.Clips[i];
                    herd.Clips[i] = new ClipInfo { Start = c.Start, Frames = c.Frames, Length = c.Length, Center = c.Bounds.center, Extents = c.Bounds.extents };
                }
                vat.Material.enableInstancing = true;
                _herds[vat] = herd;
                _order.Add(herd);
            }
            herd.Bodies.Add(new Body { ClipA = -1, ClipB = -1, Weight = 1f, Lift = lift });
            herd.Transforms.Add(view.transform);
            herd.Owners.Add(view);
            return new Member { Crowd = this, Herd = herd, Slot = herd.Owners.Count - 1 };
        }

        /// <summary>Leaves (its slot goes to the last monster's).</summary>
        public void Remove(Member m)
        {
            if (m?.Herd == null || m.Slot < 0) return;
            var herd = m.Herd;
            if (_freed || !herd.Bodies.IsCreated) { m.Slot = -1; return; }
            var i = m.Slot;
            var last = herd.Owners.Count - 1;
            herd.Bodies.RemoveAtSwapBack(i);
            herd.Transforms.RemoveAtSwapBack(i);
            herd.Owners[i] = herd.Owners[last];
            herd.Owners.RemoveAt(last);
            if (i < herd.Owners.Count) herd.Owners[i].CrowdMember.Slot = i;
            m.Slot = -1;
        }

        static bool Valid(Member m) => m?.Herd != null && m.Slot >= 0 && m.Herd.Bodies.IsCreated;

        /// <summary>
        /// As MonsterRig.Play: `clip` (an index into the VAT's clips) fading in over `fade` s at `rate` x; `once` from
        /// its start, holding its last frame, else looping, and asking for the clip already looping only sets its rate.
        /// </summary>
        public void Play(Member m, int clip, float rate, bool once, float fade)
        {
            if (!Valid(m) || clip < 0) return;
            ref var b = ref m.Herd.Bodies.ElementAt(m.Slot);
            if (clip == b.ClipA)
            {
                b.RateA = rate;
                if (once) { b.TimeA = 0f; b.OnceA = 1; }   // replayed from its start (a looping clip asked again only changes rate)
                return;
            }
            float weight, time;
            if (b.ClipA < 0) { weight = 1f; time = 0f; }   // the first clip: straight in, not from the bind pose
            else if (clip == b.ClipB)
            {
                // Back to the one it was leaving: it carries on from where it is, A becomes the one fading out.
                weight = 1f - b.Weight;
                time = b.TimeB;
                b.ClipB = b.ClipA; b.TimeB = b.TimeA; b.RateB = b.RateA; b.OnceB = b.OnceA;
            }
            else
            {
                // A new clip: it fades in over whichever of the two showed more (two clips at most, unlike the mixer).
                weight = 0f;
                time = 0f;
                if (b.ClipB < 0 || b.Weight >= 0.5f) { b.ClipB = b.ClipA; b.TimeB = b.TimeA; b.RateB = b.RateA; b.OnceB = b.OnceA; }
            }
            if (once || weight < 0.01f) time = 0f;
            b.ClipA = clip;
            b.TimeA = time;
            b.RateA = rate;
            b.OnceA = once ? 1 : 0;
            b.Weight = weight;
            b.Fade = 1f / math.max(0.01f, fade);
        }

        /// <summary>Hit flash: the Ship shader's heat at `seconds`, fading to nothing over them.</summary>
        public void Flash(Member m, float seconds)
        {
            if (!Valid(m)) return;
            m.Herd.Bodies.ElementAt(m.Slot).Flash = seconds;
        }

        public void SetHidden(Member m, bool hidden)
        {
            if (!Valid(m)) return;
            m.Herd.Bodies.ElementAt(m.Slot).Hidden = hidden ? 1 : 0;
        }

        /// <summary>The hit flash's full length (MonsterView.Flash), for the shader's 0..1 heat.</summary>
        public const float FlashTime = 0.15f;

        void Draw()
        {
            if (_freed) return;
            var cam = Camera.main;
            var cull = cam != null && !CastShadows;   // a caster off screen can still throw a shadow on it
            if (cull)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _planes);
                for (var p = 0; p < 6; p++) _nativePlanes[p] = new float4(_planes[p].normal, _planes[p].distance);
            }
            var dt = Time.deltaTime;
            var block = 0;
            var drawn = 0;
            foreach (var herd in _order)
            {
                var n = herd.Owners.Count;
                if (n == 0) continue;
                herd.Grow(n);
                new AnimateJob
                {
                    Bodies = herd.Bodies.AsArray(), Clips = herd.Clips, Planes = _nativePlanes, Dt = dt, FlashTime = FlashTime, Cull = cull,
                    Matrices = herd.Matrices, A = herd.A, B = herd.B, Lo = herd.Lo, Hi = herd.Hi, Visible = herd.Visible,
                }.ScheduleReadOnly(herd.Transforms, 16).Complete();

                var count = 0;
                for (var i = 0; i < n; i++)
                {
                    if (herd.Visible[i] == 0) continue;
                    herd.Drawn[count] = herd.Matrices[i];
                    herd.DrawnA[count] = herd.A[i];
                    herd.DrawnB[count] = herd.B[i];
                    if (count != i) { herd.Lo[count] = herd.Lo[i]; herd.Hi[count] = herd.Hi[i]; }
                    count++;
                }
                drawn += count;
                var vat = herd.Vat;
                if (count == 0 || vat == null || vat.Mesh == null || vat.Material == null) continue;
                var a = herd.DrawnA.Reinterpret<Vector4>();
                var b = herd.DrawnB.Reinterpret<Vector4>();
                for (var start = 0; start < count; start += Batch)
                {
                    var k = math.min(Batch, count - start);
                    NativeArray<Vector4>.Copy(a, start, _a, 0, k);
                    NativeArray<Vector4>.Copy(b, start, _b, 0, k);
                    var lo = herd.Lo[start];
                    var hi = herd.Hi[start];
                    for (var j = start + 1; j < start + k; j++) { lo = math.min(lo, herd.Lo[j]); hi = math.max(hi, herd.Hi[j]); }
                    var props = Block(block++);
                    props.SetVectorArray(VatA, _a);   // whole arrays (a block's array size is fixed by its first set); the draw reads `k`
                    props.SetVectorArray(VatB, _b);
                    var rp = new RenderParams(vat.Material)
                    {
                        layer = herd.Owners[0].gameObject.layer,
                        worldBounds = new Bounds((lo + hi) * 0.5f, hi - lo),
                        shadowCastingMode = CastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = false,
                        lightProbeUsage = LightProbeUsage.Off,
                        reflectionProbeUsage = ReflectionProbeUsage.Off,
                        matProps = props,
                    };
                    for (var s = 0; s < vat.Mesh.subMeshCount; s++)
                        Graphics.RenderMeshInstanced(rp, vat.Mesh, s, herd.Drawn, k, start);
                }
            }
            DrawnLastFrame = drawn;
            if (Stats && Time.unscaledTime >= _statsAt && _order.Count > 0)
            {
                _statsAt = Time.unscaledTime + 5f;
                Debug.Log($"[MonsterCrowd] {Count} monster(s), {drawn} in view, {block} instanced draw(s)");
            }
        }

        float _statsAt;
        static bool? _stats;
        /// <summary>?ln-perf in the page's URL: every 5 s, how many it has, drew and in how many draws.</summary>
        static bool Stats => _stats ??= Application.absoluteURL.Contains("ln-perf");

        MaterialPropertyBlock Block(int i)
        {
            while (_blocks.Count <= i) _blocks.Add(new MaterialPropertyBlock());
            var b = _blocks[i];
            b.Clear();
            return b;
        }

        void FreeNative()
        {
            if (_freed) return;
            _freed = true;
            foreach (var herd in _order) herd.Free(true);
            _herds.Clear();
            _order.Clear();
            if (_nativePlanes.IsCreated) _nativePlanes.Dispose();
            if (_current == this) _current = null;
        }

        public void Dispose()
        {
            FreeNative();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
