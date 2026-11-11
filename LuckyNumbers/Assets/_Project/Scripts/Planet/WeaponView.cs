using System;
using System.Collections.Generic;
using LuckyNumbers.Core;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A gun you can pick up (its Rigidbody makes it carryable, like a pebble). Points are in this transform's space:
    /// +Z is down the barrel, +Y up. The scene builder measures them from the model; tune them here per weapon.
    /// FirstPersonPresenter holds it in view (right hand on the grip, left on the foregrip, IK) and fires it on click:
    /// energy bolts that fly from the muzzle to where you aim, nudging what they hit.
    /// </summary>
    public sealed class WeaponView : MonoBehaviour
    {
        [Header("Hold (local, +Z barrel)")]
        [SerializeField] Vector3 grip;        // where the right palm closes (middle of the pistol grip)
        [SerializeField] Vector3 foregrip;    // where the left palm cups (under the handguard)
        [SerializeField] Vector3 muzzle;      // where bolts leave
        [Tooltip("Where the grip sits in front of the eye (camera space, metres). Shoulders are at about (+-0.2, -0.07, 0) " +
                 "and reach 0.56 m: the foregrip (ahead of this) must stay within ~0.48 m of the left one.")]
        [SerializeField] Vector3 holdOffset = new(0.08f, -0.20f, 0.28f);
        [Tooltip("Scale while held (a viewmodel trick): these guns are long for the PSX arms' 0.56 m reach.")]
        [SerializeField] float heldScale = 0.75f;
        [Tooltip("How far the whole arms rig shifts while holding it (camera space): right brings the left shoulder to a gun held low and right.")]
        [SerializeField] Vector3 armsShift;
        [Tooltip("Which way each palm faces and the fingers point (local) before they curl: defaults are a pistol grip " +
                 "(palm on its left, knuckles forward and down) and a handguard (palm up, fingers over to the right).")]
        [SerializeField] Vector3 gripPalm = Vector3.left, gripAlong = new(0f, -0.5f, 1f), forePalm = Vector3.up, foreAlong = new(0.5f, 0f, 1f);

        [Header("Energy shots")]
        [SerializeField] Material boltMaterial;
        [SerializeField] Color boltColor = new(0.35f, 1.6f, 2.6f);   // HDR: blooms in the station
        [SerializeField] float boltSpeed = 70f;                      // m/s
        [SerializeField] float boltLength = 0.6f;
        [SerializeField] float boltWidth = 0.035f;
        [SerializeField] float range = 80f;
        [SerializeField] float cooldown = 0.16f;                     // seconds between shots (combat.txtpb's entry for this model wins)
        [SerializeField] float impulse = 3f;                         // push on what it hits (N s)
        [SerializeField] float recoil = 0.035f;                      // metres back per shot (pitch follows)

        public Vector3 Grip => grip;
        [Header("Baked grips (weapons.json, tools/ln.py bake-grasps)")]
        [SerializeField] LuckyNumbers.Hands.GraspData graspLeft;
        [SerializeField] LuckyNumbers.Hands.GraspData graspRight;
        public LuckyNumbers.Hands.GraspData BakedGrip(int side) => side == 0 ? graspLeft : graspRight;
        public void InitGrips(LuckyNumbers.Hands.GraspData left, LuckyNumbers.Hands.GraspData right) { graspLeft = left; graspRight = right; }

        public Vector3 Foregrip => foregrip;
        public Vector3 HoldOffset => holdOffset;
        public float HeldScale => heldScale;
        public Vector3 ArmsShift => armsShift;
        public Vector3 GripPalm => gripPalm;
        public Vector3 GripAlong => gripAlong;
        public Vector3 ForePalm => forePalm;
        public Vector3 ForeAlong => foreAlong;
        public Vector3 MuzzleWorld => transform.TransformPoint(muzzle);
        public float Recoil => recoil;
        /// <summary>
        /// Whose stats its hits roll with (content/combat.txtpb `weapons` source): "default" for a gun found lying
        /// around, the item's id for the equipped gun the hands draw (FirstPersonPresenter).
        /// </summary>
        public string Source { get; set; } = "default";
        /// <summary>Off cooldown: the next TryFire shoots.</summary>
        public bool Ready => Time.time >= _nextShot;

        struct Bolt { public Shape View; public Vector3 Position, Direction; public float Left; }

        static readonly int ColorA = Shader.PropertyToID("_ColorA");
        static readonly int ColorB = Shader.PropertyToID("_ColorB");
        /// <summary>
        /// Bolt and flash shapes shared by every gun with the same material (most guns just lie there: none gets its
        /// own pool). Built on the first shot, again after a scene change took the old ones.
        /// </summary>
        static readonly Dictionary<Material, (ShapePool capsules, ShapePool spheres)> Pools = new();
        static (ShapePool capsules, ShapePool spheres) _unmaterialed;

        static (ShapePool capsules, ShapePool spheres) PoolsFor(Material m)
        {
            var have = m != null ? Pools.TryGetValue(m, out var p) ? p : default : _unmaterialed;
            if (have.capsules is { Alive: true }) return have;
            have = (new ShapePool(PrimitiveType.Capsule, m, "Bolt", ColorA, ColorB, prewarm: 6),
                    new ShapePool(PrimitiveType.Sphere, m, "Bolt flash", ColorA, ColorB, prewarm: 6));
            if (m != null) Pools[m] = have; else _unmaterialed = have;
            return have;
        }

        /// <summary>Any gun fired (the station's range counts shots).</summary>
        public static event Action<WeaponView> Fired;
        /// <summary>Any gun's bolt hit something (the station's range counts hits on its targets).</summary>
        public static event Action<WeaponView, RaycastHit> BoltHit;
        readonly List<Bolt> _bolts = new();
        ShapePool _capsules;
        Flashes _flashes;
        readonly RaycastHit[] _hits = new RaycastHit[8];
        Transform _ignore;
        Light _flashLight;
        float _nextShot;

        /// <summary>Editor setup (SceneBuilder): measured hold points and the bolt's material.</summary>
        public void Init(Vector3 grip, Vector3 foregrip, Vector3 muzzle, Material bolt)
        {
            this.grip = grip;
            this.foregrip = foregrip;
            this.muzzle = muzzle;
            boltMaterial = bolt;
        }

        /// <summary>Editor setup with a hold fitted to the arms (tools/blender/weapon_holds.py).</summary>
        public void Init(Vector3 grip, Vector3 foregrip, Vector3 muzzle, Material bolt, Vector3 holdOffset, float heldScale, Vector3 armsShift)
        {
            Init(grip, foregrip, muzzle, bolt);
            this.holdOffset = holdOffset;
            this.heldScale = heldScale;
            this.armsShift = armsShift;
        }

        /// <summary>The hands' directions too (hand-placed sockets, tools/blender/weapon_sockets.json).</summary>
        public void InitHands(Vector3 gripPalm, Vector3 gripAlong, Vector3 forePalm, Vector3 foreAlong)
        {
            this.gripPalm = gripPalm;
            this.gripAlong = gripAlong;
            this.forePalm = forePalm;
            this.foreAlong = foreAlong;
        }

        /// <summary>Fire toward a world point if the gun is ready; bolts skip colliders under `ignore` (the shooter).</summary>
        public bool TryFire(Vector3 target, Transform ignore)
        {
            if (Time.time < _nextShot) return false;
            var rate = CombatWeapons.Held(this).Cooldown;
            _nextShot = Time.time + (rate > 0f ? rate : cooldown);
            _ignore = ignore;
            var from = MuzzleWorld;
            var dir = target - from;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            if (_capsules is not { Alive: true })
            {
                var (capsules, spheres) = PoolsFor(boltMaterial);
                _capsules = capsules;
                _flashes?.Clear();
                _flashes = new Flashes(spheres, ownsPool: false);
            }
            var view = _capsules.Get(from, Quaternion.FromToRotation(Vector3.up, dir), new Vector3(boltWidth, boltLength * 0.5f, boltWidth), boltColor);
            _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Left = range });
            _flashes.Add(from, 0.09f, 0.06f, boltColor, gameObject.layer);   // muzzle flash with the gun (viewmodel when held)
            if (_flashLight == null)
            {
                _flashLight = new GameObject("MuzzleLight").AddComponent<Light>();
                _flashLight.type = LightType.Point;
                _flashLight.range = 5f;
                _flashLight.color = boltColor / Mathf.Max(0.01f, boltColor.maxColorComponent);
            }
            _flashLight.transform.position = from;
            _flashLight.intensity = 4f;
            Debug.Log($"[Weapon] {name} fired");
            Fired?.Invoke(this);
            return true;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            for (var i = _bolts.Count - 1; i >= 0; i--)
            {
                var b = _bolts[i];
                var step = Mathf.Min(boltSpeed * dt, b.Left);
                if (Cast(b.Position, b.Direction, step, out var hit))
                {
                    hit.collider.GetComponentInParent<IDamageable>()?.Hit(Source, hit.point, b.Direction);
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        hit.rigidbody.AddForceAtPosition(b.Direction * impulse, hit.point, ForceMode.Impulse);
                    _flashes.Add(hit.point + hit.normal * 0.02f, 0.35f, 0.18f, boltColor);
                    Debug.Log($"[Weapon] bolt hit {hit.collider.name} after {range - b.Left + hit.distance:0.0} m");
                    BoltHit?.Invoke(this, hit);
                    Remove(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                // The bolt's tail trails its head, so it never pokes through what it is about to hit.
                b.View.Transform.position = b.Position - b.Direction * (boltLength * 0.5f);
                if (b.Left <= 0f) { Remove(i); continue; }
                _bolts[i] = b;
            }
            _flashes?.Tick(dt);
            if (_flashLight != null) _flashLight.intensity = Mathf.MoveTowards(_flashLight.intensity, 0f, dt * 60f);
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
                if (h.collider.transform.IsChildOf(transform)) continue;
                if (_ignore != null && h.collider.transform.IsChildOf(_ignore)) continue;
                if (h.distance < best.distance) { best = h; found = true; }
            }
            return found;
        }

        /// <summary>Back to the pool; swap-remove (the last bolt, already ticked this frame, takes its slot).</summary>
        void Remove(int i)
        {
            _capsules.Release(_bolts[i].View);
            _bolts[i] = _bolts[^1];
            _bolts.RemoveAt(_bolts.Count - 1);
        }

        void OnDestroy()
        {
            foreach (var b in _bolts) _capsules?.Release(b.View);   // the shapes are shared: back to the pool
            _bolts.Clear();
            _flashes?.Clear();
            if (_flashLight != null) Destroy(_flashLight.gameObject);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green; Gizmos.DrawWireSphere(transform.TransformPoint(grip), 0.02f);
            Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.TransformPoint(foregrip), 0.02f);
            Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(MuzzleWorld, 0.015f);
        }
    }
}
