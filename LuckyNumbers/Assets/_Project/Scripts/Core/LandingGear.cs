using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// A ship's landing legs (content/engines.txtpb `legs`): one strut each under the pivot, in its model units, drawn
    /// by LuckyNumbers/LandingLeg, which telescopes it from _Extend (one value for the set, eased here). Down fits each
    /// leg to reach the ground a given drop below the pivot (along the pivot's down: landed on a slope, the ship is
    /// tilted with it); Up tucks them away. Tucked all the way the renderers switch off.
    /// </summary>
    public sealed class LandingGear
    {
        static readonly int Extend = Shader.PropertyToID("_Extend");
        static Mesh _mesh;

        sealed class Leg
        {
            public LandingLeg Def;
            public Transform Transform;
            public Renderer Renderer;
            public float Cos;   // of its splay: a leaning leg is longer for the same drop
            public bool Fits;   // long enough to show at this drop
        }

        readonly List<Leg> _legs = new();
        readonly MaterialPropertyBlock _block = new();
        float _value, _target;

        /// <summary>How far out, 0 (tucked away) .. 1 (on the ground).</summary>
        public float Value => _value;

        LandingGear() { }

        /// <summary>The legs under <paramref name="pivot"/>; null when there are none (or no material).</summary>
        public static LandingGear Build(Transform pivot, IList<LandingLeg> legs, Material material)
        {
            if (legs == null || legs.Count == 0 || material == null) return null;
            var gear = new LandingGear();
            var root = new GameObject("Landing legs").transform;
            root.SetParent(pivot, false);
            foreach (var def in legs)
            {
                var top = def.Position.ToVector3();
                var splay = Mathf.Abs(top.x) < 1e-4f ? 0f : def.SplayDegrees * Mathf.Sign(top.x);   // out to its own side
                var go = new GameObject($"Leg {def.Name}");
                go.transform.SetParent(root, false);
                go.transform.localPosition = top;
                go.transform.localRotation = Quaternion.Euler(0f, 0f, splay);   // +Z turn swings the foot toward +X
                go.AddComponent<MeshFilter>().sharedMesh = Mesh();
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.enabled = false;
                gear._legs.Add(new Leg { Def = def, Transform = go.transform, Renderer = r, Cos = Mathf.Cos(splay * Mathf.Deg2Rad) });
            }
            gear.Apply();
            return gear;
        }

        /// <summary>Out to the ground <paramref name="drop"/> below the pivot (model units, along its down).</summary>
        public void Down(float drop, bool instant = false)
        {
            foreach (var leg in _legs)
            {
                var r = Mathf.Max(1e-4f, leg.Def.Radius);
                var length = (drop + leg.Def.Position.Y) / Mathf.Max(0.2f, leg.Cos);   // from where it leaves the hull
                leg.Fits = length > r * 3f;   // the ground's (nearly) at the hull: nothing to stand on
                if (leg.Fits) leg.Transform.localScale = new Vector3(r, length, r);
            }
            _target = 1f;
            if (instant) _value = 1f;
            Apply();
        }

        public void Up(bool instant = false)
        {
            _target = 0f;
            if (instant) _value = 0f;
            Apply();
        }

        /// <summary>Ease toward Down / Up over <paramref name="seconds"/> (the whole travel).</summary>
        public void Step(float dt, float seconds)
        {
            if (Mathf.Approximately(_value, _target)) return;
            _value = Mathf.MoveTowards(_value, _target, dt / Mathf.Max(0.01f, seconds));
            Apply();
        }

        /// <summary>Jump to <paramref name="value"/> (0..1) now, no easing (previews).</summary>
        public void Snap(float value)
        {
            _value = _target = Mathf.Clamp01(value);
            Apply();
        }

        void Apply()
        {
            foreach (var leg in _legs)
            {
                var on = leg.Fits && _value > 0.001f;
                if (leg.Renderer.enabled != on) leg.Renderer.enabled = on;
                if (!on) continue;
                leg.Renderer.GetPropertyBlock(_block);
                _block.SetFloat(Extend, _value);
                leg.Renderer.SetPropertyBlock(_block);
            }
        }

        /// <summary>
        /// The strut mesh (shared): three tubes the shader places and sizes, uv = (which: 0 housing, 1 piston, 2 foot
        /// pad; 0..1 along it). Vertices are unit circles in xz (the shader sets y); the pad gets a centre vertex per
        /// end (xz 0) for its caps. Normals point out (caps up / down); the shader draws both sides.
        /// </summary>
        public static Mesh Mesh()
        {
            if (_mesh != null) return _mesh;
            const int sides = 12;
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uv = new List<Vector2>();
            var tris = new List<int>();
            for (var which = 0; which < 3; which++)
            {
                var start = verts.Count;
                foreach (var t in new[] { 0f, 1f })
                for (var s = 0; s <= sides; s++)
                {
                    var a = s / (float)sides * Mathf.PI * 2f;
                    var p = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    verts.Add(p);
                    normals.Add(p);
                    uv.Add(new Vector2(which, t));
                }
                for (var s = 0; s < sides; s++)
                {
                    int a = start + s, b = a + sides + 1;
                    tris.AddRange(new[] { a, a + 1, b, a + 1, b + 1, b });
                }
                if (which != 2) continue;
                foreach (var (t, n) in new[] { (0f, Vector3.up), (1f, Vector3.down) })
                {
                    var ring = start + (t > 0.5f ? sides + 1 : 0);
                    var centre = verts.Count;
                    var capRing = verts.Count + 1;
                    verts.Add(Vector3.zero);
                    normals.Add(n);
                    uv.Add(new Vector2(which, t));
                    for (var s = 0; s <= sides; s++)   // the rim again, facing the cap's way (a hard edge)
                    {
                        verts.Add(verts[ring + s]);
                        normals.Add(n);
                        uv.Add(new Vector2(which, t));
                    }
                    for (var s = 0; s < sides; s++) tris.AddRange(new[] { centre, capRing + s, capRing + s + 1 });
                }
            }
            _mesh = new Mesh { name = "Landing leg" };
            _mesh.SetVertices(verts);
            _mesh.SetNormals(normals);
            _mesh.SetUVs(0, uv);
            _mesh.SetTriangles(tris, 0);
            // The shader places it: down to y -1 (the full length), the pad up to 4x the housing's radius.
            _mesh.bounds = new Bounds(new Vector3(0f, -0.5f, 0f), new Vector3(8f, 1.2f, 8f));
            return _mesh;
        }
    }
}
