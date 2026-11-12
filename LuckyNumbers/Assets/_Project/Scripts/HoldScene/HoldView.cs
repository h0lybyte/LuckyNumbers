using System.Collections.Generic;
using UnityEngine;

namespace LuckyNumbers.HoldScene
{
    /// <summary>
    /// The hold's meshes in the scene: one child per chunk (mesh + its collider boxes), all with the one HoldKit
    /// material. HoldPresenter decides what's shown; this only creates, reuses and hides the objects.
    /// </summary>
    public sealed class HoldView : MonoBehaviour
    {
        [SerializeField] Material material;

        readonly Dictionary<long, GameObject> _chunks = new();
        readonly Stack<GameObject> _spare = new();

        public Material Material => material;

        public void Init(Material kit) => material = kit;

        public bool Has(long key) => _chunks.ContainsKey(key);

        /// <summary>Show a chunk: its mesh (world-space vertices) and collider boxes (world-space bounds).</summary>
        public void Show(long key, Mesh mesh, IReadOnlyList<Bounds> boxes)
        {
            Hide(key);
            var go = _spare.Count > 0 ? _spare.Pop() : NewChunk();
            go.name = $"Chunk {key}";
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            foreach (var old in go.GetComponents<BoxCollider>()) Destroy(old);
            if (boxes != null)
                foreach (var b in boxes)
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                }
            go.SetActive(true);
            _chunks[key] = go;
        }

        public void Hide(long key)
        {
            if (!_chunks.Remove(key, out var go)) return;
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            if (mesh != null) Destroy(mesh);
            go.GetComponent<MeshFilter>().sharedMesh = null;
            go.SetActive(false);
            _spare.Push(go);
        }

        public IEnumerable<long> Keys => _chunks.Keys;

        GameObject NewChunk()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }
    }
}
