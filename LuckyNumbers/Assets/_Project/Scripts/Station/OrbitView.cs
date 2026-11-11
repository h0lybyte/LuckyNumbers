using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.Station
{
    /// <summary>
    /// The station seen from outside (Station/OrbitPresenter): placeholder blocks for its hull (one per stretch of
    /// rooms and walls in its plan, named so they're easy to swap for a real model later) and the docking beacon, a
    /// glowing frame off the bay's outer wall. Materials come from SceneBuilder.BuildOrbit.
    /// </summary>
    public sealed class OrbitView : MonoBehaviour
    {
        [SerializeField] Material hull;
        [SerializeField] Material trim;
        [SerializeField] Material beacon;

        readonly List<GameObject> _built = new();
        Transform _beacon;

        /// <summary>One block per box (world centre and size); `trims` get the lit trim material (the bay's walls).</summary>
        public void BuildHull(IReadOnlyList<Bounds> blocks, IReadOnlyList<Bounds> trims)
        {
            foreach (var g in _built) Destroy(g);
            _built.Clear();
            for (var i = 0; i < blocks.Count; i++) Block($"Hull block {i} (placeholder)", blocks[i], hull);
            for (var i = 0; i < trims.Count; i++) Block($"Bay trim {i} (placeholder)", trims[i], trim);
        }

        /// <summary>The docking beacon: a square frame `size` across at `position`, facing along `normal`.</summary>
        public void Beacon(Vector3 position, Vector3 normal, float size)
        {
            if (_beacon != null) Destroy(_beacon.gameObject);
            _beacon = new GameObject("Docking beacon (placeholder)").transform;
            _beacon.SetParent(transform, false);
            _beacon.SetPositionAndRotation(position, Quaternion.LookRotation(normal, Vector3.up));
            var bar = size * 0.06f;
            var half = size * 0.5f;
            void Bar(string name, Vector3 at, Vector3 scale)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = name;
                Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(_beacon, false);
                g.transform.localPosition = at;
                g.transform.localScale = scale;
                var r = g.GetComponent<MeshRenderer>();
                r.sharedMaterial = beacon;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            Bar("Top", new Vector3(0f, half, 0f), new Vector3(size + bar, bar, bar));
            Bar("Bottom", new Vector3(0f, -half, 0f), new Vector3(size + bar, bar, bar));
            Bar("Left", new Vector3(-half, 0f, 0f), new Vector3(bar, size, bar));
            Bar("Right", new Vector3(half, 0f, 0f), new Vector3(bar, size, bar));
        }

        /// <summary>The beacon's frame pulses brighter while you're inside it.</summary>
        public void BeaconScale(float scale)
        {
            if (_beacon != null) _beacon.localScale = Vector3.one * scale;
        }

        void Block(string name, Bounds b, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false);
            g.transform.position = b.center;
            g.transform.localScale = b.size;
            g.GetComponent<MeshRenderer>().sharedMaterial = m;
            _built.Add(g);
        }
    }
}
