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
    ///
    /// A model whose rig carries its clips baked to textures (MonsterRig.Vat) is drawn by <see cref="MonsterCrowd"/>,
    /// GPU-instanced with every other: this keeps only its GameObject and collider, and tells the crowd what to play.
    /// Without a bake (or with ?ln-skinned in the page's URL / -lnSkinned on the command line, for A/B tests, or in
    /// edit mode: previews) it's a copy of the rig, skinned and animated by its Animator as before.
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
        // Or a VAT model: drawn by the crowd, which plays its clips.
        MonsterVat _vat;
        MonsterCrowd.Member _member;
        internal MonsterCrowd.Member CrowdMember => _member;
        // Its blob shadow (MonsterBlobs), on the ground it's told of (Grounded).
        MonsterBlobs.Member _blob;

        public bool Modelled => _rig != null || _member != null;
        /// <summary>Mid-stagger (a hit's clip): it doesn't walk.</summary>
        public bool Busy => Modelled && Time.time < _busyUntil;

        static bool? _skinnedOnly;
        /// <summary>?ln-skinned (Web) / -lnSkinned: every model on the Animator path, to compare against the crowd.</summary>
        static bool SkinnedOnly => _skinnedOnly ??= Application.absoluteURL.Contains("ln-skinned") ||
                                                    Array.IndexOf(Environment.GetCommandLineArgs(), "-lnSkinned") >= 0;

        /// <summary>
        /// A monster from a model template (MonsterRig, kept off): a copy, its feet on the bottom of the `size` box
        /// (its collider), facing its root's forward.
        /// </summary>
        public static MonsterView CreateModel(string id, MonsterRig template, Vector3 size, LuckyNumbers.Model.MonsterClips clips)
        {
            var go = new GameObject($"Monster_{id}");
            var box = go.AddComponent<BoxCollider>();   // bolts hit it
            box.size = size;
            var view = go.AddComponent<MonsterView>();
            view._clips = clips ?? new LuckyNumbers.Model.MonsterClips();
            var vat = template.Vat;
            if (Application.isPlaying && !SkinnedOnly && vat != null && VatCovers(vat, template, view._clips, id))
            {
                view._vat = vat;
                view._member = MonsterCrowd.Current.Add(view, vat, -size.y * 0.5f);
                if (Said.Add(id)) Debug.Log($"[Monster] {id}: instanced (VAT {vat.name}: {vat.Clips.Length} clips, {vat.BoneCount} bones)");
            }
            else
            {
                var rig = Instantiate(template, go.transform, false);
                rig.gameObject.SetActive(true);
                rig.transform.localPosition = new Vector3(0f, -size.y * 0.5f, 0f);
                view._rig = rig;
                view._skins = rig.GetComponentsInChildren<Renderer>(true);
                view._block = new MaterialPropertyBlock();
                if (Application.isPlaying && Said.Add(id)) Debug.Log($"[Monster] {id}: skinned{(SkinnedOnly ? " (ln-skinned)" : vat == null ? " (no VAT bake)" : "")}");
            }
            view.Move(0f, false);
            view.AddBlob(size);
            return view;
        }

        static readonly System.Collections.Generic.HashSet<string> Warned = new(), Said = new();

        /// <summary>The bake can draw this device and has every clip `clips` asks of the rig; else the Animator path, said once.</summary>
        static bool VatCovers(MonsterVat vat, MonsterRig rig, LuckyNumbers.Model.MonsterClips clips, string id)
        {
            string missing = null;
            if (!vat.Usable) missing = "this device can't draw it";
            else
                foreach (var c in new[] { clips.Idle, clips.Walk, clips.Run, clips.Attack, clips.Hit, clips.Die })
                    if (rig.Has(c) && !vat.Has(c)) { missing = $"clip '{c}' isn't baked (tools/ln.py scenes)"; break; }
            if (missing == null) return true;
            if (Warned.Add(id)) Debug.LogWarning($"[Monster] {id}: VAT {vat.name}: {missing}; skinned instead");
            return false;
        }

        bool Has(string clip) => _vat != null ? _vat.Has(clip) : _rig != null && _rig.Has(clip);
        float Length(string clip) => _vat != null ? _vat.Length(clip) : _rig != null ? _rig.Length(clip) : 0f;
        float Stride(string clip) => _vat != null ? _vat.Stride(clip) : _rig != null ? _rig.Stride(clip) : 0f;

        void Play(string clip, float rate, bool once, float fade)
        {
            if (_member != null) _member.Crowd.Play(_member, _vat.Index(clip), rate, once, fade);
            else if (_rig != null) _rig.Play(clip, rate, once, fade);
        }

        /// <summary>Walking at `speed` m/s (0: standing), chasing or not: the matching loop, at the stride's rate.</summary>
        public void Move(float speed, bool chasing)
        {
            if (!Modelled || _dead || Time.time < _oneShotUntil) return;
            var clip = speed < 0.05f ? _clips.Idle : chasing && Has(_clips.Run) ? _clips.Run : _clips.Walk;
            if (!Has(clip)) clip = _clips.Idle;
            var stride = Stride(clip);
            var rate = stride > 0.01f ? Mathf.Clamp(speed / stride, 0.4f, 3.2f) : 1f;
            Play(clip, rate, false, 0.25f);
        }

        /// <summary>Its attack (the clip once, then back to walking).</summary>
        public void Attack() => OneShot(_clips?.Attack, 0f);

        /// <summary>Hit and still up: a stagger, standing still a moment.</summary>
        public void Staggered() => OneShot(_clips?.Hit, 0.6f);

        void OneShot(string clip, float busy)
        {
            if (!Modelled || _dead || !Has(clip)) return;
            Play(clip, 1f, true, 0.12f);
            _oneShotUntil = Time.time + Mathf.Max(0.1f, Length(clip) - 0.25f);   // hand back while it settles
            _busyUntil = Time.time + Mathf.Min(busy, Length(clip));
        }

        /// <summary>Killed: its fall, held. Returns how long the fall lasts (0: no clip).</summary>
        public float Die()
        {
            if (!Modelled || _dead) return 0f;
            _dead = true;
            if (!Has(_clips.Die)) return 0f;
            Play(_clips.Die, 1f, true, 0.1f);
            return Length(_clips.Die);
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
            view.AddBlob(size);
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

        void AddBlob(Vector3 size)
        {
            if (Application.isPlaying) _blob = MonsterBlobs.Current.Add(transform, size);
        }

        /// <summary>The ground's height under it, where its blob shadow lies (whoever moves it says, each time).</summary>
        public void Grounded(float groundY) => _blob?.Blobs.SetGround(_blob, groundY);

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
        public void Flash()
        {
            if (_member != null) _member.Crowd.Flash(_member, MonsterCrowd.FlashTime);   // the crowd fades it
            else _flash = MonsterCrowd.FlashTime;
        }

        void OnEnable()
        {
            _member?.Crowd.SetHidden(_member, false);
            _blob?.Blobs.SetHidden(_blob, false);
        }

        void OnDisable()
        {
            _member?.Crowd.SetHidden(_member, true);
            _blob?.Blobs.SetHidden(_blob, true);
        }

        void OnDestroy()
        {
            _member?.Crowd.Remove(_member);
            _blob?.Blobs.Remove(_blob);
        }

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
