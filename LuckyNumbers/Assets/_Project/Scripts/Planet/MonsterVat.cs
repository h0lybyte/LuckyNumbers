using System;
using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>
    /// A monster model's clips baked into an animation texture (Editor/VatBaker, run by SceneBuilder.BakeMonsters), so
    /// MonsterCrowd draws every monster of the model GPU-instanced: no Animator, no SkinnedMeshRenderer, no renderer
    /// per monster.
    ///
    /// What's baked is the skeleton, not the vertices: <see cref="Bones"/> (RGBA half) holds every bone's skinning
    /// matrix (bone to model space x bind pose: the top 3 rows, one texel each) per frame, a frame per row, bone b at
    /// x = 3b. The vertex shader skins <see cref="Mesh"/> (the skin's bind pose, its 4 bone indices in uv1 and weights
    /// in uv2) from it. A per-vertex texture would be 77 MB for the aberration (19 344 vertices x 384 frames);
    /// this is ~0.5 MB. Model space is MonsterRig's parent's (the rig's turn and scale baked in, its feet at 0).
    /// A clip's frames run from its start to its end inclusive (a loop's last frame is its first again), sampled at
    /// <see cref="Fps"/>; the shader blends neighbouring frames, and two clips for cross-fades.
    /// <see cref="Material"/> is LuckyNumbers/MonsterVat with the model's own skin (the Ship look).
    /// </summary>
    public sealed class MonsterVat : ScriptableObject
    {
        [Serializable]
        public struct Clip
        {
            public string Name;
            /// <summary>First frame (its row in Bones) and how many.</summary>
            public int Start, Frames;
            /// <summary>Seconds, as the AnimationClip.</summary>
            public float Length;
            /// <summary>The import's loop flag (MonsterView loops or holds by how it plays the clip, as MonsterRig).</summary>
            public bool Loop;
            /// <summary>Stride, m/s (aberration.json; 0: in place).</summary>
            public float Speed;
            /// <summary>Every frame's vertices, model space.</summary>
            public Bounds Bounds;
        }

        public Mesh Mesh;
        public Material Material;
        public Texture2D Bones;
        public int BoneCount, Vertices;
        public float Fps;
        public Clip[] Clips = Array.Empty<Clip>();

        public int Index(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return -1;
            for (var i = 0; i < Clips.Length; i++) if (Clips[i].Name == clip) return i;
            return -1;
        }

        public bool Has(string clip) => Index(clip) >= 0;

        public float Length(string clip)
        {
            var i = Index(clip);
            return i < 0 ? 0f : Clips[i].Length;
        }

        public float Stride(string clip)
        {
            var i = Index(clip);
            return i < 0 ? 0f : Clips[i].Speed;
        }

        /// <summary>Everything a draw needs is here, and this device can sample half-float textures in a vertex shader.</summary>
        public bool Usable => Mesh != null && Material != null && Material.shader != null && Material.shader.isSupported &&
                              Bones != null && Clips.Length > 0 && SystemInfo.supportsInstancing &&
                              SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf);

        /// <summary>
        /// The shader's frame pair for `clip` at `time` s: (first frame, next frame, blend between them), absolute
        /// frames. Clamped to the clip (a once clip's end holds its last frame).
        /// </summary>
        public static Vector3 Frames(in Clip clip, float time) => MonsterCrowd.Frames(clip.Start, clip.Frames, clip.Length, time);
    }
}
