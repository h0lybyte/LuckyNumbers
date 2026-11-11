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
    /// A monster's body: its model (MonsterType.model: a MonsterRig template, animated by `clips`) or a placeholder
    /// block (named PLACEHOLDER_monster_*) with two eyes on its front. Either way the root sits at the middle of its
    /// `size` box, which is its collider. CombatPresenter moves it and rolls its fights; hits come back through `Damaged`.
    /// </summary>
    public sealed class MonsterView : MonoBehaviour, IDamageable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int ColorA = Shader.PropertyToID("_ColorA");   // the relic shader's two tints
        static readonly int ColorB = Shader.PropertyToID("_ColorB");

        static readonly int Heat = Shader.PropertyToID("_Heat");     // the Ship shader's glow: a model's hit flash

        public event Action<MonsterView, string, Vector3> Damaged;
        Renderer _body;
        Color _color;
        MaterialPropertyBlock _block;
        float _flash;
        // A model: its rig, clips, and a one-shot (attack, hit) playing until then, holding still until `_busyUntil`.
        MonsterRig _rig;
        Renderer[] _skins;
        LuckyNumbers.Model.MonsterClips _clips;
        float _oneShotUntil, _busyUntil;
        bool _dead;

        public bool Modelled => _rig != null;
        /// <summary>Mid-stagger (a hit's clip): it doesn't walk.</summary>
        public bool Busy => _rig != null && Time.time < _busyUntil;

        /// <summary>
        /// A monster from a model template (MonsterRig, kept off): a copy, its feet on the bottom of the `size` box
        /// (its collider), facing its root's forward.
        /// </summary>
        public static MonsterView CreateModel(string id, MonsterRig template, Vector3 size, LuckyNumbers.Model.MonsterClips clips)
        {
            var go = new GameObject($"Monster_{id}");
            var box = go.AddComponent<BoxCollider>();   // bolts hit it
            box.size = size;
            var rig = Instantiate(template, go.transform, false);
            rig.gameObject.SetActive(true);
            rig.transform.localPosition = new Vector3(0f, -size.y * 0.5f, 0f);
            var view = go.AddComponent<MonsterView>();
            view._rig = rig;
            view._clips = clips ?? new LuckyNumbers.Model.MonsterClips();
            view._skins = rig.GetComponentsInChildren<Renderer>(true);
            view._block = new MaterialPropertyBlock();
            view.Move(0f, false);
            return view;
        }

        /// <summary>Walking at `speed` m/s (0: standing), chasing or not: the matching loop, at the stride's rate.</summary>
        public void Move(float speed, bool chasing)
        {
            if (_rig == null || _dead || Time.time < _oneShotUntil) return;
            var clip = speed < 0.05f ? _clips.Idle : chasing && _rig.Has(_clips.Run) ? _clips.Run : _clips.Walk;
            if (!_rig.Has(clip)) clip = _clips.Idle;
            var stride = _rig.Stride(clip);
            var rate = stride > 0.01f ? Mathf.Clamp(speed / stride, 0.4f, 3.2f) : 1f;
            _rig.Play(clip, rate, false, 0.25f);
        }

        /// <summary>Its attack (the clip once, then back to walking).</summary>
        public void Attack() => OneShot(_clips?.Attack, 0f);

        /// <summary>Hit and still up: a stagger, standing still a moment.</summary>
        public void Staggered() => OneShot(_clips?.Hit, 0.6f);

        void OneShot(string clip, float busy)
        {
            if (_rig == null || _dead || !_rig.Has(clip)) return;
            _rig.Play(clip, 1f, true, 0.12f);
            _oneShotUntil = Time.time + Mathf.Max(0.1f, _rig.Length(clip) - 0.25f);   // hand back while it settles
            _busyUntil = Time.time + Mathf.Min(busy, _rig.Length(clip));
        }

        /// <summary>Killed: its fall, held. Returns how long the fall lasts (0: no clip).</summary>
        public float Die()
        {
            if (_rig == null || _dead) return 0f;
            _dead = true;
            if (!_rig.Has(_clips.Die)) return 0f;
            _rig.Play(_clips.Die, 1f, true, 0.1f);
            return _rig.Length(_clips.Die);
        }

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
            if (_rig != null)
            {
                _block.SetFloat(Heat, Mathf.Max(0f, _flash / 0.15f));
                foreach (var r in _skins) r.SetPropertyBlock(_block);
                return;
            }
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
