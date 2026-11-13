using System;
using System.Collections.Generic;
using LuckyNumbers.Model;
using LuckyNumbers.Rendering;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Planet
{
    /// <summary>What a blob shadow is cast by: its box, where its root sits in it, and how its ground is found.</summary>
    public struct BlobSpec
    {
        /// <summary>The caster's box (m): footprint x (across) / z (along its facing), height y. At the root's scale now.</summary>
        public Vector3 Size;
        /// <summary>The box's bottom, from the root along world up (m): -Size.y / 2 for a root in the middle, 0 at the feet.</summary>
        public float Bottom;
        /// <summary>The footprint's middle from the root (m): x along its right, y along its facing (a ship's hull off its pivot).</summary>
        public Vector2 Offset;
        /// <summary>Gone this far above the ground (m); 0: its own height.</summary>
        public float FadeHeight;
        /// <summary>Its fade distances times this (0: 1). Big things are seen further.</summary>
        public float FarScale;
        /// <summary>0..1 (0: 1).</summary>
        public float Strength;
        /// <summary>Shrinks with its root's localScale.x (a placeholder block's scale is its size).</summary>
        public bool TrackScale;
        /// <summary>Finds its own ground (the terrain, else a ray down); otherwise whoever moves it says (SetGround).</summary>
        public bool Probe;
        /// <summary>Small stuff (pebbles, crates): none on low-end devices.</summary>
        public bool Minor;

        /// <summary>A box `size` big with its root in the middle (monsters, placeholder blocks, pebbles).</summary>
        public static BlobSpec Centred(Vector3 size) => new() { Size = size, Bottom = -size.y * 0.5f };

        /// <summary>
        /// The box round `root`'s renderers in its own frame (a ship's hull, a relic's model), in metres at its scale
        /// now; false if it has none yet.
        /// </summary>
        public static bool Fit(Transform root, IReadOnlyList<Renderer> renderers, out BlobSpec spec)
        {
            spec = default;
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = -lo;
            var toRoot = root.worldToLocalMatrix;
            var any = false;
            for (var i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                var b = r.localBounds;
                var m = toRoot * r.localToWorldMatrix;
                for (var c = 0; c < 8; c++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) != 0 ? 1f : -1f, (c & 2) != 0 ? 1f : -1f, (c & 4) != 0 ? 1f : -1f));
                    var p = m.MultiplyPoint3x4(corner);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                }
                any = true;
            }
            if (!any) return false;
            var scale = root.lossyScale;
            lo = Vector3.Scale(lo, scale);
            hi = Vector3.Scale(hi, scale);
            spec = new BlobSpec
            {
                Size = hi - lo,
                Bottom = lo.y,
                Offset = new Vector2((lo.x + hi.x) * 0.5f, (lo.z + hi.z) * 0.5f),
            };
            return true;
        }
    }

    /// <summary>
    /// Every blob shadow in the scene: a soft dark patch on the ground under each caster (LuckyNumbers/BlobShadow,
    /// multiplied over the ground). The game has no shadow maps, so this fakes them. Everything goes through this one
    /// instanced draw path, no GameObject, renderer or raycast per blob: monsters (MonsterView, VAT crowd and placeholder
    /// blocks alike), you on foot (FirstPersonPresenter), your fighter and the wreck, pebbles, relics, the hold's
    /// creatures and chests, the range's crates. Tunables: content/shadows.txtpb (GameContent.Shadows).
    ///
    /// A caster joins with its box (<see cref="BlobSpec"/>): through <see cref="Add"/> (MonsterView keeps its slot) or
    /// a <see cref="BlobCaster"/> on its GameObject (joins while it's enabled). Its ground is told (whoever moves it,
    /// e.g. CombatPresenter passes the surface it already raycast: <see cref="SetGround"/>) or probed: on the main thread,
    /// only when it's moved 2 cm or more and is in range: the terrain's surface (GroundView.SurfaceAt, the near chunk's
    /// collider alone) where there's terrain, else a ray down (the hold, the station).
    ///
    /// Each frame, in LateUpdate after everyone has moved, a Burst job over the casters' transforms:
    /// - fades each blob by distance to the camera (fade_near to fade_far) and by how far its bottom is off the ground
    ///   (a hop, a jump, the death sink); a block shrinking away shrinks its blob too;
    /// - shapes it: the footprint's ellipse (spread x its box) along its facing. Under a sun (<see cref="Sunlit"/>, the
    ///   crash site) it's swept away from the sun (_LnSunPosition) by its height x cot(elevation): a low sun gives a
    ///   long, faint shadow, a high one a tight dark blob. Indoors (the hold, the station) it's straight down. The
    ///   shader adds a darker contact core under its middle;
    /// - skips the invisible ones and those outside the camera's frustum;
    /// - bends each one to the terrain. The hills curve by tens of centimetres across a blob, more than any depth bias
    ///   can hide at eye level, so a flat quad would sink into the ground. Seven GroundShape.HeightAt samples per blob give
    ///   the slope (sheared into its matrix) and the curvature (per instance, applied on a 6 x 6 grid in the vertex
    ///   shader). The centre sits on the ground it was told or probed, not the height function. No terrain: flat.
    /// The main thread packs the visible ones and draws them in batches of 64, as BoltField does. Each instance is
    /// 2 matrices + 2 vectors, so a batch is 10 KB of WebGL2's 16 KB uniform buffer.
    ///
    /// Low-end (Rendering/LowEnd): shorter fade distances (low_end_distance) and no minor casters.
    /// One per scene: its runner lives in the active scene and frees the native memory when the scene goes.
    /// ?ln-noblobs in the page's URL (or -lnNoBlobs) turns them off, for A/B frame-time tests; ?ln-perf logs counts.
    /// </summary>
    public sealed class BlobShadows
    {
        const int Batch = 64;
        /// <summary>Above the ground (m); the shader also pulls it toward the camera.</summary>
        const float Lift = 0.04f;
        /// <summary>Grid cells per side of the blob's mesh (it bends with the terrain's curvature).</summary>
        const int Cells = 6;
        /// <summary>A probe re-reads its ground once it's moved this far across (m, squared).</summary>
        const float ReprobeSq = 0.02f * 0.02f;
        /// <summary>A probe's ray reaches this far below the caster's bottom (m).</summary>
        const float ProbeDepth = 30f;

        static readonly int BendId = Shader.PropertyToID("_BlobBend");
        static readonly int ShapeId = Shader.PropertyToID("_BlobShape");
        static readonly int SunPosition = Shader.PropertyToID("_LnSunPosition");
        static BlobShadows _current;
        static Material _material;
        static Mesh _mesh;

        /// <summary>This scene's blobs (made on first use, and again once its scene is gone).</summary>
        public static BlobShadows Current => _current != null && _current.Alive ? _current : _current = new BlobShadows();

        static bool? _enabled;
        /// <summary>Off with ?ln-noblobs (Web) / -lnNoBlobs, for A/B tests.</summary>
        public static bool Enabled
        {
            get => _enabled ??= !Application.absoluteURL.Contains("ln-noblobs") &&
                                Array.IndexOf(Environment.GetCommandLineArgs(), "-lnNoBlobs") < 0;
            set => _enabled = value;
        }

        /// <summary>The tunables (content/shadows.txtpb), unset fields at their defaults.</summary>
        public struct Tuning
        {
            public float FadeNear, FadeFar, LowEndDistance, Spread, Softness, BodyStrength, CoreStrength, CoreSize;
            public float SunStretch, MaxSweep, MaxCot, LowSunFade, TipFade, RiseShrink, RiseSoften;
            public float PlayerStrength, ShipFadeHeight, ShipFarScale;
        }

        static Tuning? _tuning;
        public static Tuning Tune => _tuning ??= Read(GameContent.Shadows);

        static Tuning Read(BlobShadowConfig c)
        {
            static float Or(float v, float d) => v > 0f ? v : d;
            c ??= new BlobShadowConfig();
            return new Tuning
            {
                FadeNear = Or(c.FadeNear, 16f), FadeFar = Or(c.FadeFar, 28f), LowEndDistance = Or(c.LowEndDistance, 0.7f),
                Spread = Or(c.Spread, 1.5f), Softness = Or(c.Softness, 0.55f), BodyStrength = Or(c.BodyStrength, 0.7f),
                CoreStrength = Or(c.CoreStrength, 0.45f), CoreSize = Or(c.CoreSize, 0.5f), SunStretch = Or(c.SunStretch, 0.5f),
                MaxSweep = Or(c.MaxSweep, 1.5f), MaxCot = Or(c.MaxCot, 2.5f), LowSunFade = Or(c.LowSunFade, 0.55f),
                TipFade = Or(c.TipFade, 0.35f), RiseShrink = Or(c.RiseShrink, 0.75f), RiseSoften = Or(c.RiseSoften, 0.35f),
                PlayerStrength = Or(c.PlayerStrength, 0.8f), ShipFadeHeight = Or(c.ShipFadeHeight, 18f), ShipFarScale = Or(c.ShipFarScale, 3f),
            };
        }

        struct Blob
        {
            public float2 Foot;     // footprint x / z (m), at the scale it joined with
            public float2 Offset;   // footprint's middle off the root (m): along its right, its facing
            public float Bottom;    // its bottom off the root along up (m)
            public float Height;    // its height (m)
            public float Fade;      // gone this far off the ground (m)
            public float Far;       // fade distance scale
            public float Strength;
            public float Scale;     // its root's localScale.x when it joined (0: not tracked)
            public float Ground;    // the ground's height under it, last told / probed
            public int HasGround, Hidden;
        }

        [BurstCompile]
        struct BlobJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<Blob> Blobs;
            [ReadOnly] public NativeArray<float4> Planes;
            public GroundShape Shape;
            public bool HasShape;
            public float3 Eye;
            public float2 Away;     // horizontal, away from the sun, length = cot(elevation) (capped); zero: straight down
            public float Sun;       // strength from the sun's elevation (1 overhead / indoors)
            public float FadeNear, FadeFar, Spread, Body, Core, Stretch, MaxSweep, RiseShrink, RiseSoften;
            [WriteOnly] public NativeArray<Matrix4x4> Matrices;
            [WriteOnly] public NativeArray<float4> Bend;    // curvature along its two axes and across (m), and body strength (0: skip)
            [WriteOnly] public NativeArray<float4> Shapes;  // sun sweep (footprint units), extra softness, core strength

            public void Execute(int i, TransformAccess t)
            {
                var b = Blobs[i];
                Bend[i] = float4.zero;
                if (b.HasGround == 0 || b.Hidden != 0 || !t.isValid) return;
                float3 pos = t.position;
                var shrink = b.Scale > 0f ? math.saturate(t.localScale.x / b.Scale) : 1f;
                var foot0 = b.Foot * shrink;
                var height = b.Height * shrink;
                // Distance to its near edge, so a big one (a ship) doesn't vanish while you stand beside it.
                var dist = math.max(0f, math.distance(pos, Eye) - math.cmax(foot0) * 0.5f);
                var near = 1f - math.smoothstep(FadeNear * b.Far, FadeFar * b.Far, dist);
                var above = pos.y + b.Bottom * shrink - b.Ground;
                var rise = math.saturate(math.abs(above) / math.max(0.3f, b.Fade * shrink));
                var fade = near * (1f - rise) * (1f - rise) * shrink * b.Strength;
                if (fade < 0.01f) return;

                // Its footprint along its heading (horizontal axes): an ellipse of radius `radii`.
                var f3 = math.mul((quaternion)t.rotation, new float3(0f, 0f, 1f));
                var fwd = math.normalizesafe(f3.xz, new float2(0f, 1f));
                var right = new float2(fwd.y, -fwd.x);
                var radii = math.max(foot0 * (Spread * 0.5f * math.lerp(1f, RiseShrink, rise)), 0.05f);
                var root = pos.xz + right * (b.Offset.x * shrink) + fwd * (b.Offset.y * shrink);
                // Swept away from the sun: its height's shadow (stylised, capped), plus however far it's lifted.
                var sweep = Away * (Stretch * height + math.max(0f, above));
                var len = math.length(sweep);
                var cap = MaxSweep * math.max(height, math.cmax(radii));
                if (len > cap) sweep *= cap / len;
                var sn = new float2(math.dot(sweep, right) / radii.x, math.dot(sweep, fwd) / radii.y);   // in footprint units
                var half = radii * (1f + math.abs(sn) * 0.5f);                                            // the quad's half-size (m)
                var c = root + sweep * 0.5f;
                var centre = new float3(c.x, b.Ground, c.y);
                var reach = math.length(half);
                for (var p = 0; p < 6; p++)
                {
                    var plane = Planes[p];
                    if (math.dot(plane.xyz, centre) + plane.w < -reach) return;
                }

                // The terrain under it: heights at its middle, the ends of both axes and one corner give slope and
                // curvature along each, and the twist between. No terrain (indoors): flat.
                var ar = right * half.x;
                var af = fwd * half.y;
                float slopeR = 0f, slopeF = 0f, bendR = 0f, bendF = 0f, twist = 0f, y = b.Ground;
                if (HasShape)
                {
                    var h0 = Shape.HeightAt(c);
                    var hr = Shape.HeightAt(c + ar);
                    var hl = Shape.HeightAt(c - ar);
                    var hf = Shape.HeightAt(c + af);
                    var hb = Shape.HeightAt(c - af);
                    var hc = Shape.HeightAt(c + ar + af);
                    slopeR = (hr - hl) * 0.5f;            // per unit of U (-1..1 along its width)
                    slopeF = (hf - hb) * 0.5f;            // per unit of V (along its length)
                    bendR = (hr + hl) * 0.5f - h0;        // x U^2
                    bendF = (hf + hb) * 0.5f - h0;        // x V^2
                    twist = hc - h0 - slopeR - slopeF - bendR - bendF;   // x U V
                    // The middle on the surface under its root (the mesh you see), plus the climb to where the quad's middle is.
                    y += h0 - Shape.HeightAt(pos.xz);
                }
                y += Lift;
                // Object space is -0.5..0.5 (U = 2x, V = 2z): the slope shears its axes up or down.
                Matrices[i] = new float4x4(new float4(right.x * half.x * 2f, 2f * slopeR, right.y * half.x * 2f, 0f),
                                           new float4(0f, 1f, 0f, 0f),
                                           new float4(fwd.x * half.y * 2f, 2f * slopeF, fwd.y * half.y * 2f, 0f),
                                           new float4(centre.x, y, centre.z, 1f));
                Bend[i] = new float4(bendR, bendF, twist, fade * Body * Sun);
                Shapes[i] = new float4(sn, RiseSoften * rise, fade * Core * (1f - rise));
            }
        }

        sealed class Runner : MonoBehaviour
        {
            public BlobShadows Blobs;
            void LateUpdate() => Blobs?.Draw();
            void OnDestroy() => Blobs?.FreeNative();
        }

        /// <summary>A caster's blob: its slot (MonsterView, BlobCaster keep one).</summary>
        public sealed class Caster
        {
            internal BlobShadows Owner;
            internal int Slot = -1;
            internal Transform Root;
            internal bool Probe;
            internal Vector3 Probed;

            /// <summary>The ground's height under it (where its blob lies); until told, it has none.</summary>
            public void SetGround(float y) => Owner?.SetGround(this, y);
            public void SetHidden(bool hidden) => Owner?.SetHidden(this, hidden);
            public void Remove() => Owner?.Remove(this);
        }

        readonly Runner _runner;
        readonly Material _look;
        NativeList<Blob> _blobs;
        TransformAccessArray _transforms;
        readonly List<Caster> _members = new();
        NativeArray<Matrix4x4> _matrices, _drawn;
        NativeArray<float4> _bend, _drawnBend, _shape, _drawnShape;
        NativeArray<float4> _planes;
        readonly Plane[] _frustum = new Plane[6];
        readonly Vector4[] _batchBend = new Vector4[Batch], _batchShape = new Vector4[Batch];
        readonly List<MaterialPropertyBlock> _blocks = new();
        int _capacity;
        bool _freed;

        /// <summary>The ground the blobs lie on (its shape bends each to the terrain; probes read its surface). Without it they lie flat and probes cast a ray.</summary>
        public GroundView Ground { get; set; }
        /// <summary>Under a sun (_LnSunPosition): blobs swept away from it. Off (the default: the hold, the station): straight down.</summary>
        public bool Sunlit { get; set; }
        /// <summary>The camera to fade and cull for while it's on (the landing's chase camera), else Camera.main.</summary>
        public Camera View { get; set; }
        /// <summary>What a probe's ray down can stand on (no terrain).</summary>
        public int ProbeMask { get; set; } = Physics.DefaultRaycastLayers;

        BlobShadows()
        {
            _material ??= Resources.Load<Material>("BlobShadow");
            if (_material == null) Debug.LogError("[BlobShadows] Resources/BlobShadow material missing: no blob shadows");
            else
            {
                // Its own copy: setting the asset's properties at runtime would write them to disk in the Editor.
                var t = Tune;
                _look = new Material(_material) { name = "BlobShadow (tuned)", enableInstancing = true };
                _look.SetFloat("_Softness", math.saturate(t.Softness));
                _look.SetFloat("_CoreSize", math.saturate(t.CoreSize));
                _look.SetFloat("_TipFade", math.saturate(t.TipFade));
            }
            _mesh ??= Grid(Cells);
            _runner = new GameObject("[BlobShadows]").AddComponent<Runner>();
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
            var mesh = new Mesh { name = "BlobShadow" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>`root` joins: a monster `size` big (its collider), its root at the box's middle, its ground told.</summary>
        public Caster Add(Transform root, Vector3 size)
        {
            var spec = BlobSpec.Centred(size);
            spec.TrackScale = true;
            return Add(root, spec);
        }

        /// <summary>`root` joins with its box (null: a minor caster on a low-end device, or the scene's gone).</summary>
        public Caster Add(Transform root, in BlobSpec spec)
        {
            if (_freed || root == null) return null;
            if (spec.Minor && LowEnd.Active) return null;
            var size = spec.Size;
            _blobs.Add(new Blob
            {
                Foot = new float2(math.max(0.05f, size.x), math.max(0.05f, size.z)),
                Offset = spec.Offset,
                Bottom = spec.Bottom,
                Height = math.max(0.05f, size.y),
                Fade = spec.FadeHeight > 0f ? spec.FadeHeight : math.max(0.05f, size.y),
                Far = spec.FarScale > 0f ? spec.FarScale : 1f,
                Strength = spec.Strength > 0f ? math.saturate(spec.Strength) : 1f,
                Scale = spec.TrackScale ? root.localScale.x : 0f,
            });
            _transforms.Add(root);
            var m = new Caster { Owner = this, Slot = _members.Count, Root = root, Probe = spec.Probe };
            _members.Add(m);
            return m;
        }

        /// <summary>Leaves (its slot goes to the last one's).</summary>
        public void Remove(Caster m)
        {
            if (m == null || m.Slot < 0 || m.Owner != this) return;
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

        bool Valid(Caster m) => m != null && m.Owner == this && m.Slot >= 0 && !_freed;

        /// <summary>The ground's height under it (where its blob lies); until told, it has none.</summary>
        public void SetGround(Caster m, float y)
        {
            if (!Valid(m)) return;
            ref var b = ref _blobs.ElementAt(m.Slot);
            b.Ground = y;
            b.HasGround = 1;
        }

        public void SetHidden(Caster m, bool hidden)
        {
            if (!Valid(m)) return;
            _blobs.ElementAt(m.Slot).Hidden = hidden ? 1 : 0;
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
            _shape = new NativeArray<float4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _drawnShape = new NativeArray<float4>(_capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        /// <summary>Probed casters in range that moved: their ground, from the terrain's surface or a ray down.</summary>
        void ProbeGround(Vector3 eye, float far)
        {
            for (var i = 0; i < _members.Count; i++)
            {
                var m = _members[i];
                if (!m.Probe) continue;
                ref var b = ref _blobs.ElementAt(i);
                if (b.Hidden != 0 || m.Root == null) continue;
                var p = m.Root.position;
                var reach = far * b.Far + math.cmax(b.Foot);
                if ((p - eye).sqrMagnitude > reach * reach) continue;
                var dx = p.x - m.Probed.x;
                var dz = p.z - m.Probed.z;
                if (b.HasGround != 0 && dx * dx + dz * dz < ReprobeSq) continue;   // the ground under it is the same (a jump, a bob)
                m.Probed = p;
                var middle = p + Vector3.up * (b.Bottom + b.Height * 0.5f);
                if (Ground != null)
                {
                    b.Ground = Ground.SurfaceAt(middle, 0f);
                    b.HasGround = 1;
                }
                else if (Physics.Raycast(middle, Vector3.down, out var hit, b.Height * 0.5f + ProbeDepth, ProbeMask, QueryTriggerInteraction.Ignore))
                {
                    b.Ground = hit.point.y;
                    b.HasGround = 1;
                }
                else b.HasGround = 0;
            }
        }

        void Draw()
        {
            DrawnLastFrame = 0;
            var n = _members.Count;
            var cam = View != null && View.isActiveAndEnabled ? View : Camera.main;
            if (_freed || n == 0 || cam == null || _look == null || !Enabled) return;
            Grow(n);
            var t = Tune;
            var distance = LowEnd.Active ? t.LowEndDistance : 1f;
            var eye = cam.transform.position;
            ProbeGround(eye, t.FadeFar * distance);
            GeometryUtility.CalculateFrustumPlanes(cam, _frustum);
            for (var p = 0; p < 6; p++) _planes[p] = new float4(_frustum[p].normal, _frustum[p].distance);
            // Away from the sun, as far as a shadow falls (cot of its elevation), capped so a low sun doesn't fling it;
            // and fainter the lower it is. Indoors: straight down, full strength.
            var away = float2.zero;
            var sun = 1f;
            if (Sunlit)
            {
                var at = (Vector4)Shader.GetGlobalVector(SunPosition);
                var toSun = at.w > 0.5f ? math.normalizesafe((float3)(Vector3)at - (float3)eye) : math.normalize(new float3(0.4f, 0.8f, 0.3f));
                var flat = math.length(toSun.xz);
                var lift = math.max(0.02f, toSun.y);
                away = flat > 1e-4f ? -toSun.xz / flat * math.min(t.MaxCot, flat / lift) : float2.zero;
                sun = math.lerp(t.LowSunFade, 1f, math.saturate(toSun.y));
            }

            new BlobJob
            {
                Blobs = _blobs.AsArray(), Planes = _planes, Shape = Ground != null ? Ground.Shape : default, HasShape = Ground != null,
                Eye = eye, Away = away, Sun = sun,
                FadeNear = t.FadeNear * distance, FadeFar = math.max(t.FadeNear + 0.1f, t.FadeFar) * distance,
                Spread = t.Spread, Body = math.saturate(t.BodyStrength), Core = math.saturate(t.CoreStrength),
                Stretch = t.SunStretch, MaxSweep = t.MaxSweep, RiseShrink = t.RiseShrink, RiseSoften = t.RiseSoften,
                Matrices = _matrices, Bend = _bend, Shapes = _shape,
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
                _drawnShape[count] = _shape[i];
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
            var shape = _drawnShape.Reinterpret<Vector4>();
            var block = 0;
            for (var start = 0; start < count; start += Batch)
            {
                var k = math.min(Batch, count - start);
                NativeArray<Vector4>.Copy(bend, start, _batchBend, 0, k);
                NativeArray<Vector4>.Copy(shape, start, _batchShape, 0, k);
                var props = Block(block++);
                props.SetVectorArray(BendId, _batchBend);   // whole arrays (a block's array size is fixed by its first set); the draw reads `k`
                props.SetVectorArray(ShapeId, _batchShape);
                var rp = new RenderParams(_look)
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
                Debug.Log($"[BlobShadows] {n} blob(s), {count} drawn in {block} instanced draw(s)");
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
            if (_shape.IsCreated) _shape.Dispose();
            if (_drawnShape.IsCreated) _drawnShape.Dispose();
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
            if (_look != null) Object.Destroy(_look);
            if (_current == this) _current = null;
        }

        public void Dispose()
        {
            FreeNative();
            if (_runner != null) Object.Destroy(_runner.gameObject);
        }
    }
}
