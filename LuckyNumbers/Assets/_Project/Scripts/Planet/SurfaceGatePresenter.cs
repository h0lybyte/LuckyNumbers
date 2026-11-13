using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using Cysharp.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using VContainer;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// Where a crash site's hold gate stands (universes with holds): from the site seed alone, distance_min..max out from
    /// the site's middle on the side away from the wreck, its doorway facing the middle. Position is the foot of the
    /// facade's front, on the flattened pad.
    /// </summary>
    public readonly struct GatePlacement
    {
        public readonly bool Exists;
        public readonly Vector3 Position;
        /// <summary>The way the doorway faces (degrees around y): out of the hold, towards the site's middle.</summary>
        public readonly float Yaw;

        GatePlacement(Vector3 position, float yaw)
        {
            Exists = true;
            Position = position;
            Yaw = yaw;
        }

        public Quaternion Rotation => Quaternion.Euler(0f, Yaw, 0f);

        public static GatePlacement Place(uint siteSeed, SurfaceGate gate)
        {
            if (gate == null || gate.DistanceMax <= 0f) return default;
            var rng = new Random((siteSeed ^ 0x6A7E5EEDu) | 1u);   // its own draw: the rest of the site is unchanged
            var bearing = math.radians(rng.NextFloat(-60f, 60f));   // from straight behind where you wake (the wreck is ahead)
            var distance = rng.NextFloat(gate.DistanceMin, math.max(gate.DistanceMin, gate.DistanceMax));
            var p = new float2(math.sin(bearing), -math.cos(bearing)) * distance;
            return new GatePlacement(new Vector3(p.x, 0f, p.y), math.degrees(math.atan2(-p.x, -p.y)));
        }

        public GatePlacement At(float height) => new(new Vector3(Position.x, height, Position.z), Yaw);

        /// <summary>Where you stand coming back out: past the foot of the steps, facing away from the gate (y = pad).</summary>
        public Vector3 Outside(SurfaceGate gate) =>
            Position + Rotation * new Vector3(0f, 0f, StepsLength(gate) + 3f);

        /// <summary>From the facade's front to the foot of the bottom step (metres).</summary>
        public static float StepsLength(SurfaceGate gate) => math.max(0, gate.Steps - 1) * gate.StepDepth + Landing;

        /// <summary>Depth of the flat landing at the door (the top step).</summary>
        public const float Landing = 1.8f;
    }

    /// <summary>
    /// The hold's surface gate (universes with holds, ICrashSite.Gate): a stone facade cut into a rock mass, a dark
    /// doorway with a trim frame, two lanterns (real point lights, baked into the stone too) and steps up from the pad.
    /// Built once at runtime from boxes in one mesh with the hold's own material (HoldKit: uv0 = (u, v, slice), vertex
    /// colour = baked light), so it matches the hold below. Near the doorway "E  enter the hold"; E, or walking on into the
    /// dark, takes you down: HoldTrip.from_surface, then the Hold scene. Back out (HoldTrip.leaving), CrashSitePresenter
    /// stands you at the foot of the steps.
    /// </summary>
    [Presenter]
    public sealed class SurfaceGatePresenter : ITickable, IInteractable, IDisposable
    {
        readonly ICrashSite _site;
        readonly GroundView _ground;
        readonly PlayerView _player;
        readonly PlanetView _view;
        readonly IFirstPerson _firstPerson;
        readonly PlanetScene _config;
        readonly HoldTrip _trip;
        readonly IPublisher<SceneChangeRequested> _scenes;
        GameObject _root;
        Material _material;
        bool _built, _entering;
        static readonly object Door = new();

        SurfaceGate G => _config.Gate;

        [Inject]
        public SurfaceGatePresenter(ICrashSite site, GroundView ground, PlayerView player, PlanetView view, IFirstPerson firstPerson,
                                    PlanetScene config, HoldTrip trip, IPublisher<SceneChangeRequested> scenes)
        {
            _site = site;
            _ground = ground;
            _player = player;
            _view = view;
            _firstPerson = firstPerson;
            _config = config;
            _trip = trip;
            _scenes = scenes;
        }

        public void Tick()
        {
            if (!_built)
            {
                if (!_site.Built) return;
                _built = true;
                if (_site.Gate.Exists && G != null) Build();
            }
            if (_root == null || _entering) return;
            // Walking on into the dark takes you down too (E does it from the doorway: IInteractable, below).
            if (_firstPerson.Active && InDoorway(_player.Body.transform.position, out var local) && local.z < -G.WalkInDepth)
                EnterAsync().Forget();
        }

        /// <summary>In the doorway's reach; `local` is you in the gate's frame: x across the door, z out from the facade (negative = inside).</summary>
        bool InDoorway(Vector3 feet, out Vector3 local)
        {
            local = Quaternion.Inverse(_site.Gate.Rotation) * (feet - _site.Gate.Position);
            return math.abs(local.x) < G.DoorWidth * 0.5f && local.z < G.PromptDistance && local.z > -G.Depth;
        }

        public bool Offer(Vector3 feet, out Interaction offer)
        {
            offer = default;
            if (_root == null || _entering || !InDoorway(feet, out var local)) return false;
            offer = new Interaction(Door, InteractionPriority.Place, math.max(0f, local.z));
            return true;
        }

        public string Prompt(object key) => G.Prompt;

        public void Interact(object key)
        {
            if (!_entering) EnterAsync().Forget();
        }

        /// <summary>Fade out and go down: the hold's hall, through its gate.</summary>
        async Cysharp.Threading.Tasks.UniTaskVoid EnterAsync()
        {
            _entering = true;
            _firstPerson.Lock(ControlLock.Leaving);
            _view.Hint("", 0f);
            Debug.Log("[Gate] into the hold");
            for (var t = 0f; t < 0.6f; t += Time.deltaTime)
            {
                _view.Fade(t / 0.6f);
                await Cysharp.Threading.Tasks.UniTask.Yield();
                if (_root == null) return;   // the scene went meanwhile
            }
            _view.Fade(1f);
            _trip.FromSurface.Value = true;
            _trip.Leaving.Value = false;
            _scenes.Publish(new SceneChangeRequested(GameScene.Hold));
        }

        void Build()
        {
            var kit = _ground.HoldKit;
            if (kit == null) { Debug.LogWarning("[Gate] GroundView has no HoldKit material: no gate"); return; }
            Color sun = Color.white;
            if (_site.Bodies != null)
                foreach (var b in _site.Bodies)
                    if (b.Star != null) { sun = b.Star.LightColor.ToColor(); break; }
            var sky = _site.Look?.AtmosphereColor.ToColor() ?? Color.gray;
            _root = SurfaceGateBuilder.Build(_site.Gate, G, kit, _site.SunDirection, sun, sky,
                                             _config.ChunkSize * math.max(1, _config.ViewChunks) * 2.2f, out _material);
        }

        public void Dispose()
        {
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }

    /// <summary>The surface gate's mesh, colliders and lanterns (SurfaceGatePresenter; also Editor/DeepPreview).</summary>
    public sealed class SurfaceGateBuilder
    {

        readonly List<Vector3> _v = new();
        readonly List<Vector3> _n = new();
        readonly List<Color> _c = new();
        readonly List<Vector3> _uv = new();
        readonly List<int> _t = new();
        Vector3 _sunLocal, _ambient, _sunColor, _lanternColor;
        Vector3[] _lanterns = Array.Empty<Vector3>();

        GameObject _root;
        SurfaceGate G;

        /// <summary>
        /// The gate at `gate` (its pad height in Position.y): one mesh, box colliders, two lantern lights. `sun` lights
        /// it from `sunDirection` (baked), `sky` tints the ambient and (darker) the haze, full at `hazeDistance`.
        /// </summary>
        public static GameObject Build(GatePlacement gate, SurfaceGate config, Material kit, Vector3 sunDirection, Color sun,
                                       Color sky, float hazeDistance, out Material material) =>
            new SurfaceGateBuilder().Make(gate, config, kit, sunDirection, sun, sky, hazeDistance, out material);

        GameObject Make(GatePlacement gate, SurfaceGate config, Material kit, Vector3 sunDirection, Color sun,
                        Color sky, float hazeDistance, out Material material)
        {
            G = config;
            _root = new GameObject("Hold Gate");
            _root.transform.SetPositionAndRotation(gate.Position, gate.Rotation);

            // Light: the sun (baked Lambert) over a sky-tinted ambient, plus the lanterns' warm falloff.
            _sunLocal = Quaternion.Inverse(gate.Rotation) * sunDirection.normalized;
            _sunColor = new Vector3(sun.r, sun.g, sun.b) * 0.85f;
            _ambient = Vector3.Lerp(Vector3.one, new Vector3(sky.r, sky.g, sky.b), 0.35f) * 0.3f;
            _lanternColor = new Vector3(G.LanternR, G.LanternG, G.LanternB);

            var hw = G.Width * 0.5f;
            var dw = G.DoorWidth * 0.5f;
            var floor = math.max(1, G.Steps) * G.StepRise;   // the door's sill: the top step
            var top = floor + G.DoorHeight;
            var d = G.Depth;
            var lanternY = floor + G.DoorHeight * 0.55f;
            _lanterns = new[] { new Vector3(-dw - 1.3f, lanternY, 0.9f), new Vector3(dw + 1.3f, lanternY, 0.9f) };

            int Slice(string name, int fallback)
            {
                var layers = GameContent.Hold?.Textures?.Layers;
                var i = layers?.IndexOf("Surfaces/" + name) ?? -1;
                return i >= 0 ? i : fallback;
            }
            var brick = Slice("dlv_stonebrk1a", 0);
            var brick2 = Slice("dlv_stonebrk4a", 2);
            var trim = Slice("dlv_stonetrm1a", 9);
            var trim2 = Slice("dlv_stonetrm2a", 10);
            var rough = Slice("dlv_stonegen2", 8);
            var slate = Slice("dlv_slategen1", 6);
            var flagstone = Slice("dlv_stoneflr1a", 3);
            var metal = Slice("dlv_metaltrm1a", 19);

            // The rock the hold is cut into: rough tiers stepping back and up behind the facade, like the terraced land
            // round it. (All start behind the doorway's passage, so none shows inside it.)
            Solid(new Vector3(-hw - 15f, -12f, -d - 16f), new Vector3(hw + 15f, G.Height * 0.45f, -PassageDepth - 1f), rough);
            Solid(new Vector3(-hw - 11f, -12f, -d - 16f), new Vector3(hw + 11f, G.Height * 0.8f, -PassageDepth - 3f), slate);
            Solid(new Vector3(-hw - 7f, -12f, -d - 10f), new Vector3(hw + 7f, G.Height + 2.5f, -PassageDepth - 1f), rough);
            Solid(new Vector3(-hw - 3f, -12f, -d - 10f), new Vector3(hw + 3f, G.Height + 7f, -PassageDepth - 5f), slate);
            // The facade: piers either side of the door, the lintel over it, the wall above, a cornice and a plinth.
            Solid(new Vector3(-hw, -6f, -d), new Vector3(-dw, G.Height, 0f), brick);
            Solid(new Vector3(dw, -6f, -d), new Vector3(hw, G.Height, 0f), brick);
            Solid(new Vector3(-dw, top, -d), new Vector3(dw, G.Height, 0f), brick2);
            Solid(new Vector3(-hw - 0.4f, G.Height - 1f, -0.5f), new Vector3(hw + 0.4f, G.Height, 0.6f), trim2);
            Solid(new Vector3(-hw - 0.3f, -1f, -0.3f), new Vector3(-dw - 0.8f, floor + 0.6f, 0.4f), trim2);
            Solid(new Vector3(dw + 0.8f, -1f, -0.3f), new Vector3(hw + 0.3f, floor + 0.6f, 0.4f), trim2);
            // Door frame: jambs and a deep lintel stone, standing proud of the wall.
            Solid(new Vector3(-dw - 0.8f, floor, -0.2f), new Vector3(-dw, top + 0.2f, 0.5f), trim);
            Solid(new Vector3(dw, floor, -0.2f), new Vector3(dw + 0.8f, top + 0.2f, 0.5f), trim);
            Solid(new Vector3(-dw - 1.1f, top, -0.2f), new Vector3(dw + 1.1f, top + 1.2f, 0.7f), trim);
            // Pilasters at the facade's edges.
            Solid(new Vector3(-hw, -1f, 0f), new Vector3(-hw + 1.2f, G.Height - 1f, 0.45f), trim);
            Solid(new Vector3(hw - 1.2f, -1f, 0f), new Vector3(hw, G.Height - 1f, 0.45f), trim);
            // Steps up to the door: each a block from the facade out, the lowest the longest.
            for (var k = 1; k <= G.Steps; k++)
            {
                var reach = (G.Steps - k) * G.StepDepth + GatePlacement.Landing;
                var half = dw + 2.2f + (G.Steps - k) * 0.5f;
                Solid(new Vector3(-half, -1.5f, -0.01f), new Vector3(half, k * G.StepRise, reach), flagstone);
            }
            // Lantern brackets and their glowing cages.
            foreach (var l in _lanterns)
            {
                Solid(l + new Vector3(-0.12f, 0.35f, -0.9f), l + new Vector3(0.12f, 0.5f, 0f), metal);
                Glow(l + new Vector3(-0.22f, -0.3f, -0.22f), l + new Vector3(0.22f, 0.35f, 0.22f), metal);
            }
            // The doorway: a passage into the dark, darker the deeper it goes.
            // Inset a hair so its faces sit in front of the piers' and lintel's own.
            Passage(new Vector3(-dw + 0.02f, floor + 0.01f, -PassageDepth), new Vector3(dw - 0.02f, top - 0.02f, 0f), flagstone, brick, rough);

            var mesh = new Mesh { name = "HoldGate", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetColors(_c);
            mesh.SetUVs(0, _uv);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            _root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = _root.AddComponent<MeshRenderer>();
            material = new Material(kit);
            // Haze like the ground's: the planet's air.
            material.SetColor("_HazeColor", sky * 0.6f);
            material.SetFloat("_HazeDistance", hazeDistance);
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            foreach (var l in _lanterns)
            {
                var go = new GameObject("Gate Lantern");
                go.transform.SetParent(_root.transform, false);
                go.transform.localPosition = l + new Vector3(0f, 0f, 0.4f);
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(G.LanternR, G.LanternG, G.LanternB);
                light.intensity = G.LanternIntensity;
                light.range = G.LanternRange;
                light.shadows = LightShadows.None;
            }
            Debug.Log($"[Gate] built: {_v.Count} vertices, {_root.GetComponentsInChildren<BoxCollider>().Length} colliders");
            return _root;
        }

        /// <summary>How far the doorway's passage runs in before its dark back wall (metres).</summary>
        const float PassageDepth = 6f;

        /// <summary>A stone box with a collider.</summary>
        void Solid(Vector3 min, Vector3 max, int slice)
        {
            Box(min, max, slice, 1f);
            Collider(min, max);
        }

        /// <summary>A lantern cage: lit from inside (bright vertex colour), no collider.</summary>
        void Glow(Vector3 min, Vector3 max, int slice)
        {
            var start = _c.Count;
            Box(min, max, slice, 1f);
            for (var i = start; i < _c.Count; i++) _c[i] = new Color(_lanternColor.x * 2.6f, _lanternColor.y * 2.6f, _lanternColor.z * 2.6f, 1f);
        }

        void Collider(Vector3 min, Vector3 max)
        {
            var c = _root.AddComponent<BoxCollider>();
            c.center = (min + max) * 0.5f;
            c.size = max - min;
        }

        /// <summary>The doorway's passage: floor, walls, ceiling and a back wall facing in, fading to black inside.</summary>
        void Passage(Vector3 min, Vector3 max, int floor, int wall, int back)
        {
            float Dark(Vector3 p) => math.lerp(0.02f, 1f, math.pow(math.saturate(1f + p.z / 5f), 2f));   // 5 m in: black
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int slice)
            {
                Quad(a, b, c, d, n, slice, 1f);
                for (var i = _c.Count - 4; i < _c.Count; i++)
                {
                    var k = Dark(_v[i]);
                    _c[i] = new Color(_c[i].r * k, _c[i].g * k, _c[i].b * k, 1f);
                }
            }
            float x0 = min.x, x1 = max.x, y0 = min.y, y1 = max.y, z0 = min.z, z1 = max.z;
            Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), Vector3.up, floor);
            Face(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), Vector3.down, wall);
            Face(new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), Vector3.right, wall);
            Face(new(x1, y0, z1), new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), Vector3.left, wall);
            Face(new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), Vector3.forward, back);
            Collider(new Vector3(x0, y0 - 1f, z0), new Vector3(x1, y0, z1));          // the floor you walk in on
            Collider(new Vector3(x0, y0, z0 - 1f), new Vector3(x1, y1, z0));          // the dark at the back
        }

        /// <summary>Six outward faces (only those not buried: the bottom is skipped).</summary>
        void Box(Vector3 min, Vector3 max, int slice, float light)
        {
            float x0 = min.x, x1 = max.x, y0 = min.y, y1 = max.y, z0 = min.z, z1 = max.z;
            Quad(new(x0, y1, z0), new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), Vector3.up, slice, light);
            Quad(new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0), new(x0, y0, z0), Vector3.left, slice, light);
            Quad(new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1), Vector3.right, slice, light);
            Quad(new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0), new(x1, y0, z0), Vector3.back, slice, light);
            Quad(new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1), new(x0, y0, z1), Vector3.forward, slice, light);
        }

        /// <summary>a-b-c-d clockwise seen from the front (normal n). UVs world-aligned, one tile per 2 m (a hold cell).</summary>
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, int slice, float light)
        {
            var i = _v.Count;
            foreach (var p in new[] { a, b, c, d })
            {
                _v.Add(p);
                _n.Add(n);
                var uv = math.abs(n.y) > 0.5f ? new Vector2(p.x, p.z) : math.abs(n.x) > 0.5f ? new Vector2(p.z * -n.x, p.y) : new Vector2(p.x * n.z, p.y);
                _uv.Add(new Vector3(uv.x * 0.5f, uv.y * 0.5f, slice));
                var l = Bake(p, n) * light;
                _c.Add(new Color(l.x, l.y, l.z, 1f));
            }
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        Vector3 Bake(Vector3 p, Vector3 n)
        {
            var l = _ambient * (0.75f + 0.25f * n.y) + _sunColor * math.max(0f, Vector3.Dot(n, _sunLocal));
            foreach (var lantern in _lanterns)
            {
                var to = lantern - p;
                var fall = math.saturate(1f - to.magnitude / math.max(1f, G.LanternRange * 0.8f));
                var facing = math.saturate(Vector3.Dot(n, to.normalized) * 0.7f + 0.3f);
                l += _lanternColor * (fall * fall * facing * 0.9f);
            }
            return l;
        }
    }
}
