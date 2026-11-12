using System;
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
    /// Every monster's blob shadow: a soft dark patch on the ground under it (LuckyNumbers/MonsterBlob, multiplied over
    /// the ground). The game has no shadow maps, so this fakes one. Every monster, VAT crowd and placeholder block
    /// alike, goes through this one instanced draw path: no GameObject, renderer or raycast per blob.
    ///
    /// A monster joins with its footprint (MonsterView, at creation). Whoever moves it says where the ground is under
    /// it (<see cref="MonsterView.Grounded"/>: CombatPresenter passes the surface height it already raycast).
    ///
    /// Each frame, in LateUpdate after everyone has moved, a Burst job over the monsters' transforms:
    /// - fades each blob by distance to the camera (<see cref="FadeNear"/> to <see cref="FadeFar"/>) and by how far
    ///   the monster is off the ground (a hop, the death sink); a block shrinking away shrinks its blob too;
    /// - skips the invisible ones and those outside the camera's frustum;
    /// - nudges the rest away from the sun (_LnSunPosition), so it reads as a shadow, not a halo;
    /// - bends each one to the terrain. The hills curve by tens of centimetres across a blob, more than any depth bias
    ///   can hide at eye level, so a flat quad would sink into the ground. Seven GroundShape.HeightAt samples per blob give
    ///   the slope (sheared into its matrix) and the curvature (per instance, applied on a 6 x 6 grid in the vertex
    ///   shader). The centre sits on the raycast surface it was told, not the height function.
    /// The main thread packs the visible ones and draws them in batches of 64, as BoltField does. Each instance is
    /// 2 matrices + 1 vector, so a batch is 9 KB of WebGL2's 16 KB uniform buffer.
    ///
    /// One per scene: its runner lives in the active scene and frees the native memory when the scene goes.
    /// ?ln-noblobs in the page's URL (or -lnNoBlobs) turns them off, for A/B frame-time tests.
    /// </summary>
    public sealed class MonsterBlobs
    {
        const int Batch = 64;
        /// <summary>Full strength out to this far from the camera (m)...</summary>
        public const float FadeNear = 16f;
        /// <summary>...gone by this far (m): nothing to draw, and too small to read anyway.</summary>
        public const float FadeFar = 28f;
        /// <summary>Blob size over the monster's footprint (MonsterType.size x / z).</summary>
        const float Spread = 2f;
        /// <summary>Above the ground (m); the shader also pulls it toward the camera.</summary>
        const float Lift = 0.04f;
        /// <summary>Nudge away from the sun: this x the monster's height x cot(sun elevation), at most <see cref="MaxShift"/>.</summary>
        const float SunShift = 0.25f;
        /// <summary>Most the sun nudge moves it, as a fraction of the blob's radius (keeps it under the monster).</summary>
        const float MaxShift = 0.25f;
        /// <summary>Lifted (or sunk) its own height off the ground, a blob is gone; on the way it shrinks to this.</summary>
        const float RiseShrink = 0.6f;
        /// <summary>Grid cells per side of the blob's mesh (it bends with the terrain's curvature).</summary>
        const int Cells = 6;

        static readonly int BendId = Shader.PropertyToID("_BlobBend");
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static MonsterBlobs _current;
        static Material _material;
        static Mesh _mesh;

        /// <summary>This scene's blobs (made on first use, and again once its scene is gone).</summary>
        public static MonsterBlobs Current => _current != null && _current.Alive ? _current : _current = new MonsterBlobs();

        static bool? _enabled;
        /// <summary>Off with ?ln-noblobs (Web) / -lnNoBlobs, for A/B tests.</summary>
        public static bool Enabled
        {
            get => _enabled ??= !Application.absoluteURL.Contains("ln-noblobs") &&
                                Array.IndexOf(Environment.GetCommandLineArgs(), "-lnNoBlobs") < 0;
            set => _enabled = value;
        }

        struct Blob
        {
            public float2 Foot;     // footprint x / z (m), at the scale it joined with
            public float Half;      // half its height (m): its root is at its middle
            public float Scale;     // its root's localScale.x when it joined (a block's scale is its size)
            public float Ground;    // the ground's height under it, last told
            public int HasGround, Hidden;
        }

        [BurstCompile]
        struct BlobJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<Blob> Blobs;
            [ReadOnly] public NativeArray<float4> Planes;
            public GroundShape Shape;
            public float3 Eye;
            public float2 Away;     // horizontal, away from the sun, length = cot(elevation) (clamped)
            [WriteOnly] public NativeArray<Matrix4x4> Matrices;
            [WriteOnly] public NativeArray<float4> Bend;   // curvature along its two axes and across (m), and strength (0: skip)

            public void Execute(int i, TransformAccess t)
            {
                var b = Blobs[i];
                Bend[i] = float4.zero;
                if (b.HasGround == 0 || b.Hidden != 0 || !t.isValid) return;
                float3 pos = t.position;
                var shrink = b.Scale > 0f ? math.saturate(t.localScale.x / b.Scale) : 1f;
                var near = 1f - math.smoothstep(FadeNear, FadeFar, math.distance(pos, Eye));
                var height = 2f * b.Half;
                var rise = math.saturate(math.abs(pos.y - b.Half * shrink - b.Ground) / math.max(0.3f, height));
                var alpha = near * (1f - rise) * (1f - rise) * shrink;
                if (alpha < 0.01f) return;

                var foot = b.Foot * Spread * shrink * math.lerp(1f, RiseShrink, rise);
                var radius = math.cmax(foot) * 0.5f;
                var shift = Away * (SunShift * height * shrink);
                var len = math.length(shift);
                if (len > MaxShift * radius) shift *= MaxShift * radius / len;
                var c = pos.xz + shift;
                var centre = new float3(c.x, b.Ground, c.y);
                for (var p = 0; p < 6; p++)
                {
                    var plane = Planes[p];
                    if (math.dot(plane.xyz, centre) + plane.w < -radius) return;
                }

                // Its footprint along its heading (horizontal axes), then the terrain under it: heights at its middle,
                // the ends of both axes and one corner give slope and curvature along each, and the twist between.
                var f3 = math.mul((quaternion)t.rotation, new float3(0f, 0f, 1f));
                var fwd = math.normalizesafe(f3.xz, new float2(0f, 1f));
                var right = new float2(fwd.y, -fwd.x);
                var ar = right * (foot.x * 0.5f);
                var af = fwd * (foot.y * 0.5f);
                var h0 = Shape.HeightAt(c);
                var hr = Shape.HeightAt(c + ar);
                var hl = Shape.HeightAt(c - ar);
                var hf = Shape.HeightAt(c + af);
                var hb = Shape.HeightAt(c - af);
                var hc = Shape.HeightAt(c + ar + af);
                var slopeR = (hr - hl) * 0.5f;            // per unit of U (-1..1 along its width)
                var slopeF = (hf - hb) * 0.5f;            // per unit of V (along its length)
                var bendR = (hr + hl) * 0.5f - h0;        // x U^2
                var bendF = (hf + hb) * 0.5f - h0;        // x V^2
                var twist = hc - h0 - slopeR - slopeF - bendR - bendF;   // x U V
                // The middle on the raycast surface under its root (the mesh you see), plus the sun shift's climb.
                var y = b.Ground + (h0 - Shape.HeightAt(pos.xz)) + Lift;
                // Object space is -0.5..0.5 (U = 2x, V = 2z): the slope shears its axes up or down.
                Matrices[i] = new float4x4(new float4(right.x * foot.x, 2f * slopeR, right.y * foot.x, 0f),
                                           new float4(0f, 1f, 0f, 0f),
                                           new float4(fwd.x * foot.y, 2f * slopeF, fwd.y * foot.y, 0f),
                                           new float4(centre.x, y, centre.z, 1f));
                Bend[i] = new float4(bendR, bendF, twist, alpha);
            }
        }

        sealed class Runner : MonoBehaviour
        {
            public MonsterBlobs Blobs;
            void LateUpdate() => Blobs?.Draw();
            void OnDestroy() => Blobs?.FreeNative();
        }

        /// <summary>A monster's blob: its slot (MonsterView keeps one).</summary>
        public sealed class Member
        {
            internal MonsterBlobs Blobs;
            internal int Slot = -1;
        }

        readonly Runner _runner;
        NativeList<Blob> _blobs;
        TransformAccessArray _transforms;
        readonly List<Member> _members = new();
        NativeArray<Matrix4x4> _matrices, _drawn;
        NativeArray<float4> _bend, _drawnBend;
        NativeArray<float4> _planes;
        readonly Plane[] _frustum = new Plane[6];
        readonly Vector4[] _batchBend = new Vector4[Batch];
        readonly List<MaterialPropertyBlock> _blocks = new();
        int _capacity;
        bool _freed;

        /// <summary>The ground the blobs lie on (its shape bends each to the terrain). Without it they lie flat.</summary>
        public GroundView Ground { get; set; }

        MonsterBlobs()
        {
            _material ??= Resources.Load<Material>("MonsterBlob");
            if (_material == null) Debug.LogError("[MonsterBlobs] Resources/MonsterBlob material missing: no blob shadows");
            else _material.enableInstancing = true;
            _mesh ??= Grid(Cells);
            _runner = new GameObject("[MonsterBlobs]").AddComponent<Runner>();
            _runner.Blobs = this;
            _blobs = new NativeList<Blob>(16, Allocator.Persistent);
            _transforms = new TransformAccessArray(16);
            _planes = new NativeArray<float4>(6, Allocator.Persistent);
        }

        bool Alive => _runner != null && !_freed;

        public int Count => _members.Count;
        public int DrawnLastFrame { get; private set; }

        /// <summary>The blob's mesh: a flat `cells` x `cells` grid in the XZ plane, -0.5..0.5 (the shader bends it).</summary>
        static Mesh Grid(int cells)
        {
            var n = cells + 1;
            var vertices = new Vector3[n * n];
            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
                vertices[z * n + x] = new Vector3((float)x / cells - 0.5f, 0f, (float)z / cells - 0.5f);
            var triangles = new int[cells * cells * 6];
            for (int z = 0, t = 0; z < cells; z++)
            for (var x = 0; x < cells; x++, t += 6)
            {
                var i = z * n + x;
                triangles[t] = i; triangles[t + 1] = i + n; triangles[t + 2] = i + 1;
                triangles[t + 3] = i + 1; triangles[t + 4] = i + n; triangles[t + 5] = i + n + 1;
            }
            var mesh = new Mesh { name = "MonsterBlob" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>`root` joins: a monster `size` big (its collider), its root at the box's middle.</summary>
        public Member Add(Transform root, Vector3 size)
        {
            if (_freed) return null;
            _blobs.Add(new Blob { Foot = new float2(size.x, size.z), Half = size.y * 0.5f, Scale = root.localScale.x });
            _transforms.Add(root);
            var m = new Member { Blobs = this, Slot = _members.Count };
            _members.Add(m);
            return m;
        }

        /// <summary>Leaves (its slot goes to the last one's).</summary>
        public void Remove(Member m)
        {
            if (m == null || m.Slot < 0) return;
            if (_freed || !_blobs.IsCreated) { m.Slot = -1; return; }
            var i = m.Slot;
            var last = _members.Count - 1;
            _blobs.RemoveAtSwapBack(i);
            _transforms.RemoveAtSwapBack(i);
            _members[i] = _members[last];
            _members.RemoveAt(last);
            if (i < _members.Count) _members[i].Slot = i;
            m.Slot = -1;
        }

        static bool Valid(Member m) => m?.Blobs != null && m.Slot >= 0 && !m.Blobs._freed;

        /// <summary>The ground's height under it (where its blob lies); until told, it has none.</summary>
        public void SetGround(Member m, float y)
        {
            if (!Valid(m)) return;
            ref var b = ref m.Blobs._blobs.ElementAt(m.Slot);
            b.Ground = y;
            b.HasGround = 1;
        }

        public void SetHidden(Member m, bool hidden)
        {
            if (!Valid(m)) return;
            m.Blobs._blobs.ElementAt(m.Slot).Hidden = hidden ? 1 : 0;
        }

        void Grow(int n)
        {
            if (_capacity >= n) return;
            FreeBuffers();
            _capacity = math.max(16, math.ceilpow2(n));
            _matrices = new NativeArray<Matrix4x4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _drawn = new NativeArray<Matrix4x4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _bend = new NativeArray<float4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _drawnBend = new NativeArray<float4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        void Draw()
        {
            DrawnLastFrame = 0;
            var n = _members.Count;
            var cam = Camera.main;
            if (_freed || n == 0 || cam == null || _material == null || !Enabled) return;
            Grow(n);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            for (var p = 0; p < 6; p++) _planes[p] = new float4(_frustum[p].normal, _frustum[p].distance);
            var eye = cam.transform.position;
            // Away from the sun, as far as a shadow falls (cot of its elevation), capped so a low sun doesn't fling it.
            var sun = (Vector4)Shader.GetGlobalVector(SunPosition);
            var toSun = sun.w > 0.5f ? math.normalizesafe((float3)(Vector3)sun - (float3)eye) : math.normalize(new float3(0.4f, 0.8f, 0.3f));
            var flat = math.length(toSun.xz);
            var away = flat > 1e-4f ? -toSun.xz / flat * math.min(2f, flat / math.max(0.05f, toSun.y)) : float2.zero;

            new BlobJob
            {
                Blobs = _blobs.AsArray(), Planes = _planes, Shape = Ground != null ? Ground.Shape : default, Eye = eye, Away = away,
                Matrices = _matrices, Bend = _bend,
            }.ScheduleReadOnly(_transforms, 32).Complete();

            var count = 0;
            var lo = new float3(float.MaxValue);
            var hi = new float3(float.MinValue);
            for (var i = 0; i < n; i++)
            {
                if (_bend[i].w <= 0f) continue;
                var m = _matrices[i];
                _drawn[count] = m;
                _drawnBend[count] = _bend[i];
                var c = (float3)(Vector3)m.GetColumn(3);
                var r = math.max(((Vector3)m.GetColumn(0)).magnitude, ((Vector3)m.GetColumn(2)).magnitude);
                lo = math.min(lo, c - r);
                hi = math.max(hi, c + r);
                count++;
            }
            DrawnLastFrame = count;
            if (count == 0) return;
            var bounds = new Bounds((lo + hi) * 0.5f, hi - lo);   // one box for all: a blob's draw is tiny either way
            var bend = _drawnBend.Reinterpret<Vector4>();
            var block = 0;
            for (var start = 0; start < count; start += Batch)
            {
                var k = math.min(Batch, count - start);
                NativeArray<Vector4>.Copy(bend, start, _batchBend, 0, k);
                var props = Block(block++);
                props.SetVectorArray(BendId, _batchBend);   // whole array (a block's array size is fixed by its first set); the draw reads `k`
                var rp = new RenderParams(_material)
                {
                    layer = 0,
                    worldBounds = bounds,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    lightProbeUsage = LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.Off,
                    matProps = props,
                };
                Graphics.RenderMeshInstanced(rp, _mesh, 0, _drawn, k, start);
            }
            if (Stats && Time.unscaledTime >= _statsAt)
            {
                _statsAt = Time.unscaledTime + 5f;
                Debug.Log($"[MonsterBlobs] {n} blob(s), {count} drawn in {block} instanced draw(s)");
            }
        }

        float _statsAt;
        static bool? _stats;
        /// <summary>?ln-perf in the page's URL: every 5 s, how many it has and drew.</summary>
        static bool Stats => _stats ??= Application.absoluteURL.Contains("ln-perf");

        MaterialPropertyBlock Block(int i)
        {
            while (_blocks.Count <= i) _blocks.Add(new MaterialPropertyBlock());
            var b = _blocks[i];
            b.Clear();
            return b;
        }

        void FreeBuffers()
        {
            if (_matrices.IsCreated) _matrices.Dispose();
            if (_drawn.IsCreated) _drawn.Dispose();
            if (_bend.IsCreated) _bend.Dispose();
            if (_drawnBend.IsCreated) _drawnBend.Dispose();
            _capacity = 0;
        }

        void FreeNative()
        {
            if (_freed) return;
            _freed = true;
            FreeBuffers();
            if (_blobs.IsCreated) _blobs.Dispose();
            if (_transforms.isCreated) _transforms.Dispose();
            if (_planes.IsCreated) _planes.Dispose();
            foreach (var m in _members) m.Slot = -1;
            _members.Clear();
            if (_current == this) _current = null;
        }

        public void Dispose()
        {
            FreeNative();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
