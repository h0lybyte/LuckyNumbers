using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// Dust round your ship (content/orbit.txtpb dust): motes that hang still in space, so open space has something
    /// to read your speed and drift against. They live in a box round you and wrap to its far side as you fly on
    /// (shrinking to nothing near its edge, so none pops in), each a streak along your velocity as long as you move in
    /// a few hundredths of a second. One instanced draw of the bolts' material.
    /// </summary>
    public sealed class SpaceDust : IDisposable
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        readonly Vector3[] _motes;
        readonly Matrix4x4[] _matrices;
        readonly Vector4[] _colors;
        readonly MaterialPropertyBlock _block = new();
        readonly Material _material;
        readonly Mesh _mesh;
        readonly GameObject _meshSource;
        readonly float _half, _width;
        readonly int _layer;

        public SpaceDust(int count, float radius, float width, int layer, uint seed)
        {
            count = math.clamp(count, 0, 1023);
            _motes = new Vector3[count];
            _matrices = new Matrix4x4[count];
            _colors = new Vector4[math.max(1, count)];
            _half = math.max(1f, radius);
            _width = math.max(0.01f, width);
            _layer = layer;
            var rng = new Random(seed | 1u);
            for (var i = 0; i < count; i++)
            {
                _motes[i] = (Vector3)rng.NextFloat3(-_half, _half);
                var shade = rng.NextFloat(0.35f, 0.75f);
                _colors[i] = new Vector4(shade * 0.9f, shade * 0.95f, shade, 1f);
            }
            _material = Resources.Load<Material>("Bolt");
            if (_material != null) _material.enableInstancing = true;
            _meshSource = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _mesh = _meshSource.GetComponent<MeshFilter>().sharedMesh;
            _meshSource.SetActive(false);
            _meshSource.name = "[Dust] mesh";
        }

        /// <summary>Draw this frame round `center`, streaked along `velocity` (the motes sit still; you're the one moving).</summary>
        public void Draw(Vector3 center, Vector3 velocity)
        {
            if (_material == null || _motes.Length == 0) return;
            var speed = velocity.magnitude;
            var dir = speed > 0.1f ? velocity / speed : Vector3.up;
            var along = math.clamp(speed * 0.04f, _width, _half * 0.15f);
            var turn = Quaternion.FromToRotation(Vector3.up, dir);
            var size = _half * 2f;
            for (var i = 0; i < _motes.Length; i++)
            {
                // Wrap into the box round you.
                var p = _motes[i] - center;
                p.x -= size * math.floor((p.x + _half) / size);
                p.y -= size * math.floor((p.y + _half) / size);
                p.z -= size * math.floor((p.z + _half) / size);
                _motes[i] = center + p;
                var edge = math.max(math.abs(p.x), math.max(math.abs(p.y), math.abs(p.z))) / _half;
                var fade = math.saturate((1f - edge) * 4f);
                _matrices[i] = Matrix4x4.TRS(_motes[i], turn, new Vector3(_width * fade, along * 0.5f * fade, _width * fade));
            }
            _block.SetVectorArray(BaseColor, _colors);
            Graphics.RenderMeshInstanced(new RenderParams(_material)
            {
                layer = _layer, worldBounds = new Bounds(center, Vector3.one * (size + along * 2f)),
                shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, matProps = _block,
            }, _mesh, 0, _matrices, _motes.Length);
        }

        public void Dispose()
        {
            if (_meshSource != null) Object.Destroy(_meshSource);
        }
    }
}
