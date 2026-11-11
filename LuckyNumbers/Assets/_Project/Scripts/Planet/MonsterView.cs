using System;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>Something your shots and swings can hurt: bolts (WeaponView, ShipGuns, Bolts) and blades (MeleeView) call it.</summary>
    public interface IDamageable
    {
        /// <summary>`source`: what hit it ("ship", "melee", an item id, "default").</summary>
        void Hit(string source, Vector3 point, Vector3 direction);
    }

    /// <summary>
    /// A monster's body: a placeholder block (named PLACEHOLDER_monster_*, replace with a model) with two eyes on its
    /// front. CombatPresenter moves it and rolls its fights; hits on its collider come back through `Damaged`.
    /// </summary>
    public sealed class MonsterView : MonoBehaviour, IDamageable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int ColorA = Shader.PropertyToID("_ColorA");   // the relic shader's two tints
        static readonly int ColorB = Shader.PropertyToID("_ColorB");

        public event Action<MonsterView, string, Vector3> Damaged;
        Renderer _body;
        Color _color;
        MaterialPropertyBlock _block;
        float _flash;

        /// <summary>`material`: one that's in the build (a primitive's default is stripped and renders magenta).</summary>
        public static MonsterView Create(string id, Vector3 size, Color color, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // keeps its BoxCollider: bolts hit it
            go.name = $"PLACEHOLDER_monster_{id}";
            go.transform.localScale = size;
            var view = go.AddComponent<MonsterView>();
            view._body = go.GetComponent<Renderer>();
            if (material != null) view._body.sharedMaterial = material;
            view._body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            view._color = color;
            view._block = new MaterialPropertyBlock();
            view.Tint(color);
            // Eyes: two small dark blocks on its front face (local space: the cube is 1 x 1 x 1).
            foreach (var x in new[] { -0.22f, 0.22f })
            {
                var eye = GameObject.CreatePrimitive(PrimitiveType.Cube);
                eye.name = "Eye";
                Destroy(eye.GetComponent<Collider>());
                eye.transform.SetParent(go.transform, false);
                eye.transform.localPosition = new Vector3(x, 0.22f, 0.5f);
                eye.transform.localScale = new Vector3(0.16f, 0.16f, 0.06f);
                var r = eye.GetComponent<Renderer>();
                if (material != null) r.sharedMaterial = material;
                var b = new MaterialPropertyBlock();
                Set(b, new Color(1.6f, 0.15f, 0.1f));
                r.SetPropertyBlock(b);
            }
            return view;
        }

        public void Hit(string source, Vector3 point, Vector3 direction) => Damaged?.Invoke(this, source, point);

        /// <summary>A plain tinted placeholder block (no collider, nothing to hit): encounter finds.</summary>
        public static Transform Block(string name, Vector3 size, Color color, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            if (material != null) r.sharedMaterial = material;
            var b = new MaterialPropertyBlock();
            Set(b, color);
            r.SetPropertyBlock(b);
            return go.transform;
        }

        /// <summary>Flash white for a moment (a hit).</summary>
        public void Flash() => _flash = 0.15f;

        void Update()
        {
            if (_flash <= 0f) return;
            _flash -= Time.deltaTime;
            Tint(_flash > 0f ? Color.Lerp(_color, Color.white * 1.5f, _flash / 0.15f) : _color);
        }

        void Tint(Color c)
        {
            Set(_block, c);
            _body.SetPropertyBlock(_block);
        }

        static void Set(MaterialPropertyBlock b, Color c)
        {
            b.SetColor(BaseColor, c);
            b.SetColor(ColorId, c);
            b.SetColor(ColorA, c);
            b.SetColor(ColorB, c * 0.7f);
        }
    }
}
