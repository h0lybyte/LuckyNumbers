using System.Collections.Generic;
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
        [SerializeField] float cooldown = 0.16f;                     // seconds between shots
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

        sealed class Bolt { public Transform View; public Vector3 Position, Direction; public float Left; }
        readonly List<Bolt> _bolts = new();
        readonly List<(Transform view, float age, float life, float size)> _flashes = new();
        readonly RaycastHit[] _hits = new RaycastHit[8];
        Transform _ignore;
        Light _flashLight;
        MaterialPropertyBlock _block;
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
            _nextShot = Time.time + cooldown;
            _ignore = ignore;
            var from = MuzzleWorld;
            var dir = target - from;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
            var view = Shape(PrimitiveType.Capsule, from, new Vector3(boltWidth, boltLength * 0.5f, boltWidth), 0);
            view.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            _bolts.Add(new Bolt { View = view, Position = from, Direction = dir, Left = range });
            Flash(from, 0.09f, 0.06f, gameObject.layer);   // muzzle flash with the gun (viewmodel when held)
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
                    if (hit.rigidbody != null && !hit.rigidbody.isKinematic)
                        hit.rigidbody.AddForceAtPosition(b.Direction * impulse, hit.point, ForceMode.Impulse);
                    Flash(hit.point + hit.normal * 0.02f, 0.35f, 0.18f, 0);
                    Debug.Log($"[Weapon] bolt hit {hit.collider.name} after {range - b.Left + hit.distance:0.0} m");
                    Destroy(b.View.gameObject);
                    _bolts.RemoveAt(i);
                    continue;
                }
                b.Position += b.Direction * step;
                b.Left -= step;
                // The bolt's tail trails its head, so it never pokes through what it is about to hit.
                b.View.position = b.Position - b.Direction * (boltLength * 0.5f);
                if (b.Left <= 0f) { Destroy(b.View.gameObject); _bolts.RemoveAt(i); }
            }
            for (var i = _flashes.Count - 1; i >= 0; i--)
            {
                var (view, age, life, size) = _flashes[i];
                age += dt;
                if (age >= life) { Destroy(view.gameObject); _flashes.RemoveAt(i); continue; }
                view.localScale = Vector3.one * (size * Mathf.Lerp(0.4f, 1f, age / life));
                _flashes[i] = (view, age, life, size);
            }
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

        void Flash(Vector3 at, float size, float life, int layer)
        {
            var view = Shape(PrimitiveType.Sphere, at, Vector3.one * size * 0.4f, layer);
            _flashes.Add((view, 0f, life, size));
        }

        Transform Shape(PrimitiveType type, Vector3 at, Vector3 scale, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Bolt";
            go.layer = layer;
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (boltMaterial != null) r.sharedMaterial = boltMaterial;
            _block ??= new MaterialPropertyBlock();
            _block.SetColor("_ColorA", boltColor);
            _block.SetColor("_ColorB", boltColor);
            r.SetPropertyBlock(_block);
            go.transform.SetPositionAndRotation(at, Quaternion.identity);
            go.transform.localScale = scale;
            return go.transform;
        }

        void OnDestroy()
        {
            foreach (var b in _bolts) if (b.View != null) Destroy(b.View.gameObject);
            foreach (var f in _flashes) if (f.view != null) Destroy(f.view.gameObject);
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
