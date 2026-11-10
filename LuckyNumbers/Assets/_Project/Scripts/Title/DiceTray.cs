using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LuckyNumbers.Title
{
    /// <summary>
    /// 3D d20s for the dice panel. A small physics tray far below the world, on its own layer, filmed by its own
    /// orthographic camera into a render texture that DiceView shows behind the dice row. A throw drops the dice
    /// spinning into the tray; once they settle the face on top of each is its roll, and the dice glide back to their
    /// slots (under the row's hover boxes) and turn their number to the camera. Meshes and the face atlas (dark glass,
    /// neon edges, 7-segment numbers) are generated here; the material is Resources/DieHolo (URP Unlit).
    /// </summary>
    public sealed class DiceTray : MonoBehaviour
    {
        const int Layer = 31;
        const float Radius = 0.5f;                    // circumradius: the die is about 1 unit across
        static readonly Vector3 Origin = new(0f, -10000f, 0f);
        static readonly float Phi = (1f + Mathf.Sqrt(5f)) * 0.5f;
        static readonly float RestHeight = Radius * 0.7947f;   // inradius: centre height lying on a face

        sealed class Die
        {
            public Transform Transform;
            public Rigidbody Body;
            public Material Material;
            public Color Color;
            public Vector3 Slot;
        }

        readonly List<Die> _dice = new();
        Vector3[] _normals;     // per face, local
        Vector3[] _ups;         // per face, local: towards the top of its number
        int[] _numbers;         // per face
        int[] _faceOf;          // number -> face
        Camera _camera;
        RenderTexture _texture;
        Transform _walls;
        float _pixelsPerUnit = 76f;
        bool _throwing;

        public RenderTexture Texture => _texture;
        public bool Throwing => _throwing;

        public static DiceTray Create(IReadOnlyList<Color> colors)
        {
            var go = new GameObject("DiceTray") { layer = Layer };
            go.transform.position = Origin;
            var tray = go.AddComponent<DiceTray>();
            tray.Build(colors);
            return tray;
        }

        void Build(IReadOnlyList<Color> colors)
        {
            var mesh = BuildMesh(out _normals, out _ups, out _numbers);
            _faceOf = new int[21];
            for (var f = 0; f < _numbers.Length; f++) _faceOf[_numbers[f]] = f;
            var atlas = BuildAtlas(_numbers);
            var baseMaterial = Resources.Load<Material>("DieHolo");
            var bounce = new PhysicsMaterial("Die") { bounciness = 0.35f, dynamicFriction = 0.45f, staticFriction = 0.6f,
                                                      bounceCombine = PhysicsMaterialCombine.Maximum };

            for (var i = 0; i < colors.Count; i++)
            {
                var go = new GameObject($"Die{i}") { layer = Layer };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var material = baseMaterial != null ? new Material(baseMaterial) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                material.mainTexture = atlas;
                material.SetTexture("_BaseMap", atlas);
                material.color = colors[i];
                material.SetColor("_BaseColor", colors[i]);
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                collider.convex = true;
                collider.sharedMaterial = bounce;
                var body = go.AddComponent<Rigidbody>();
                body.mass = 1f;
                body.isKinematic = true;
                body.maxAngularVelocity = 60f;
                body.interpolation = RigidbodyInterpolation.None;   // Interpolate only while thrown (it would undo placing)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                go.AddComponent<ConstantForce>().force = new Vector3(0f, -14f, 0f);   // with gravity: a snappier fall
                _dice.Add(new Die { Transform = go.transform, Body = body, Material = material, Color = colors[i] });
            }

            var cameraGo = new GameObject("DiceCamera") { layer = Layer };
            cameraGo.transform.SetParent(transform, false);
            _camera = cameraGo.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.cullingMask = 1 << Layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 40f;
            _camera.allowHDR = false;
            _camera.depth = -100f;
            _camera.enabled = false;   // until there's a texture to draw into

            _walls = new GameObject("Walls") { layer = Layer }.transform;
            _walls.SetParent(transform, false);
            StripLayerFromOtherCameras();
        }

        /// <summary>
        /// Fit the tray to the panel: `width` x `height` pixels for the image, `diePixels` the size of one die box,
        /// and each slot's centre in image pixels from its left edge. Rebuilds the render texture and the walls.
        /// </summary>
        public void Layout(float width, float height, float diePixels, IReadOnlyList<float> slotPixels, float scale)
        {
            if (width < 4f || height < 4f || diePixels < 4f) return;
            _pixelsPerUnit = diePixels;
            var w = Mathf.CeilToInt(width * scale);
            var h = Mathf.CeilToInt(height * scale);
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) { _camera.targetTexture = null; _texture.Release(); Destroy(_texture); }
                _texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "DiceTray" };
                _texture.Create();
                _camera.targetTexture = _texture;
            }
            _camera.enabled = true;

            // Looking down at 62 degrees: horizontal pixels map 1:1 to the row; the tilt shows the dice in 3D.
            var halfWidth = width / _pixelsPerUnit * 0.5f;
            var halfHeight = height / _pixelsPerUnit * 0.5f;
            _camera.orthographicSize = halfHeight;
            var look = Quaternion.Euler(62f, 0f, 0f);
            var focus = Origin + new Vector3(0f, RestHeight, 0f);
            _camera.transform.SetPositionAndRotation(focus - look * Vector3.forward * 15f, look);

            for (var i = 0; i < _dice.Count && i < slotPixels.Count; i++)
                _dice[i].Slot = Origin + new Vector3(slotPixels[i] / _pixelsPerUnit - halfWidth, RestHeight, 0f);

            // Floor, walls and a lid, just inside what the camera sees (depth: the floor's visible span).
            var halfDepth = Mathf.Max(0.75f, halfHeight / Mathf.Sin(62f * Mathf.Deg2Rad));
            foreach (Transform child in _walls) Destroy(child.gameObject);
            Wall(new Vector3(0f, -0.5f, 0f), new Vector3(halfWidth * 2f + 4f, 1f, halfDepth * 2f + 4f));
            Wall(new Vector3(-halfWidth - 0.5f, 2f, 0f), new Vector3(1f, 6f, halfDepth * 2f + 4f));
            Wall(new Vector3(halfWidth + 0.5f, 2f, 0f), new Vector3(1f, 6f, halfDepth * 2f + 4f));
            Wall(new Vector3(0f, 2f, -halfDepth - 0.5f), new Vector3(halfWidth * 2f + 4f, 6f, 1f));
            Wall(new Vector3(0f, 2f, halfDepth + 0.5f), new Vector3(halfWidth * 2f + 4f, 6f, 1f));
            Wall(new Vector3(0f, 3.2f, 0f), new Vector3(halfWidth * 2f + 4f, 1f, halfDepth * 2f + 4f));

            if (!_throwing)
                for (var i = 0; i < _dice.Count; i++) _dice[i].Transform.position = _dice[i].Slot;
        }

        void Wall(Vector3 local, Vector3 size)
        {
            var go = new GameObject("Wall") { layer = Layer };
            go.transform.SetParent(_walls, false);
            go.transform.localPosition = local;
            go.AddComponent<BoxCollider>().size = size;
        }

        /// <summary>Lie in the slot with `face` on top, its number up the screen; `tumble` (degrees) tilts it off.</summary>
        public void ShowFace(int index, int face, float tumble)
        {
            if (index < 0 || index >= _dice.Count || _throwing) return;
            var die = _dice[index];
            var rotation = Readable(_faceOf[Mathf.Clamp(face, 1, 20)]);
            if (Mathf.Abs(tumble) > 0.01f)
                rotation = Quaternion.Euler(tumble * 1.3f, tumble * 2.1f, -tumble) * rotation;
            Place(die, die.Slot + Vector3.up * (Mathf.Abs(tumble) * 0.006f), rotation);
        }

        static void Place(Die die, Vector3 position, Quaternion rotation)
        {
            die.Body.isKinematic = true;
            die.Body.interpolation = RigidbodyInterpolation.None;
            die.Transform.SetPositionAndRotation(position, rotation);
            die.Body.position = position;
            die.Body.rotation = rotation;
        }

        /// <summary>The hovered die glows brighter (-1: none).</summary>
        public void SetActive(int index)
        {
            for (var i = 0; i < _dice.Count; i++)
            {
                var c = i == index ? Color.Lerp(_dice[i].Color, Color.white, 0.35f) : _dice[i].Color;
                _dice[i].Material.SetColor("_BaseColor", c);
                _dice[i].Material.color = c;
            }
        }

        /// <summary>Throw every die; completes with the face on top of each once they've settled and slid home.</summary>
        public async UniTask<int[]> ThrowAsync(uint seed, CancellationToken ct)
        {
            _throwing = true;
            var rng = new Random(seed | 1u);
            for (var i = 0; i < _dice.Count; i++)
            {
                var d = _dice[i];
                Place(d, d.Slot + new Vector3(rng.NextFloat(-0.25f, 0.25f), rng.NextFloat(0.9f, 1.4f), rng.NextFloat(-0.2f, 0.2f)),
                      Quaternion.Euler(rng.NextFloat(360f), rng.NextFloat(360f), rng.NextFloat(360f)));
                d.Body.isKinematic = false;
                d.Body.interpolation = RigidbodyInterpolation.Interpolate;
                d.Body.linearVelocity = new Vector3(rng.NextFloat(-3.5f, 3.5f), rng.NextFloat(-2f, 0.5f), rng.NextFloat(-1.5f, 1.5f));
                d.Body.angularVelocity = new Vector3(rng.NextFloat(-1f, 1f), rng.NextFloat(-1f, 1f), rng.NextFloat(-1f, 1f)).normalized * rng.NextFloat(18f, 32f);
            }

            // Settled: everything still for a moment (or asleep), at most a few seconds.
            var start = Time.time;
            var stillSince = -1f;
            while (Time.time - start < 4.5f)
            {
                await UniTask.Yield(PlayerLoopTiming.FixedUpdate, ct);
                var still = true;
                foreach (var d in _dice)
                    if (!d.Body.IsSleeping() && (d.Body.linearVelocity.sqrMagnitude > 0.004f || d.Body.angularVelocity.sqrMagnitude > 0.01f))
                        still = false;
                if (!still) { stillSince = -1f; continue; }
                if (stillSince < 0f) stillSince = Time.time;
                if (Time.time - stillSince > 0.25f) break;
            }

            var faces = new int[_dice.Count];
            var from = new Vector3[_dice.Count];
            var fromRot = new Quaternion[_dice.Count];
            var toRot = new Quaternion[_dice.Count];
            for (var i = 0; i < _dice.Count; i++)
            {
                var d = _dice[i];
                d.Body.isKinematic = true;
                d.Body.interpolation = RigidbodyInterpolation.None;
                var face = TopFace(d.Transform.rotation);
                faces[i] = _numbers[face];
                from[i] = d.Transform.position;
                fromRot[i] = d.Transform.rotation;
                toRot[i] = Readable(face);
            }

            // Home: glide back to the slots and turn the number to the camera.
            const float glide = 0.45f;
            for (var t = 0f; t < glide; t += Time.deltaTime)
            {
                var e = Mathf.SmoothStep(0f, 1f, t / glide);
                for (var i = 0; i < _dice.Count; i++)
                {
                    var hop = Mathf.Sin(e * Mathf.PI) * 0.25f;
                    _dice[i].Transform.SetPositionAndRotation(Vector3.Lerp(from[i], _dice[i].Slot, e) + Vector3.up * hop,
                                                              Quaternion.Slerp(fromRot[i], toRot[i], e));
                }
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
            for (var i = 0; i < _dice.Count; i++) Place(_dice[i], _dice[i].Slot, toRot[i]);
            _throwing = false;
            return faces;
        }

        int TopFace(Quaternion rotation)
        {
            var best = 0;
            var bestDot = float.MinValue;
            for (var f = 0; f < _normals.Length; f++)
            {
                var dot = Vector3.Dot(rotation * _normals[f], Vector3.up);
                if (dot > bestDot) { bestDot = dot; best = f; }
            }
            return best;
        }

        /// <summary>
        /// Face `face` turned to the camera, the top of its number up the screen, so the big number in the middle is
        /// the roll (lying flat, the tilted view would show a neighbouring face larger).
        /// </summary>
        Quaternion Readable(int face)
        {
            var toCamera = _camera != null ? -_camera.transform.forward : Vector3.up;
            var screenUp = _camera != null ? _camera.transform.up : Vector3.forward;
            return Quaternion.LookRotation(toCamera, screenUp) * Quaternion.Inverse(Quaternion.LookRotation(_normals[face], _ups[face]));
        }

        float _stripAt;

        void Update()
        {
            if (Time.unscaledTime < _stripAt) return;
            _stripAt = Time.unscaledTime + 1f;
            StripLayerFromOtherCameras();
        }

        /// <summary>The tray is on its own layer: no other camera should draw it.</summary>
        void StripLayerFromOtherCameras()
        {
            foreach (var cam in Camera.allCameras)
                if (cam != _camera) cam.cullingMask &= ~(1 << Layer);
        }

        void OnDestroy()
        {
            if (_camera != null) _camera.targetTexture = null;
            if (_texture != null) { _texture.Release(); Destroy(_texture); }
        }

        // ---- mesh ------------------------------------------------------------------------------------------------

        static readonly int[] Faces =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        // Where a face's corners sit in its atlas cell (0..1): top, lower left, lower right.
        static readonly Vector2[] CellTri = { new(0.5f, 0.93f), new(0.06f, 0.17f), new(0.94f, 0.17f) };
        const int Cols = 5, Rows = 4, Cell = 128;

        /// <summary>
        /// Icosahedron, flat faces (3 vertices each) with atlas UVs. Opposite faces are numbered to add up to 21,
        /// like a real d20.
        /// </summary>
        static Mesh BuildMesh(out Vector3[] normals, out Vector3[] ups, out int[] numbers)
        {
            var v = new[]
            {
                new Vector3(-1, Phi, 0), new Vector3(1, Phi, 0), new Vector3(-1, -Phi, 0), new Vector3(1, -Phi, 0),
                new Vector3(0, -1, Phi), new Vector3(0, 1, Phi), new Vector3(0, -1, -Phi), new Vector3(0, 1, -Phi),
                new Vector3(Phi, 0, -1), new Vector3(Phi, 0, 1), new Vector3(-Phi, 0, -1), new Vector3(-Phi, 0, 1),
            };
            for (var i = 0; i < v.Length; i++) v[i] = v[i].normalized * Radius;

            const int count = 20;
            normals = new Vector3[count];
            ups = new Vector3[count];
            numbers = new int[count];
            var corners = new int[count * 3];
            for (var f = 0; f < count; f++)
            {
                int a = Faces[f * 3], b = Faces[f * 3 + 1], c = Faces[f * 3 + 2];
                var centroid = (v[a] + v[b] + v[c]) / 3f;
                var outward = centroid.normalized;   // an icosahedron's face normal points through its centroid
                // `a` is the top of the number. Drawn as (a, c, b), which must face outward (Unity: front faces have Cross(v1 - v0, v2 - v0) towards the viewer).
                if (Vector3.Dot(Vector3.Cross(v[c] - v[a], v[b] - v[a]), outward) < 0f) (b, c) = (c, b);
                normals[f] = outward;
                ups[f] = (v[a] - centroid).normalized;
                corners[f * 3] = a; corners[f * 3 + 1] = b; corners[f * 3 + 2] = c;
            }

            var next = 1;
            for (var f = 0; f < count; f++)
            {
                if (numbers[f] != 0) continue;
                var opposite = 0;
                var best = float.MaxValue;
                for (var g = 0; g < count; g++)
                {
                    var d = Vector3.Dot(normals[f], normals[g]);
                    if (g != f && d < best) { best = d; opposite = g; }
                }
                numbers[f] = next;
                numbers[opposite] = 21 - next;
                next++;
            }

            var positions = new Vector3[count * 3];
            var uvs = new Vector2[count * 3];
            var vertexNormals = new Vector3[count * 3];
            var triangles = new int[count * 3];
            for (var f = 0; f < count; f++)
            {
                var cell = numbers[f] - 1;
                var origin = new Vector2(cell % Cols, cell / Cols);
                for (var k = 0; k < 3; k++)
                {
                    var i = f * 3 + k;
                    positions[i] = v[corners[i]];
                    vertexNormals[i] = normals[f];
                    // Drawn (a, c, b) = clockwise seen from outside: a top, c lower right, b lower left.
                    uvs[i] = new Vector2((origin.x + CellTri[k].x) / Cols, (origin.y + CellTri[k].y) / Rows);
                }
                triangles[f * 3] = f * 3;
                triangles[f * 3 + 1] = f * 3 + 2;
                triangles[f * 3 + 2] = f * 3 + 1;
            }
            var mesh = new Mesh { name = "d20", vertices = positions, uv = uvs, normals = vertexNormals, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- face atlas -------------------------------------------------------------------------------------------

        /// <summary>
        /// One cell per number: dark glass (tinted by the material's colour), a bright neon edge, the number in
        /// 7-segment digits (6 and 9 underlined). White is the die's colour after tinting.
        /// </summary>
        static Texture2D BuildAtlas(int[] numbers)
        {
            var width = Cols * Cell;
            var height = Rows * Cell;
            var pixels = new Color32[width * height];
            var a = CellTri[0];
            var b = CellTri[1];
            var c = CellTri[2];
            for (var n = 1; n <= 20; n++)
            {
                var cell = n - 1;
                int ox = cell % Cols * Cell, oy = cell / Cols * Cell;
                for (var y = 0; y < Cell; y++)
                for (var x = 0; x < Cell; x++)
                {
                    var p = new Vector2((x + 0.5f) / Cell, (y + 0.5f) / Cell);
                    var edge = Mathf.Min(EdgeDistance(p, a, b), Mathf.Min(EdgeDistance(p, b, c), EdgeDistance(p, c, a)));
                    float value;
                    if (edge < 0f) value = 0.1f;                                  // outside (never seen)
                    else if (edge < 0.035f) value = 1f;                           // neon edge
                    else if (edge < 0.06f) value = Mathf.Lerp(1f, 0.22f, (edge - 0.035f) / 0.025f);   // glow into the glass
                    else value = 0.16f + 0.08f * p.y;                             // glass, a little lighter up top
                    if (Digit(n, p)) value = 1f;
                    var g = (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
                    pixels[(oy + y) * width + ox + x] = new Color32(g, g, g, 255);
                }
            }
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = "d20 faces", wrapMode = TextureWrapMode.Clamp,
                                                                                    filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>Signed distance from p to edge a->b, positive inside the (counter-clockwise) cell triangle.</summary>
        static float EdgeDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var e = b - a;
            var n = new Vector2(e.y, -e.x).normalized;   // a (top) -> b (lower left) -> c (lower right) is counter-clockwise
            return -Vector2.Dot(p - a, n);
        }

        // 7-segment bits: a (top), b (upper right), c (lower right), d (bottom), e (lower left), f (upper left), g (middle).
        static readonly int[] Segments = { 0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110, 0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111 };

        static bool Digit(int number, Vector2 p)
        {
            // Digits sit a little below the face's centroid (y ~ 0.42), where the triangle is wider.
            const float h = 0.26f, w = 0.13f, gap = 0.045f, t = 0.03f, cy = 0.4f;
            var text = number.ToString();
            var total = text.Length * w + (text.Length - 1) * gap;
            var x0 = 0.5f - total * 0.5f;
            for (var i = 0; i < text.Length; i++)
            {
                var d = text[i] - '0';
                var left = x0 + i * (w + gap);
                if (Seg7(Segments[d], p, left, cy - h * 0.5f, w, h, t)) return true;
            }
            // 6 and 9 underlined, so they read either way up.
            if (number == 6 || number == 9)
                return p.x >= 0.5f - w * 0.6f && p.x <= 0.5f + w * 0.6f && p.y >= cy - h * 0.5f - 0.07f && p.y <= cy - h * 0.5f - 0.07f + t;
            return false;
        }

        static bool Seg7(int bits, Vector2 p, float x, float y, float w, float h, float t)
        {
            bool Rect(float rx, float ry, float rw, float rh) => p.x >= rx && p.x <= rx + rw && p.y >= ry && p.y <= ry + rh;
            var mid = y + h * 0.5f;
            if ((bits & 1) != 0 && Rect(x, y + h - t, w, t)) return true;                 // a
            if ((bits & 2) != 0 && Rect(x + w - t, mid, t, h * 0.5f)) return true;        // b
            if ((bits & 4) != 0 && Rect(x + w - t, y, t, h * 0.5f)) return true;          // c
            if ((bits & 8) != 0 && Rect(x, y, w, t)) return true;                         // d
            if ((bits & 16) != 0 && Rect(x, y, t, h * 0.5f)) return true;                 // e
            if ((bits & 32) != 0 && Rect(x, mid, t, h * 0.5f)) return true;               // f
            if ((bits & 64) != 0 && Rect(x, mid - t * 0.5f, w, t)) return true;           // g
            return false;
        }
    }
}
