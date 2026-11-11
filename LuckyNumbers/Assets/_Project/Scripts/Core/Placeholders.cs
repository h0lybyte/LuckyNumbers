using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Plain coloured blocks for things that don't have a model yet (dropped items, the range's crates and targets),
    /// named PLACEHOLDER_* so they're easy to find and replace. Unlit (Resources/Placeholder, URP Unlit): a primitive's
    /// default material is stripped from web builds and shows magenta.
    /// </summary>
    public static class Placeholders
    {
        static Material _material;

        public static GameObject Block(string name, Vector3 size, Color color, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name.StartsWith("PLACEHOLDER_") ? name : "PLACEHOLDER_" + name;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            go.transform.localScale = size;
            var renderer = go.GetComponent<Renderer>();
            _material ??= Resources.Load<Material>("Placeholder");
            if (_material != null) renderer.sharedMaterial = _material;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
            return go;
        }
    }
}
