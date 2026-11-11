using System.Collections.Generic;
using LuckyNumbers.Model;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// A ship: a pivot (+Z forward, +Y up) around its model. With skins (the player's fighter) one shared material
    /// swaps textures; without (the museum freighter) every part keeps its own material. Heat and engine glow
    /// apply to all of them. Shared by every scene that shows a ship, so each scope registers it itself (no [View]).
    ///
    /// Engines (content/engines.txtpb, `engines` names the layout): a flame per jet (LuckyNumbers/Exhaust), built
    /// on first Show. Whoever flies the ship calls SetThrust each frame with what it asks for (a push and a turn
    /// in the ship's frame); each jet burns for its share of that, from which way it pushes and how it turns the
    /// ship about its centre, so the mains light for forward, the belly for up, the side jets for strafe, and
    /// opposite pairs fire to pitch, yaw and roll. Flames spool up and die down; EnginesOff cuts them all.
    /// Cold-gas jets (LuckyNumbers/ColdGas) don't burn: past a small share they puff compressed air, one short
    /// cloud after another while still asked, so fine corrections show as little bursts under and beside the ship.
    ///
    /// Landing legs (the layout's `legs`, LandingGear): tucked away unless whoever parks the ship calls GearDown with
    /// how high it sits (landed on the ground, docked in the bay); GearUp folds them as it lifts off.
    ///
    /// Hull from Addressables (`model` set, the museum freighter): the scene holds only this shell (pivot, length,
    /// engines), so it can be posed, shown and read for its position at once; ShipYard.Fit loads the hull prefab
    /// (Prefabs/Ships) and hands it to Fit, which puts it under the pivot and applies the heat already set. Until
    /// then the ship draws nothing and HullRenderers is empty (Fitted says which). Hold the scene's SceneCurtain
    /// with that load so nobody sees it pop in.
    /// </summary>
    public sealed class ShipView : MonoBehaviour
    {
        static readonly int Heat = Shader.PropertyToID("_Heat");
        static readonly int Glow = Shader.PropertyToID("_Glow");
        static readonly int Thrust = Shader.PropertyToID("_Thrust");
        static readonly int Seed = Shader.PropertyToID("_Seed");
        static readonly int Puff = Shader.PropertyToID("_Puff");

        [SerializeField] Transform pivot;
        [SerializeField] Renderer[] renderers;
        [SerializeField] Texture2D[] skins;   // Art/Models/Fighter/Skins, set by the scene builder
        [SerializeField] float modelLength = 0.49f;
        [SerializeField] string engines;      // EngineConfig.ships[].ship ("fighter"); empty: no flames
        [SerializeField] Material exhaust;    // LuckyNumbers/Exhaust, set by the scene builder
        [SerializeField] Material coldGas;    // LuckyNumbers/ColdGas, set by the scene builder
        [SerializeField] string model;        // the hull prefab's address (ShipYard fits it); empty: hull baked in
        [Tooltip("Gun muzzles (the pivot's space, model units: +Z the nose): the tips of the fighter's twin gun barrels " +
                 "(the rods beside the cockpit, fighter1.fbx), left then right.")]
        [SerializeField] Vector3[] muzzles = { new(-0.111f, -0.0074f, 0.116f), new(0.111f, -0.0074f, 0.116f) };
        Material _material;                                         // skinned ships: the one shared material
        readonly List<(Material material, float glow)> _parts = new();
        bool _fitted, _shown;
        float _heat, _throttle = 1f;   // the last SetHeat, for a hull fitted after it

        sealed class Jet
        {
            public Thruster Def;
            public Renderer Renderer;
            public Vector3 Push;     // unit, ship frame: the way it shoves the ship
            public Vector3 Turn;     // unit turn axis it gives about the centre (zero: no useful lever)
            public float Lever;      // 0..1: how much it can turn the ship (arm against the ship's size)
            public float Target, Value;
            public float Age = 1f;   // gas: through the current puff, 0..1 (1: none showing)
        }

        readonly List<Jet> _jets = new();
        MaterialPropertyBlock _blockStore;
        MaterialPropertyBlock _block => _blockStore ??= new MaterialPropertyBlock();   // not in a field initializer (Unity)
        bool _jetsBuilt, _running, _gearBuilt;
        LandingGear _gear;
        const float GearSeconds = 0.9f;   // the legs' whole travel, tucked to on the ground
        float _boost = 1f;
        Material _mainMat, _rcsMat, _gasMat;
        static Mesh _plume;

        public Transform Pivot => pivot;
        public int SkinCount => skins.Length;
        public string SkinAt(int index) => skins[index].name;
        public string SkinName => _material != null && _material.mainTexture != null ? _material.mainTexture.name : "";

        /// <summary>The hull's renderers (its model, not the flames). Empty until Fitted.</summary>
        public IReadOnlyList<Renderer> HullRenderers => renderers;

        /// <summary>The hull prefab's address, for ShipYard (empty: the hull is baked into the scene).</summary>
        public string ModelAddress => model;

        /// <summary>Is the hull here (baked in, or loaded and fitted)?</summary>
        public bool Fitted => string.IsNullOrEmpty(model) || _fitted;

        /// <summary>
        /// The loaded hull (ShipYard): under the pivot, +Z forward like the rest. Its parts take the heat already set,
        /// and show at once if the ship is already showing.
        /// </summary>
        public void Fit(GameObject hull)
        {
            if (_fitted) { Debug.LogWarning($"[Ship] {name}: hull already fitted"); return; }
            _fitted = true;
            hull.transform.SetParent(pivot, false);
            renderers = hull.GetComponentsInChildren<Renderer>(true);
            if (_shown) TakeParts();
            SetHeat(_heat, _throttle);
        }

        /// <summary>The hull's materials, for heat and glow (once, when there are renderers to take them from).</summary>
        void TakeParts()
        {
            if (_parts.Count > 0 || renderers.Length == 0) return;
            if (skins.Length > 0)
            {
                _material = new Material(renderers[0].sharedMaterial);
                foreach (var r in renderers) r.sharedMaterial = _material;
                _parts.Add((_material, _material.GetFloat(Glow)));
            }
            else
                foreach (var r in renderers) { var m = r.material; _parts.Add((m, m.GetFloat(Glow))); }   // own copies, for heat
        }

        /// <summary>World bounds of the hull (its model only, not the flames).</summary>
        public Bounds Hull
        {
            get
            {
                var hull = new Bounds(pivot.position, Vector3.zero);
                var first = true;
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (first) { hull = r.bounds; first = false; }
                    else hull.Encapsulate(r.bounds);
                }
                return hull;
            }
        }

        /// <summary>Show the ship at a world length; `skin` (file name) picks a skin if it has any (unknown: the first).</summary>
        public void Show(string skin, float length)
        {
            _shown = true;
            TakeParts();
            if (skins.Length > 0)
            {
                var tex = skins[0];
                foreach (var s in skins) if (s.name == skin) tex = s;
                if (tex.name != skin) Debug.LogWarning($"[Ship] no skin '{skin}', using {tex.name}");
                _material.mainTexture = tex;
                Debug.Log($"[Ship] {name}: skin {tex.name}");
            }
            BuildJets();
            BuildGear();
            pivot.localScale = Vector3.one * (length / Mathf.Max(modelLength, 1e-4f));
            pivot.gameObject.SetActive(true);
        }

        public int MuzzleCount => muzzles.Length;

        /// <summary>A barrel's muzzle in the world (where its gun's bolts leave).</summary>
        public Vector3 Muzzle(int index) => pivot.TransformPoint(muzzles[index]);

        public void Hide()
        {
            EnginesOff(instant: true);
            GearUp(instant: true);
            pivot.gameObject.SetActive(false);
        }

        public void Pose(Vector3 position, Quaternion rotation) => pivot.SetPositionAndRotation(position, rotation);

        /// <summary>
        /// Legs out to the ground <paramref name="height"/> metres below the pivot (along the ship's down). Call after
        /// Show (it sizes the ship). No legs in the layout: nothing.
        /// </summary>
        public void GearDown(float height, bool instant = false) =>
            _gear?.Down(height / Mathf.Max(1e-6f, pivot.lossyScale.y), instant);

        /// <summary>Fold the legs away (lifting off).</summary>
        public void GearUp(bool instant = false) => _gear?.Up(instant);

        /// <summary>Set the legs part way out now (previews: no LateUpdate in the editor).</summary>
        public void SnapGear(float extend) => _gear?.Snap(extend);

        /// <summary>How far out the legs are: 0 tucked away .. 1 on the ground.</summary>
        public float Gear => _gear?.Value ?? 0f;

        /// <summary>Entry heat 0..1, engine throttle (glow multiplier).</summary>
        public void SetHeat(float heat, float throttle)
        {
            _heat = heat;
            _throttle = throttle;
            foreach (var (m, glow) in _parts)
            {
                m.SetFloat(Heat, heat);
                m.SetFloat(Glow, glow * throttle);
            }
        }

        /// <summary>
        /// What the pilot asks for now, in the ship's frame: `push` (x right, y up, z forward; each -1..1, the share
        /// of what the jets can do) and `turn` (x pitch nose down, y yaw right, z roll left, like the Euler rates the
        /// flight code spins with; each -1..1). `boost` above 1 stretches the mains. Starts the engines (mains idle).
        /// </summary>
        public void SetThrust(Vector3 push, Vector3 turn, float boost = 1f)
        {
            _running = true;
            _boost = Mathf.Max(1f, boost);
            var cfg = GameContent.Engines;
            foreach (var j in _jets)
            {
                var share = Vector3.Dot(j.Push, push);
                if (j.Def.Main)
                {
                    j.Target = Mathf.Max(cfg.Idle, Mathf.Clamp01(share));
                    continue;
                }
                share += Vector3.Dot(j.Turn, turn) * j.Lever;
                var threshold = j.Def.Gas ? cfg.GasThreshold : cfg.RcsThreshold;
                j.Target = share > threshold ? Mathf.Clamp01(share) : 0f;
            }
        }

        /// <summary>Cut the engines (parked, crashed, docked): every flame dies down, or goes out at once.</summary>
        public void EnginesOff(bool instant = false)
        {
            _running = false;
            foreach (var j in _jets)
            {
                j.Target = 0f;
                if (!instant) continue;
                j.Value = 0f;
                j.Age = 1f;
            }
            if (instant) Apply();
        }

        /// <summary>
        /// What the jets are doing now, for a readout (the Flight School): each lit flame with its share ("main_l 82"),
        /// then on a second line the cold-gas jets puffing ("GAS side_r under_tail_l"); "" when nothing is lit.
        /// </summary>
        public string JetReadout()
        {
            var flames = new System.Text.StringBuilder();
            var gas = new System.Text.StringBuilder();
            foreach (var j in _jets)
            {
                if (j.Value <= 0.01f) continue;
                if (j.Def.Gas) gas.Append(gas.Length == 0 ? "GAS  " : "  ").Append(j.Def.Name);
                else flames.Append(flames.Length == 0 ? "" : "  ").Append(j.Def.Name).Append(' ').Append(Mathf.RoundToInt(j.Value * 100f));
            }
            if (gas.Length > 0) flames.Append(flames.Length == 0 ? "" : "\n").Append(gas);
            return flames.ToString();
        }

        /// <summary>Flames straight to what's asked, no spooling (previews, which have no frames to ease over).</summary>
        public void SnapEngines()
        {
            foreach (var j in _jets)
            {
                j.Value = _running ? j.Target : 0f;
                if (j.Def.Gas) j.Age = j.Value > 0f ? 0.3f : 1f;   // a puff on its way out
            }
            Apply();
        }

        void LateUpdate()
        {
            _gear?.Step(Time.deltaTime, GearSeconds);
            if (_jets.Count == 0) return;
            var cfg = GameContent.Engines;
            var dt = Time.deltaTime;
            var up = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, cfg.SpoolUpSeconds));
            var down = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, cfg.SpoolDownSeconds));
            var life = Mathf.Max(0.05f, cfg.GasLifeSeconds);
            var repeat = Mathf.Clamp(cfg.GasRepeat, 0.1f, 1f);
            foreach (var j in _jets)
            {
                var target = _running ? j.Target : 0f;
                if (j.Def.Gas)
                {
                    // A puff runs its course; while still asked, the next one starts part way through it.
                    j.Age = Mathf.Min(1f, j.Age + dt / life);
                    if (target > 0f && j.Age >= repeat)
                    {
                        j.Age = 0f;
                        j.Value = Mathf.Max(0.35f, target);   // even a nudge makes a visible puff
                    }
                    if (j.Age >= 1f) j.Value = 0f;
                    continue;
                }
                j.Value = Mathf.Lerp(j.Value, target, target > j.Value ? up : down);
                if (j.Value < 0.002f && target <= 0f) j.Value = 0f;
            }
            Apply();
        }

        void Apply()
        {
            var stretch = Mathf.Max(1f, GameContent.Engines.BoostStretch);
            foreach (var j in _jets)
            {
                var on = j.Value > 0f;
                if (j.Renderer.enabled != on) j.Renderer.enabled = on;
                if (!on) continue;
                // Boost: the mains reach further (_Thrust past 1), by the boost's share of its full stretch.
                var boost = j.Def.Main ? 1f + (stretch - 1f) * Mathf.Clamp01(_boost - 1f) : 1f;
                j.Renderer.GetPropertyBlock(_block);
                _block.SetFloat(Thrust, j.Value * boost);
                if (j.Def.Gas) _block.SetFloat(Puff, j.Age);
                j.Renderer.SetPropertyBlock(_block);
            }
        }

        /// <summary>The layout's landing legs, under the pivot (LandingGear), tucked away.</summary>
        void BuildGear()
        {
            if (_gearBuilt) return;
            _gearBuilt = true;
            if (string.IsNullOrEmpty(engines)) return;
            var layout = GameContent.Engines?.Ships.Find(s => s.Ship == engines);
            if (layout == null || layout.Legs.Count == 0) return;
            var material = Resources.Load<Material>("LandingLeg");
            if (material == null) Debug.LogWarning($"[Ship] {name}: no Resources/LandingLeg material, no legs");
            _gear = LandingGear.Build(pivot, layout.Legs, material);
        }

        /// <summary>A flame per jet in the layout, under the pivot (so it scales and moves with the ship).</summary>
        void BuildJets()
        {
            if (_jetsBuilt) return;
            _jetsBuilt = true;
            if (string.IsNullOrEmpty(engines) || exhaust == null) return;
            var cfg = GameContent.Engines;
            var layout = cfg?.Ships.Find(s => s.Ship == engines);
            if (layout == null)
            {
                Debug.LogWarning($"[Ship] {name}: no engine layout '{engines}' in engines.txtpb");
                return;
            }
            _mainMat = new Material(exhaust) { name = "Exhaust (main)" };
            _rcsMat = new Material(exhaust) { name = "Exhaust (rcs)" };
            cfg.MainLook?.ApplyTo(_mainMat);
            cfg.RcsLook?.ApplyTo(_rcsMat);
            if (coldGas != null)
            {
                _gasMat = new Material(coldGas) { name = "Cold gas" };
                cfg.GasLook?.ApplyTo(_gasMat);
            }
            var centre = layout.Centre.ToVector3();
            // Lever: the arm that counts as a full turn (a jet at the ship's ends turns it hardest).
            var reach = 0.01f;
            foreach (var t in layout.Thrusters) reach = Mathf.Max(reach, (t.Position.ToVector3() - centre).magnitude);
            var root = new GameObject("Engines").transform;
            root.SetParent(pivot, false);
            var index = 0;
            foreach (var t in layout.Thrusters)
            {
                if (t.Gas && _gasMat == null)
                {
                    Debug.LogWarning($"[Ship] {name}: no cold-gas material, skipping jet {t.Name} (rebuild the scenes)");
                    continue;
                }
                var dir = t.Direction.ToVector3();
                if (dir.sqrMagnitude < 1e-6f) dir = Vector3.back;
                dir.Normalize();
                var go = new GameObject($"Jet {t.Name}");
                go.transform.SetParent(root, false);
                go.transform.localPosition = t.Position.ToVector3();
                go.transform.localRotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up);
                go.transform.localScale = new Vector3(t.Radius, t.Radius, t.Length);
                go.AddComponent<MeshFilter>().sharedMesh = Plume();
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = t.Main ? _mainMat : t.Gas ? _gasMat : _rcsMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;
                r.GetPropertyBlock(_block);
                _block.SetFloat(Seed, index++ * 0.618f);
                _block.SetFloat(Thrust, 0f);
                r.SetPropertyBlock(_block);
                var push = -dir;
                var torque = Vector3.Cross(t.Position.ToVector3() - centre, push);
                _jets.Add(new Jet
                {
                    Def = t, Renderer = r, Push = push,
                    Turn = torque.sqrMagnitude > 1e-8f ? torque.normalized : Vector3.zero,
                    Lever = Mathf.Clamp01(torque.magnitude / reach * 1.5f),
                });
            }
            Debug.Log($"[Ship] {name}: {_jets.Count} jets ({engines})");
        }

        /// <summary>
        /// The flame mesh (shared): a unit tube along +Z (radius 1 at z 0, rings to z 1; uv = around, along) and a
        /// quad at the nozzle (uv2.x = 1, corners in uv) that the shader turns to face the camera.
        /// </summary>
        static Mesh Plume()
        {
            if (_plume != null) return _plume;
            const int sides = 16, rings = 12;
            var verts = new List<Vector3>();
            var uv = new List<Vector2>();
            var kind = new List<Vector2>();
            var tris = new List<int>();
            for (var k = 0; k <= rings; k++)
            {
                var z = (float)k / rings;
                z *= z * 0.35f + 0.65f;   // a little denser near the nozzle, where it changes most
                for (var s = 0; s <= sides; s++)
                {
                    var a = s / (float)sides * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), z));
                    uv.Add(new Vector2(s / (float)sides, z));
                    kind.Add(Vector2.zero);
                }
            }
            for (var k = 0; k < rings; k++)
            for (var s = 0; s < sides; s++)
            {
                int a = k * (sides + 1) + s, b = a + sides + 1;
                tris.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
            var h = verts.Count;
            foreach (var c in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
            {
                verts.Add(Vector3.zero);
                uv.Add(c);
                kind.Add(Vector2.right);
            }
            tris.AddRange(new[] { h, h + 2, h + 1, h, h + 3, h + 2 });
            _plume = new Mesh { name = "Plume" };
            _plume.SetVertices(verts);
            _plume.SetUVs(0, uv);
            _plume.SetUVs(1, kind);
            _plume.SetTriangles(tris, 0);
            // The shader stretches the tube (boost) and grows the halo: bounds wide enough for both.
            _plume.bounds = new Bounds(new Vector3(0f, 0f, 1f), new Vector3(8f, 8f, 4f));
            return _plume;
        }
    }
}
