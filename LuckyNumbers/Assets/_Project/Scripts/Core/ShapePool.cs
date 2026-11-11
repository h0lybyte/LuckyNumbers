using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>One pooled primitive: its transform and renderer, so a reuse needs no GetComponent.</summary>
    public readonly struct Shape
    {
        public readonly Transform Transform;
        public readonly MeshRenderer Renderer;
        public Shape(Transform transform, MeshRenderer renderer) { Transform = transform; Renderer = renderer; }
        public bool Alive => Transform != null;   // Unity null: destroyed with its scene
    }

    /// <summary>
    /// Reused primitive renderers (bolts, muzzle flashes): no collider, no shadows, one material, tinted per Get with
    /// a property block. Creating and destroying a GameObject per shot hitches the web build (GC + native churn);
    /// here a released shape only has its renderer turned off and waits under the pool's root for the next Get.
    /// The root lives in the active scene, so a scene change takes everything with it (Get skips the dead).
    /// </summary>
    public sealed class ShapePool : IDisposable
    {
        readonly PrimitiveType _type;
        readonly Material _material;
        readonly string _name;
        readonly int _colorA, _colorB;
        readonly Stack<Shape> _free = new();
        readonly MaterialPropertyBlock _block = new();
        Transform _root;

        /// <param name="colorA">Shader property the tint goes to (and colorB, if the shader has a second).</param>
        public ShapePool(PrimitiveType type, Material material, string name, int colorA, int colorB, int prewarm = 0)
        {
            _type = type;
            _material = material;
            _name = name;
            _colorA = colorA;
            _colorB = colorB;
            _root = new GameObject($"[Pool] {name}").transform;
            for (var i = 0; i < prewarm; i++) _free.Push(Create(false));
        }

        /// <summary>Alive: the scene that made this pool is still loaded (a static pool rebuilds when it isn't).</summary>
        public bool Alive => _root != null;

        /// <summary>A shape at `at`, turned and scaled, tinted `color`, on `layer`.</summary>
        public Shape Get(Vector3 at, Quaternion rotation, Vector3 scale, Color color, int layer = 0)
        {
            Shape s;
            do s = _free.Count > 0 ? _free.Pop() : Create(true);
            while (!s.Alive);
            var t = s.Transform;
            t.SetPositionAndRotation(at, rotation);
            t.localScale = scale;
            if (t.gameObject.layer != layer) t.gameObject.layer = layer;
            _block.SetColor(_colorA, color);
            if (_colorB != _colorA) _block.SetColor(_colorB, color);
            s.Renderer.SetPropertyBlock(_block);
            s.Renderer.enabled = true;
            return s;
        }

        public void Release(Shape s)
        {
            if (!s.Alive) return;
            s.Renderer.enabled = false;
            _free.Push(s);
        }

        Shape Create(bool on)
        {
            var go = GameObject.CreatePrimitive(_type);
            go.name = _name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_root, false);
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (_material != null) r.sharedMaterial = _material;
            r.enabled = on;
            return new Shape(go.transform, r);
        }

        public void Dispose()
        {
            _free.Clear();
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
        }
    }

    /// <summary>
    /// Short flashes (muzzle, impact): a sphere that grows from 40% to full size over its life, then goes back to
    /// the pool. Tick once a frame.
    /// </summary>
    public sealed class Flashes : IDisposable
    {
        struct Flash { public Shape Shape; public float Age, Life, Size; }

        readonly ShapePool _pool;
        readonly bool _ownsPool;
        readonly List<Flash> _live = new();

        public Flashes(ShapePool spheres, bool ownsPool)
        {
            _pool = spheres;
            _ownsPool = ownsPool;
        }

        public void Add(Vector3 at, float size, float life, Color color, int layer = 0) =>
            _live.Add(new Flash { Shape = _pool.Get(at, Quaternion.identity, Vector3.one * (size * 0.4f), color, layer),
                                  Life = life, Size = size });

        public void Tick(float dt)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                var f = _live[i];
                f.Age += dt;
                if (f.Age >= f.Life || !f.Shape.Alive)
                {
                    _pool.Release(f.Shape);
                    _live[i] = _live[^1];   // swap-remove: order doesn't matter, nothing shifts
                    _live.RemoveAt(_live.Count - 1);
                    continue;
                }
                f.Shape.Transform.localScale = Vector3.one * (f.Size * Mathf.Lerp(0.4f, 1f, f.Age / f.Life));
                _live[i] = f;
            }
        }

        /// <summary>Put every live flash back (an owner going away while its flashes are still up).</summary>
        public void Clear()
        {
            foreach (var f in _live) _pool.Release(f.Shape);
            _live.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (_ownsPool) _pool.Dispose();
        }
    }
}
