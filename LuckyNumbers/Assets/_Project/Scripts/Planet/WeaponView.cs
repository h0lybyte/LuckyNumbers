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
    public sealed class WeaponView : MonoBehaviour, IBoltSink
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

        static readonly int ColorA = Shader.PropertyToID("_ColorA");
        static readonly int ColorB = Shader.PropertyToID("_ColorB");
        /// <summary>
        /// The bolts of every gun with the same material, in one field that ticks itself (most guns just lie there:
        /// none gets its own). Made on the first shot, again after a scene change took the old one. A bolt keeps
        /// flying if its gun goes away; its hit just isn't reported.
        /// </summary>
        static readonly Dictionary<Material, BoltField> Fields = new();
        static BoltField _unmaterialed;

        static BoltField FieldFor(Material m)
        {
            var have = m != null ? Fields.TryGetValue(m, out var f) ? f : null : _unmaterialed;
            if (have is { Alive: true }) return have;
            have = new BoltField(m, "Bolt", ColorA, ColorB, prewarm: 6, selfTick: true);
            if (m != null) Fields[m] = have; else _unmaterialed = have;
            return have;
        }

        /// <summary>Any gun fired (the station's range counts shots).</summary>
        public static event Action<WeaponView> Fired;
        /// <summary>Any gun's bolt hit something (the station's range counts hits on its targets).</summary>
        public static event Action<WeaponView, RaycastHit> BoltHit;
        BoltField _field;
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
            if (_field is not { Alive: true }) _field = FieldFor(boltMaterial);
            _field.Fire(new BoltShot
            {
                From = from, Direction = dir, Speed = boltSpeed, Range = range, Length = boltLength, Width = boltWidth, Color = boltColor,
                ImpactSize = 0.35f, ImpactLife = 0.18f, ImpactLift = 0.02f, Impulse = impulse,
                Source = Source, Ignore = transform, IgnoreAlso = _ignore, Sink = this,
            });
            _field.Flash(from, 0.09f, 0.06f, boltColor, gameObject.layer);   // muzzle flash with the gun (viewmodel when held)
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
            if (_flashLight != null) _flashLight.intensity = Mathf.MoveTowards(_flashLight.intensity, 0f, Time.deltaTime * 60f);
        }

        void IBoltSink.OnBoltHit(in BoltShot shot, in RaycastHit hit, IDamageable target, float travelled)
        {
            Debug.Log($"[Weapon] bolt hit {hit.collider.name} after {travelled:0.0} m");
            BoltHit?.Invoke(this, hit);
        }

        void OnDestroy()
        {
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
