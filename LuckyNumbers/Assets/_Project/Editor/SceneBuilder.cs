using LuckyNumbers.Core;
using LuckyNumbers.Space;
using LuckyNumbers.Intro;
using LuckyNumbers.Planet;
using LuckyNumbers.Title;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Builds the game scenes from code (reproducible, reviewable): tools/ln.py scenes.
    /// Look, text, menu and bodies come from content/*.txtpb at runtime; scenes only hold structure.
    /// </summary>
    public static class SceneBuilder
    {
        const string ScenesDir = "Assets/_Project/Scenes";
        const string MaterialsDir = "Assets/_Project/Art/Materials";
        const string UiDir = "Assets/_Project/UI";
        const string PanelSettingsPath = UiDir + "/PanelSettings.asset";

        [MenuItem("LuckyNumbers/Scenes/Rebuild All")]
        public static void BuildAll()
        {
            FontSetup.Build();
            var starfield = MaterialAsset("Starfield", "LuckyNumbers/Starfield");
            var planet = MaterialAsset("Planet3D", "LuckyNumbers/Planet3D");
            var star = MaterialAsset("Star", "LuckyNumbers/Star");
            var galaxy = MaterialAsset("Galaxy", "LuckyNumbers/Galaxy");
            var disk = MaterialAsset("Disk", "LuckyNumbers/Disk");

            var panel = PanelSettingsAsset();
            var rig = new Rig { Starfield = starfield, Planet = planet, Star = star, Galaxy = galaxy, Disk = disk, Panel = panel };
            BuildTitle(rig);
            BuildIntro(rig);
            BuildPlanet(rig);
            BuildStation(rig);
            BuildRange(rig);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene($"{ScenesDir}/Title.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Intro.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Planet.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Station.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Range.unity", true),
            };
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"{ScenesDir}/SampleScene.unity") != null)
                AssetDatabase.DeleteAsset($"{ScenesDir}/SampleScene.unity"); // URP template scene
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuilder] built Title, Intro, Planet, Station, Range; build settings = [Title, Intro, Planet, Station, Range]");
        }

        static void BuildTitle(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // UI Toolkit: UI/Screens/Title.uxml styled by UI/Theme; TitleView binds it ([Ui] fields, Roslyn).
            var ui = Document("UI", rig.Panel, "Title");
            SpaceRig(rig, ui);
            var view = new GameObject("TitleView").AddComponent<TitleView>();
            Assign(view, ("document", ui));
            var dice = new GameObject("DiceView").AddComponent<DiceView>();
            Assign(dice, ("document", ui));
            ShipRig();   // your fighter, for the hangar after character creation (ShipPickerPresenter)

            Scopes<TitleScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Title.unity");
        }

        static void BuildIntro(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Intro");
            SpaceRig(rig, ui);   // same universe as the title (IntroScope registers the intro backdrop)
            FreighterRig();      // the museum freighter that goes down
            var view = new GameObject("IntroView").AddComponent<IntroView>();
            Assign(view, ("document", ui));
            var dice = new GameObject("DiceView").AddComponent<DiceView>();
            Assign(dice, ("document", ui));
            Scopes<IntroScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Intro.unity");
        }

        const string ArmsDir = "Assets/_Project/Art/Characters/Arms";

        /// <summary>Crash site: ground, sun, wreck, first-person player with arms, overlay UI.</summary>
        static void BuildPlanet(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Sky(rig.Starfield);
            var ui = Document("UI", rig.Panel, "Planet");

            // Ground: endless chunks streamed at runtime.
            var ground = new GameObject("Ground").AddComponent<GroundView>();
            Assign(ground, ("material", MaterialAsset("Ground", "LuckyNumbers/Ground")));

            var (cam, _) = PlayerRig();

            // Sun: a star sphere far away (moved with the player at runtime).
            var sun = Sphere("Sun", rig.Star, null);
            sun.transform.localScale = Vector3.one * 60f;
            var view = new GameObject("PlanetView").AddComponent<PlanetView>();
            Assign(view, ("document", ui), ("sun", sun));

            // Map (M): a hex globe on its own layer, far from the world, with its own camera (off until opened).
            var mapLayer = LayerMask.NameToLayer("Map");
            if (mapLayer < 0) throw new System.Exception("layer Map missing (ProjectSettings/TagManager)");
            cam.cullingMask &= ~(1 << mapLayer);
            var mapGo = new GameObject("Map");
            mapGo.transform.position = new Vector3(0f, 5000f, 0f);
            var globe = new GameObject("Globe");
            globe.transform.SetParent(mapGo.transform, false);
            var globeFilter = globe.AddComponent<MeshFilter>();
            var globeRenderer = globe.AddComponent<MeshRenderer>();
            globeRenderer.sharedMaterial = MaterialAsset("HexMap", "LuckyNumbers/HexMap");
            globeRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var marker = MaterialAsset("MapMarker", "Universal Render Pipeline/Unlit");
            Transform Marker(string name, PrimitiveType type, Vector3 scale)
            {
                var m = GameObject.CreatePrimitive(type);
                m.name = name;
                Object.DestroyImmediate(m.GetComponent<Collider>());
                m.transform.SetParent(globe.transform, false);
                m.transform.localScale = scale;
                m.GetComponent<MeshRenderer>().sharedMaterial = marker;
                return m.transform;
            }
            var crashMarker = Marker("Crash", PrimitiveType.Cylinder, new Vector3(0.02f, 0.04f, 0.02f));
            var pinMarker = Marker("You", PrimitiveType.Sphere, Vector3.one * 0.022f);
            // The system's star (its look set at runtime) and a moon template (Planet3D, copied per moon).
            var mapStar = Sphere("Star", rig.Star, mapGo.transform);
            var moonTemplate = Sphere("MoonTemplate", rig.Planet, mapGo.transform);
            moonTemplate.gameObject.SetActive(false);
            var mapCamGo = new GameObject("MapCamera");
            mapCamGo.transform.SetParent(mapGo.transform, false);
            mapCamGo.transform.localPosition = new Vector3(0f, 0f, -3f);
            var mapCam = mapCamGo.AddComponent<Camera>();
            mapCam.clearFlags = CameraClearFlags.Skybox;   // your universe's sky behind the globe
            mapCam.cullingMask = 1 << mapLayer;
            mapCam.fieldOfView = 40f;
            mapCam.nearClipPlane = 0.05f;
            mapCam.farClipPlane = 400f;                   // out to the star
            ArmsView.SetLayer(mapGo.transform, mapLayer);
            var mapView = mapGo.AddComponent<MapView>();
            Assign(mapView, ("mapCamera", mapCam), ("globe", globe.transform), ("globeMesh", globeFilter), ("globeRenderer", globeRenderer),
                   ("crash", crashMarker), ("pin", pinMarker), ("star", mapStar),
                   ("moonTemplate", moonTemplate));
            mapCamGo.SetActive(false);

            var wreck = FreighterRig(collide: true);   // the wreck: the museum freighter, solid, so you walk around (and onto) it
            var fighter = ShipRig(collide: true, name: "Fighter");   // your ship: you fly it in and land it (solid once parked)
            // Chase camera for flying in (off until then).
            var flightGo = new GameObject("FlightCamera");
            var flightCam = flightGo.AddComponent<Camera>();
            flightCam.clearFlags = CameraClearFlags.Skybox;
            flightCam.fieldOfView = 65f;
            flightCam.nearClipPlane = 0.1f;
            flightCam.farClipPlane = 3000f;
            flightCam.cullingMask = cam.cullingMask;   // the world, not the arms or the map
            flightGo.SetActive(false);
            // Beacon over the wreck (shown while you fly in): built here, not at runtime, so the build never needs a
            // primitive's collider type it may have stripped.
            var beaconMat = MaterialAsset("Beacon", "Universal Render Pipeline/Unlit");
            beaconMat.color = new Color(1f, 0.55f, 0.15f, 1f);
            EditorUtility.SetDirty(beaconMat);
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beam.name = "WreckBeacon";
            Object.DestroyImmediate(beam.GetComponent<Collider>());
            beam.GetComponent<MeshRenderer>().sharedMaterial = beaconMat;
            beam.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            beam.transform.localScale = new Vector3(1.6f, 160f, 1.6f);   // 320 m tall
            beam.SetActive(false);
            // Landing pad (flight assist): a flat green ring the presenter puts on the flattest spot near the wreck.
            var padMat = MaterialAsset("LandingPad", "Universal Render Pipeline/Unlit");
            padMat.color = new Color(0.27f, 0.75f, 0.35f, 1f);
            EditorUtility.SetDirty(padMat);
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pad.name = "LandingPad";
            Object.DestroyImmediate(pad.GetComponent<Collider>());
            pad.GetComponent<MeshRenderer>().sharedMaterial = padMat;
            pad.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pad.transform.localScale = new Vector3(22f, 0.04f, 22f);   // a 22 m disc, flat on the ground (fits the 24 m fighter)
            pad.SetActive(false);
            // Relic shapes (copied per relic at the crash site, coloured per relic): an amulet (a jade disc on a cord
            // ring) and a vase (round body, neck, lip). No colliders: you pick them up by walking close.
            var relicMat = MaterialAsset("Relic", "LuckyNumbers/Relic");
            var relicRoot = new GameObject("RelicTemplates").transform;
            Transform Part(Transform parent, PrimitiveType type, Vector3 position, Vector3 scale, Vector3 euler = default)
            {
                var go = GameObject.CreatePrimitive(type);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.transform.SetParent(parent, false);
                go.transform.localPosition = position;
                go.transform.localScale = scale;
                go.transform.localEulerAngles = euler;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = relicMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                return go.transform;
            }
            var amulet = new GameObject("Amulet").transform;
            amulet.SetParent(relicRoot, false);
            Part(amulet, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.34f, 0.025f, 0.34f), new Vector3(90f, 0f, 0f));      // the disc
            Part(amulet, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.012f), new Vector3(0.12f, 0.02f, 0.12f), new Vector3(90f, 0f, 0f)); // boss
            Part(amulet, PrimitiveType.Sphere, new Vector3(0f, 0.2f, 0f), new Vector3(0.06f, 0.06f, 0.06f));                   // cord bead
            var vase = new GameObject("Vase").transform;
            vase.SetParent(relicRoot, false);
            Part(vase, PrimitiveType.Sphere, new Vector3(0f, 0.2f, 0f), new Vector3(0.38f, 0.42f, 0.38f));                      // body
            Part(vase, PrimitiveType.Cylinder, new Vector3(0f, 0.46f, 0f), new Vector3(0.14f, 0.1f, 0.14f));                    // neck
            Part(vase, PrimitiveType.Cylinder, new Vector3(0f, 0.57f, 0f), new Vector3(0.22f, 0.02f, 0.22f));                   // lip
            var relicBeam = Part(relicRoot, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.18f, 30f, 0.18f));              // 60 m tall
            relicBeam.name = "RelicBeam";
            relicBeam.GetComponent<MeshRenderer>().sharedMaterial = beaconMat;
            amulet.gameObject.SetActive(false);
            vase.gameObject.SetActive(false);
            relicBeam.gameObject.SetActive(false);
            var artifacts = new[] { ArtifactModel(relicRoot, "water_amulet", 3f) };
            Assign(view, ("wreck", wreck), ("fighter", fighter), ("flightCamera", flightCam), ("wreckBeacon", beam.transform),
                   ("landingPad", pad.transform), ("amuletTemplate", amulet), ("vaseTemplate", vase), ("relicBeamTemplate", relicBeam));
            var vso = new SerializedObject(view);
            var models = vso.FindProperty("artifactModels");
            models.arraySize = artifacts.Length;
            for (var i = 0; i < artifacts.Length; i++) models.GetArrayElementAtIndex(i).objectReferenceValue = artifacts[i];
            vso.ApplyModifiedPropertiesWithoutUndo();
            Scopes<PlanetScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Planet.unity");
        }

        /// <summary>
        /// First-person player: body (CharacterController) > head > eyes (camera) > arms pivot > arms rig, with the
        /// arms on the Viewmodel layer drawn by an overlay camera stacked on the eyes. Returns both cameras.
        /// </summary>
        static (UnityEngine.Camera eyes, UnityEngine.Camera viewmodel) PlayerRig()
        {
            // Player: body (CharacterController) > head > camera > arms pivot > arms rig (eye at the rig origin).
            var playerGo = new GameObject("Player");
            var cc = playerGo.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.28f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.4f;
            cc.slopeLimit = 50f;
            var head = new GameObject("Head").transform;
            head.SetParent(playerGo.transform, false);
            head.localPosition = new Vector3(0f, 1.7f, 0f);
            var camGo = new GameObject("Eyes");
            camGo.transform.SetParent(head, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.02f;
            cam.farClipPlane = 3000f;
            camGo.tag = "MainCamera";
            // Viewmodel: the arms (and what they hold) on their own layer, drawn by an overlay camera after the
            // world with a cleared depth, so they never clip into walls or the wreck. Same FOV: IK aims in world space.
            var viewmodel = LayerMask.NameToLayer("Viewmodel");
            if (viewmodel < 0) throw new System.Exception("layer Viewmodel missing (ProjectSettings/TagManager)");
            cam.cullingMask &= ~(1 << viewmodel);
            var vmGo = new GameObject("ViewmodelEyes");
            vmGo.transform.SetParent(camGo.transform, false);
            var vm = vmGo.AddComponent<Camera>();
            vm.fieldOfView = cam.fieldOfView;
            vm.nearClipPlane = 0.01f;
            vm.farClipPlane = 5f;
            vm.cullingMask = 1 << viewmodel;
            vm.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            cam.GetUniversalAdditionalCameraData().cameraStack.Add(vm);
            var player = playerGo.AddComponent<PlayerView>();
            Assign(player, ("body", cc), ("head", head), ("eyes", cam));

            var pivot = new GameObject("ArmsPivot").transform;
            pivot.SetParent(camGo.transform, false);
            pivot.localRotation = Quaternion.Euler(-10f, 0f, 0f);   // the rig is framed for a slightly lowered gaze
            var armsModel = AssetDatabase.LoadAssetAtPath<GameObject>($"{ArmsDir}/arms_rig.fbx")
                            ?? throw new System.Exception("arms_rig.fbx missing");
            var arms = (GameObject)PrefabUtility.InstantiatePrefab(armsModel);
            arms.transform.SetParent(pivot, false);
            arms.transform.localPosition = new Vector3(0f, -1.66f, 0.02f);
            foreach (var c in arms.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(c.gameObject); // the rig's own camera
            var armsMat = MaterialAsset("Arms", "LuckyNumbers/Ship");
            armsMat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArmsDir}/arms_gloves_01.png");
            armsMat.SetFloat("_Glow", 0f);
            armsMat.SetFloat("_Rim", 0.15f);
            EditorUtility.SetDirty(armsMat);
            ArmsView.SetLayer(arms.transform, viewmodel);
            foreach (var r in arms.GetComponentsInChildren<Renderer>())
            {
                r.sharedMaterial = armsMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (r is SkinnedMeshRenderer s) s.updateWhenOffscreen = true;
            }
            var animator = arms.GetComponent<Animator>();
            if (animator == null) animator = arms.AddComponent<Animator>();   // Unity null: no ?? on components
            if (animator.avatar == null)
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath($"{ArmsDir}/arms_rig.fbx"))
                    if (o is Avatar avatar) animator.avatar = avatar;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var clips = new System.Collections.Generic.List<AnimationClip>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath($"{ArmsDir}/arms_rig.fbx"))
                if (o is AnimationClip clip && !clip.name.StartsWith("__preview")) clips.Add(clip);
            var armsView = arms.AddComponent<ArmsView>();
            var so = new SerializedObject(armsView);
            so.FindProperty("pivot").objectReferenceValue = pivot;
            so.FindProperty("animator").objectReferenceValue = animator;
            var cl = so.FindProperty("clips");
            cl.arraySize = clips.Count;
            for (var i = 0; i < clips.Count; i++) cl.GetArrayElementAtIndex(i).objectReferenceValue = clips[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            return (cam, vm);
        }

        const string KitsDir = "Assets/_Project/Art/Models";

        /// <summary>
        /// The player's station: the universe (SpaceRig, its camera the base of the stack) seen from orbit, and the
        /// station + first-person player drawn over it by overlay cameras (eyes, then arms). StationPresenter turns
        /// the space camera with the player's head, so windows look out at the real system.
        /// </summary>
        static void BuildStation(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Planet");   // fade, hint, crosshair, dialogue box (for the call)
            SpaceRig(rig, ui);
            var spaceCam = Object.FindAnyObjectByType<UnityEngine.Camera>();

            var (eyes, viewmodel) = PlayerRig();
            eyes.tag = "Untagged";                          // the space camera is the main (base) camera
            eyes.farClipPlane = 200f;                       // the station only; space draws underneath
            // Depth precision goes with far / near: 5 cm (walls never come closer; the arms have their own camera)
            // keeps millimetre offsets (posts proud of walls) resolved out to the far end of the big rooms.
            eyes.nearClipPlane = 0.05f;
            var eyesData = eyes.GetUniversalAdditionalCameraData();
            eyesData.cameraStack.Clear();
            eyesData.renderType = CameraRenderType.Overlay;
            var spaceData = spaceCam.GetUniversalAdditionalCameraData();
            spaceData.cameraStack.Add(eyes);
            spaceData.cameraStack.Add(viewmodel);
            spaceCam.fieldOfView = eyes.fieldOfView;

            var view = new GameObject("PlanetView").AddComponent<PlanetView>();
            Assign(view, ("document", ui));

            var station = new GameObject("Station").AddComponent<LuckyNumbers.Station.StationView>();
            station.Init(KitMaterial("CombinedKit"), MaterialAsset("StationGlass", "LuckyNumbers/StationGlass"),
                         KitPieces("StationKit", "StarshipKit"));
            EditorUtility.SetDirty(station);

            // Your ship, docked in the bay (StationPresenter shows and poses it); solid, so you walk round it.
            ShipRig(collide: true);

            // A test gun by the spawn (StationPresenter drops it there): pick up with E, click fires energy bolts.
            WeaponRig(StationWeapon);

            // Lamp glow: bloom on the station camera (its strength set from content by StationPresenter).
            eyes.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var volume = new GameObject("Glow").AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = GlowProfile();

            Scopes<LuckyNumbers.Station.StationScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Station.unity");
        }

        /// <summary>The station's bloom profile (an asset): lamps above 1.0 glow; strength comes from content.</summary>
        static UnityEngine.Rendering.VolumeProfile GlowProfile()
        {
            const string path = "Assets/_Project/Settings/StationGlow.asset";
            var profile = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            if (!profile.TryGet<Bloom>(out var bloom)) bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.02f);
            bloom.intensity.Override(0.8f);
            bloom.scatter.Override(0.6f);
            bloom.highQualityFiltering.Override(false);   // cheaper on the Web
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            return profile;
        }

        /// <summary>One material per kit (its atlas, StationKit shader), saved as an asset.</summary>
        public static Material KitMaterial(string kit)
        {
            var mat = MaterialAsset(kit, "LuckyNumbers/StationKit");
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{KitsDir}/{kit}/atlas.png")
                              ?? throw new System.Exception($"{kit}/atlas.png not imported");
            mat.SetFloat("_FogStart", 16f);   // big rooms (the gallery is 48 m long): fade late
            mat.SetFloat("_FogEnd", 70f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Every model in the kits' folders, by file name (the names StationPlan places).</summary>
        public static LuckyNumbers.Station.StationView.KitPiece[] KitPieces(params string[] kits)
        {
            var list = new System.Collections.Generic.List<LuckyNumbers.Station.StationView.KitPiece>();
            foreach (var kit in kits)
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { $"{KitsDir}/{kit}" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                list.Add(new LuckyNumbers.Station.StationView.KitPiece
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(path),
                    model = AssetDatabase.LoadAssetAtPath<GameObject>(path),
                    kit = kit == "StationKit" ? 0 : 1,   // which band of the combined atlas its UVs need
                });
            }
            return list.ToArray();
        }

        const string RelicsDir = "Assets/_Project/Art/Models/Relics";

        /// <summary>
        /// A named artifact's own model (Art/Models/Relics/{name}.fbx), off, under `parent`: its parts get relic
        /// materials by sub-mesh order: gold metal, the main gem in water blue, the crest gem pale; chains gold.
        /// Scaled up so a 13 cm amulet reads on the ground.
        /// </summary>
        static Transform ArtifactModel(Transform parent, string name, float scale)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{RelicsDir}/{name}.fbx")
                      ?? throw new System.Exception($"{name}.fbx missing in {RelicsDir}");
            Material Mat(string asset, Color a, Color b, float glow)
            {
                var m = MaterialAsset(asset, "LuckyNumbers/Relic");
                m.SetColor("_ColorA", a);
                m.SetColor("_ColorB", b);
                m.SetFloat("_Glow", glow);
                EditorUtility.SetDirty(m);
                return m;
            }
            var gold = Mat("RelicGold", new Color(0.62f, 0.44f, 0.12f), new Color(0.95f, 0.78f, 0.35f), 0.35f);
            var gem = Mat("RelicWaterGem", new Color(0.08f, 0.32f, 0.62f), new Color(0.35f, 0.78f, 0.98f), 1.2f);
            var crest = Mat("RelicCrestGem", new Color(0.55f, 0.85f, 0.9f), new Color(0.9f, 1f, 1f), 0.9f);
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            model.transform.SetParent(root, false);
            model.transform.localScale = Vector3.one * scale;
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>())
            {
                var count = r.GetComponent<MeshFilter>().sharedMesh.subMeshCount;
                var mats = new Material[count];
                for (var i = 0; i < count; i++) mats[i] = count == 1 ? gold : i == 0 ? gold : i == 1 ? gem : crest;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            root.gameObject.SetActive(false);
            return root;
        }

        /// <summary>A two-tone material on the Relic shader (_ColorA low, _ColorB high, rim glow), saved as an asset.</summary>
        static Material Tint(string asset, Color a, Color b, float glow, float pulse)
        {
            var m = MaterialAsset(asset, "LuckyNumbers/Relic");
            m.SetColor("_ColorA", a);
            m.SetColor("_ColorB", b);
            m.SetFloat("_Glow", glow);
            m.SetFloat("_Pulse", pulse);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Art/Models/Weapons/weapons.json (written by tools/blender/export_weapons.py): what each model is.</summary>
        [System.Serializable] sealed class WeaponIndex { public WeaponItem[] items; }
        [System.Serializable] sealed class WeaponItem
        {
            public string name;
            public string kind;          // gun, melee or mod
            public string[] submeshes;   // material per submesh, in order: metal, darker metal, lights (unused dropped)
            public WeaponHold hold;      // guns: fitted to the arms by tools/blender/weapon_holds.py (absent: measured here)
            public WeaponGrasp grasp;    // finger grips baked by tools/ln.py bake-grasps (absent: solved live on pickup)
        }
        [System.Serializable] sealed class WeaponGrasp { public LuckyNumbers.Hands.GraspData left, right; }
        static LuckyNumbers.Hands.GraspData Baked(LuckyNumbers.Hands.GraspData g) => g != null && g.rotations != null && g.rotations.Length > 0 ? g : null;
        [System.Serializable] sealed class WeaponHold
        {
            public float[] grip, foregrip, muzzle, offset, arms_shift;   // model space; offset and arms_shift: eye space
            public float[] grip_palm, grip_along, fore_palm, fore_along;  // the hands' directions (model space)
            public float scale;
        }
        static Vector3 V3(float[] a) => new(a[0], a[1], a[2]);

        static WeaponItem[] Weapons() =>
            JsonUtility.FromJson<WeaponIndex>(System.IO.File.ReadAllText($"{WeaponsDir}/weapons.json")).items;

        static WeaponItem Weapon(string name) =>
            System.Array.Find(Weapons(), w => w.name == name) ?? throw new System.Exception($"{name} not in {WeaponsDir}/weapons.json");

        const string RangeFirstGun = "rifle_block";   // the gun you spawn in front of

        /// <summary>
        /// Dev shooting range (GameScene.Range): a closed hall, every gun on a bench in front of you, the melee weapons
        /// and loose mods on a table to your left, crates to knock over and target boards downrange (+Z).
        /// RangePresenter runs it; the debug inspector or ?ln-range opens it.
        /// </summary>
        static void BuildRange(Rig rig)
        {
            // Guns in export order, but the one you spawn at moved to the middle of the bench.
            var gunList = new System.Collections.Generic.List<WeaponItem>(System.Array.FindAll(Weapons(), w => w.kind == "gun"));
            var firstGun = gunList.Find(w => w.name == RangeFirstGun);
            if (firstGun != null) { gunList.Remove(firstGun); gunList.Insert(gunList.Count / 2, firstGun); }
            var guns = gunList.ToArray();
            var others = System.Array.FindAll(Weapons(), w => w.kind != "gun");
            float GunX(int i) => (i - (guns.Length - 1) / 2f) * 1.12f;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Planet");   // hint, crosshair
            var (eyes, _) = PlayerRig();
            eyes.clearFlags = CameraClearFlags.SolidColor;
            eyes.backgroundColor = new Color(0.03f, 0.035f, 0.045f);
            eyes.farClipPlane = 120f;
            var first = System.Math.Max(0, System.Array.FindIndex(guns, w => w.name == RangeFirstGun));
            eyes.transform.root.position = new Vector3(GunX(first), 0f, -1.2f);   // behind the bench, facing downrange
            var view = new GameObject("PlanetView").AddComponent<PlanetView>();
            Assign(view, ("document", ui));

            var floor = Tint("RangeFloor", new Color(0.16f, 0.17f, 0.19f), new Color(0.22f, 0.23f, 0.26f), 0f, 0f);
            var wall = Tint("RangeWall", new Color(0.3f, 0.31f, 0.33f), new Color(0.55f, 0.56f, 0.58f), 0.05f, 0f);
            var bench = Tint("RangeBench", new Color(0.25f, 0.18f, 0.12f), new Color(0.42f, 0.31f, 0.2f), 0f, 0f);
            var crate = Tint("RangeCrate", new Color(0.55f, 0.36f, 0.12f), new Color(0.85f, 0.6f, 0.25f), 0.1f, 0f);
            var board = Tint("RangeTarget", new Color(0.75f, 0.2f, 0.15f), new Color(0.95f, 0.9f, 0.85f), 0.2f, 0f);

            var hall = new GameObject("Range").transform;
            Transform Box(string name, Vector3 centre, Vector3 size, Material m)
            {
                var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
                b.name = name;
                b.transform.SetParent(hall, false);
                b.transform.localPosition = centre;
                b.transform.localScale = size;
                var r = b.GetComponent<MeshRenderer>();
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                return b.transform;
            }
            const float width = 12f, height = 4.5f, back = -4f, front = 32f;
            var mid = (back + front) / 2f;
            Box("Floor", new Vector3(0f, -0.25f, mid), new Vector3(width, 0.5f, front - back), floor);
            Box("Ceiling", new Vector3(0f, height + 0.25f, mid), new Vector3(width, 0.5f, front - back), wall);
            Box("Wall L", new Vector3(-width / 2f - 0.25f, height / 2f, mid), new Vector3(0.5f, height, front - back), wall);
            Box("Wall R", new Vector3(width / 2f + 0.25f, height / 2f, mid), new Vector3(0.5f, height, front - back), wall);
            Box("Wall back", new Vector3(0f, height / 2f, back - 0.25f), new Vector3(width, height, 0.5f), wall);
            Box("Wall front", new Vector3(0f, height / 2f, front + 0.25f), new Vector3(width, height, 0.5f), wall);
            Box("Bench", new Vector3(0f, 0.4f, 0.4f), new Vector3(11.5f, 0.8f, 0.7f), bench);

            // Every gun on the bench, barrels downrange, dropped a hair above it (they settle with physics).
            for (var i = 0; i < guns.Length; i++)
            {
                var gun = WeaponRig(guns[i].name);
                gun.transform.position = new Vector3(GunX(i), 1.15f, 0.4f);
                gun.gameObject.SetActive(true);
            }
            // Melee weapons and loose mods (carryable, no firing yet) on a table along the left wall.
            Box("Table", new Vector3(-5.3f, 0.4f, -1.8f), new Vector3(0.8f, 0.8f, 3.6f), bench);
            for (var i = 0; i < others.Length; i++)
            {
                var item = WeaponRig(others[i].name);
                item.transform.SetPositionAndRotation(new Vector3(-5.3f, 1.1f, -3.3f + i * 0.5f), Quaternion.Euler(0f, 90f, 0f));
                item.SetActive(true);
            }

            // Crates to knock about (loose: bolts push them), then fixed boards at the far end.
            void Crate(Vector3 at, float size)
            {
                var c = Box("Crate", at, Vector3.one * size, crate);
                var body = c.gameObject.AddComponent<Rigidbody>();
                body.mass = 4f * size;
                body.interpolation = RigidbodyInterpolation.Interpolate;
            }
            for (var i = 0; i < 3; i++)
            {
                Crate(new Vector3(-3f + i * 0.62f, 0.3f, 9f), 0.6f);
                Crate(new Vector3(-2.7f + i * 0.62f, 0.9f, 9f), 0.6f);
                Crate(new Vector3(2f + i * 0.45f, 0.22f, 15f), 0.44f);
            }
            for (var i = 0; i < 3; i++)
            {
                var x = (i - 1) * 3.5f;
                Box("Target post", new Vector3(x, 0.6f, 28f), new Vector3(0.12f, 1.2f, 0.12f), wall);
                Box("Target", new Vector3(x, 1.6f, 28f), new Vector3(1.2f, 1.2f, 0.08f), board);
            }

            Scopes<RangeScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Range.unity");
        }

        const string WeaponsDir = "Assets/_Project/Art/Models/Weapons";
        /// <summary>Which of Art/Models/Weapons lies in the station (any of the guns: their hold points are measured).</summary>
        const string StationWeapon = "rifle_block";

        /// <summary>
        /// A model from Art/Models/Weapons: a root (+Z down the barrel, carryable: Rigidbody + box) holding the model,
        /// coloured per submesh from weapons.json. Guns also get a WeaponView with hold points measured from the mesh:
        /// the grip (the low part behind the middle, where the pistol grip hangs), the foregrip (under the handguard
        /// ahead of it) and the muzzle (the barrel's end, +Z as exported). Melee weapons and mods are just carryable.
        /// </summary>
        static GameObject WeaponRig(string name)
        {
            var item = Weapon(name);
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{WeaponsDir}/{name}.fbx")
                      ?? throw new System.Exception($"{name}.fbx missing in {WeaponsDir}");
            var Mat = (System.Func<string, Color, Color, float, float, Material>)Tint;
            var metal = Mat("WeaponMetal", new Color(0.42f, 0.44f, 0.47f), new Color(0.72f, 0.74f, 0.76f), 0.12f, 0f);
            var dark = Mat("WeaponDarkMetal", new Color(0.12f, 0.13f, 0.15f), new Color(0.24f, 0.25f, 0.28f), 0.08f, 0f);
            var lights = Mat("WeaponLights", new Color(0.2f, 1.2f, 1.8f), new Color(0.4f, 1.8f, 2.6f), 0.6f, 0.5f);
            var bolt = Mat("WeaponBolt", new Color(0.35f, 1.6f, 2.6f), new Color(0.35f, 1.6f, 2.6f), 1.5f, 0f);

            var root = new GameObject($"Weapon {name}");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            model.transform.SetParent(root.transform, false);

            // Points over the surface in the root's space (the model keeps its import rotation), dense enough
            // (every cm) that big flat faces, like a grip made of one quad, still have points down to their edges.
            var verts = new System.Collections.Generic.List<Vector3>();
            foreach (var f in model.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = f.sharedMesh;
                var vs = mesh.vertices;
                for (var i = 0; i < vs.Length; i++) vs[i] = root.transform.InverseTransformPoint(f.transform.TransformPoint(vs[i]));
                var tris = mesh.triangles;
                for (var i = 0; i < tris.Length; i += 3)
                {
                    Vector3 a = vs[tris[i]], ab = vs[tris[i + 1]] - a, ac = vs[tris[i + 2]] - a;
                    var n = Mathf.Clamp((int)(Mathf.Max(ab.magnitude, ac.magnitude, (ab - ac).magnitude) / 0.01f), 1, 12);
                    for (var u = 0; u <= n; u++)
                        for (var w = 0; w <= n - u; w++) verts.Add(a + ab * ((float)u / n) + ac * ((float)w / n));
                }
                var r = f.GetComponent<MeshRenderer>();
                var count = f.sharedMesh.subMeshCount;
                if (count != item.submeshes.Length)
                    Debug.LogWarning($"[SceneBuilder] {name}: {count} submeshes, weapons.json lists {item.submeshes.Length}");
                var mats = new Material[count];
                for (var i = 0; i < count; i++)
                    mats[i] = i >= item.submeshes.Length ? metal : item.submeshes[i] switch { "darker metal" => dark, "lights" => lights, _ => metal };
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            // tools/blender/export_weapons.py points every barrel down Blender -Y, which imports as +Z: already aligned.
            // (Guessing from the shape doesn't work: some stocks are no taller than the muzzle end.)
            var b = new Bounds(verts[0], Vector3.zero);
            foreach (var v in verts) b.Encapsulate(v);

            var length = b.size.z;
            Vector3 Mean(System.Func<Vector3, bool> where, Vector3 fallback)
            {
                var sum = Vector3.zero;
                var n = 0;
                foreach (var v in verts) if (where(v)) { sum += v; n++; }
                return n > 0 ? sum / n : fallback;
            }
            // Grip: the slice behind the middle (clear of the stock's toe) that hangs lowest: the pistol grip. The palm
            // closes a few centimetres up from its bottom.
            float SliceLow(float z)
            {
                var lo = float.MaxValue;
                foreach (var v in verts) if (Mathf.Abs(v.z - z) < length * 0.015f) lo = Mathf.Min(lo, v.y);
                return lo;
            }
            float gz = b.center.z - length * 0.1f, gLow = float.MaxValue;
            for (var z = b.min.z + length * 0.2f; z <= b.center.z; z += length * 0.01f)
            {
                var lo = SliceLow(z);
                if (lo < gLow) { gLow = lo; gz = z; }
            }
            if (gLow == float.MaxValue) gLow = b.min.y;
            var gx = Mean(v => Mathf.Abs(v.z - gz) < length * 0.015f && v.y < gLow + 0.05f, new Vector3(0f, gLow, gz)).x;
            var grip = new Vector3(gx, gLow + 0.04f, gz);
            // Foregrip: under the handguard a fifth of the way ahead of the middle, but no more than a short
            // hand span ahead of the grip: the arms reach 0.56 m, and a real rifle's foregrip is out of reach.
            const float maxSpan = 0.2f;
            var zf = Mathf.Min(b.center.z + length * 0.22f, grip.z + maxSpan);
            var slice = Mean(v => Mathf.Abs(v.z - zf) < length * 0.04f, new Vector3(0f, b.min.y, zf));
            var low = float.MaxValue;
            foreach (var v in verts) if (Mathf.Abs(v.z - zf) < length * 0.04f) low = Mathf.Min(low, v.y);
            var foregrip = new Vector3(slice.x, low < float.MaxValue ? low : b.min.y, zf);
            var muzzleEnd = Mean(v => v.z > b.max.z - length * 0.03f, new Vector3(0f, b.center.y, b.max.z));
            var muzzle = new Vector3(muzzleEnd.x, muzzleEnd.y, b.max.z + 0.01f);

            var box = root.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;
            // Its exact shape, for the fingers to close onto while it's held (FirstPersonPresenter switches it on, on the
            // arms' layer, which collides with nothing else). Off otherwise: a dynamic body can't have a concave collider.
            if (item.kind != "mod")
                foreach (var f in model.GetComponentsInChildren<MeshFilter>())
                {
                    var grasp = f.gameObject.AddComponent<MeshCollider>();
                    grasp.sharedMesh = f.sharedMesh;
                    grasp.enabled = false;
                }
            var body = root.AddComponent<Rigidbody>();   // a rigidbody marks it as something you can carry
            body.mass = item.kind == "gun" ? 3.5f : item.kind == "melee" ? 1.5f : 0.4f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            root.SetActive(false);   // placed by whoever builds the scene (StationPresenter, BuildRange)
            if (item.kind == "melee")
            {
                // Handle down, blade / head up (+Y as exported). The fist closes a little way up the handle (above the
                // pommel, or the hammer's spike and cap); the tip is the top slice's middle (the hammer: its head's).
                var hammer = name == "hammer";
                var h = b.size.y;
                var gy = b.min.y + h * (hammer ? 0.25f : 0.17f);
                var handle = Mean(v => Mathf.Abs(v.y - gy) < h * 0.03f, new Vector3(b.center.x, gy, b.center.z));
                var top = Mean(v => v.y > b.max.y - h * (hammer ? 0.2f : 0.04f), new Vector3(b.center.x, b.max.y, b.center.z));
                var tipPoint = new Vector3(top.x, hammer ? top.y : b.max.y, top.z);
                // Edge, from looking at the models (measuring fails: the knife's tip curls toward its spine, so the
                // spine side bulges further): the knife cuts with its convex side, -X as exported; the hammer strikes
                // with a face of its head (its long axis, Z).
                var edgeDir = hammer ? Vector3.forward : Vector3.left;
                var melee = root.AddComponent<MeleeView>();
                melee.Init(new Vector3(handle.x, gy, handle.z), tipPoint, edgeDir,
                           swingSeconds: hammer ? 0.75f : 0.4f, radius: hammer ? 0.1f : 0.04f, impulse: hammer ? 9f : 2.5f);
                melee.InitGrips(null, Baked(item.grasp?.right));
                EditorUtility.SetDirty(melee);
                Debug.Log($"[SceneBuilder] melee {name}: grip ({handle.x:0.00}, {gy:0.00}, {handle.z:0.00}), tip {tipPoint}, edge {edgeDir}");
                return root;
            }
            if (item.kind != "gun")
            {
                Debug.Log($"[SceneBuilder] {item.kind} {name}: {b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00} m");
                return root;
            }
            var view = root.AddComponent<WeaponView>();
            var fit = item.hold != null && item.hold.grip != null && item.hold.grip.Length == 3 ? item.hold : null;
            if (fit != null)
            {
                view.Init(V3(fit.grip), V3(fit.foregrip), V3(fit.muzzle), bolt, V3(fit.offset), fit.scale, V3(fit.arms_shift));
                if (fit.grip_palm != null && fit.grip_palm.Length == 3)
                    view.InitHands(V3(fit.grip_palm), V3(fit.grip_along), V3(fit.fore_palm), V3(fit.fore_along));
            }
            else
                view.Init(grip, foregrip, muzzle, bolt);
            view.InitGrips(Baked(item.grasp?.left), Baked(item.grasp?.right));
            EditorUtility.SetDirty(view);
            Debug.Log(fit != null
                ? $"[SceneBuilder] gun {name}: {length:0.00} m, fitted hold: grip {V3(fit.grip)} at {V3(fit.offset)}, scale {fit.scale:0.00}, arms shift {V3(fit.arms_shift)}, " +
                  $"grips {(Baked(item.grasp?.left) != null && Baked(item.grasp?.right) != null ? "baked" : "not baked (solved live)")}"
                : $"[SceneBuilder] gun {name}: {length:0.00} m, measured: grip {grip}, foregrip {foregrip}, muzzle {muzzle} (run weapon_holds.py)");
            return root;
        }

        const string FighterDir = "Assets/_Project/Art/Models/Fighter";

        /// <summary>Ships keep their painted colours: true albedo (no lifted floor), a little ambient, a faint camera fill.</summary>
        internal static void ShipLook(Material m)
        {
            m.SetFloat("_Ambient", 0.45f);   // x the sky / ground tint on a planet: the shadow side still reads
            m.SetFloat("_Fill", 0.3f);
            m.SetFloat("_Lift", 0f);
            m.SetFloat("_Spec", 0.6f);       // painted metal catches the sun
            m.SetFloat("_SpecPower", 28f);
        }

        /// <summary>The fighter: pivot (+Z forward) around the model (nose is +X in the file), all skins listed.</summary>
        internal static ShipView ShipRig(bool collide = false, string name = "Ship")
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{FighterDir}/fighter1.fbx")
                        ?? throw new System.Exception("fighter1.fbx missing");
            var material = MaterialAsset("Ship", "LuckyNumbers/Ship");
            ShipLook(material);
            EditorUtility.SetDirty(material);
            var view = new GameObject(name).AddComponent<ShipView>();
            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(view.transform, false);
            var ship = (GameObject)PrefabUtility.InstantiatePrefab(model);
            ship.transform.SetParent(pivot, false);
            ship.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            var renderers = ship.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // Collider baked here: imported meshes aren't CPU-readable at runtime (ArtImport).
                if (collide && r.TryGetComponent<MeshFilter>(out var mf))
                {
                    var c = r.gameObject.AddComponent<MeshCollider>();   // convex: no hollows to get stuck in
                    c.sharedMesh = mf.sharedMesh;
                    c.convex = true;
                }
            }
            var skins = new System.Collections.Generic.List<Texture2D>();
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { $"{FighterDir}/Skins" }))
                skins.Add(AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid)));
            skins.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            var so = new SerializedObject(view);
            so.FindProperty("pivot").objectReferenceValue = pivot;
            var rs = so.FindProperty("renderers");
            rs.arraySize = renderers.Length;
            for (var i = 0; i < renderers.Length; i++) rs.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            var sk = so.FindProperty("skins");
            sk.arraySize = skins.Count;
            for (var i = 0; i < skins.Count; i++) sk.GetArrayElementAtIndex(i).objectReferenceValue = skins[i];
            // Engine flames: the fighter's jet layout (content/engines.txtpb), built by ShipView at runtime.
            so.FindProperty("engines").stringValue = "fighter";
            so.FindProperty("exhaust").objectReferenceValue = ExhaustMaterial();
            so.FindProperty("coldGas").objectReferenceValue = ColdGasMaterial();
            so.ApplyModifiedPropertiesWithoutUndo();
            pivot.gameObject.SetActive(false);
            return view;
        }

        /// <summary>The engine flames' base material (ShipView copies it per look: mains, manoeuvring jets).</summary>
        static Material ExhaustMaterial()
        {
            var m = MaterialAsset("Exhaust", "LuckyNumbers/Exhaust");
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The cold-gas puffs' base material (ShipView copies it and applies engines.txtpb's gas look).</summary>
        static Material ColdGasMaterial()
        {
            var m = MaterialAsset("ColdGas", "LuckyNumbers/ColdGas");
            EditorUtility.SetDirty(m);
            return m;
        }

        const string FreighterDir = "Assets/_Project/Art/Models/CargoShip";
        // Alpha BLEND in the source glTF (decals): alpha-tested here. Every part is double-sided.
        static readonly string[] FreighterDecals = { "mat_0001", "mat_0016" };

        /// <summary>
        /// The museum freighter (CC BY, see CREDITS): one part per material, named after it (mat_0005 uses
        /// Textures/tex_0005), its own look kept (no skins). Pivot +Z forward; length measured from the model.
        /// With `collide`: the convex pieces of cargo_ship_collision.obj (tools/collision/decompose.py).
        /// </summary>
        internal static ShipView FreighterRig(bool collide = false)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{FreighterDir}/cargo_ship.fbx")
                        ?? throw new System.Exception("cargo_ship.fbx missing");
            var view = new GameObject("Freighter").AddComponent<ShipView>();
            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(view.transform, false);
            var ship = (GameObject)PrefabUtility.InstantiatePrefab(model);
            ship.transform.SetParent(pivot, false);
            ship.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // bow (antennas) to +Z
            var renderers = ship.GetComponentsInChildren<Renderer>();
            var bounds = new Bounds();
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                var part = r.gameObject.name.Split('.')[0];                     // "mat_0005" (or "mat_0005.002")
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{FreighterDir}/Textures/{part.Replace("mat_", "tex_")}.png");
                if (texture == null) Debug.LogWarning($"[SceneBuilder] freighter part {r.gameObject.name}: no texture");
                var mat = MaterialAsset($"Freighter_{part}", "LuckyNumbers/Ship");
                mat.mainTexture = texture;
                ShipLook(mat);
                mat.SetFloat("_GlowMin", 0.85f);   // gold trim and orange pods are paint, not lamps
                mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                mat.SetFloat("_Cutoff", System.Array.IndexOf(FreighterDecals, part) >= 0 ? 0.5f : 0f);
                EditorUtility.SetDirty(mat);
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var b = r.bounds;
                if (i == 0) bounds = b; else bounds.Encapsulate(b);
            }
            // Solid collision: convex pieces from tools/collision/decompose.py (a watertight remesh decomposed, so no
            // hollow to fall into, while gaps and overhangs stay open). One convex MeshCollider each, not rendered.
            if (collide)
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>($"{FreighterDir}/cargo_ship_collision.obj")
                             ?? throw new System.Exception("cargo_ship_collision.obj missing (tools/collision/decompose.py)");
                var hulls = (GameObject)PrefabUtility.InstantiatePrefab(source);
                hulls.name = "Collision";
                hulls.transform.SetParent(ship.transform, false);
                var hullBounds = new Bounds();
                var pieces = hulls.GetComponentsInChildren<MeshFilter>();
                for (var i = 0; i < pieces.Length; i++)
                {
                    var hr = pieces[i].GetComponent<MeshRenderer>();
                    if (i == 0) hullBounds = hr.bounds; else hullBounds.Encapsulate(hr.bounds);
                    var c = pieces[i].gameObject.AddComponent<MeshCollider>();
                    c.sharedMesh = pieces[i].sharedMesh;
                    c.convex = true;
                    Object.DestroyImmediate(hr);
                }
                Debug.Log($"[SceneBuilder] freighter collision: {pieces.Length} convex pieces, bounds {hullBounds.center} {hullBounds.size} " +
                          $"(model {bounds.center} {bounds.size})");
            }

            var so = new SerializedObject(view);
            so.FindProperty("pivot").objectReferenceValue = pivot;
            so.FindProperty("modelLength").floatValue = bounds.size.z;
            var rs = so.FindProperty("renderers");
            rs.arraySize = renderers.Length;
            for (var i = 0; i < renderers.Length; i++) rs.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            so.FindProperty("skins").arraySize = 0;
            so.FindProperty("engines").stringValue = "freighter";   // content/engines.txtpb
            so.FindProperty("exhaust").objectReferenceValue = ExhaustMaterial();
            so.FindProperty("coldGas").objectReferenceValue = ColdGasMaterial();
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[SceneBuilder] freighter: {renderers.Length} parts, {bounds.size.x:0.#} x {bounds.size.y:0.#} x {bounds.size.z:0.#} m");
            pivot.gameObject.SetActive(false);
            return view;
        }

        sealed class Rig
        {
            public Material Starfield, Planet, Star, Galaxy, Disk;
            public PanelSettings Panel;
        }

        /// <summary>Universe: perspective camera + starfield skybox + body templates; SpacePresenter spawns from content.</summary>
        static void SpaceRig(Rig rig, UIDocument ui)
        {
            var cam = Camera();
            Sky(rig.Starfield);
            var space = new GameObject("Space").AddComponent<SpaceView>();
            var planetTemplate = Sphere("PlanetTemplate", rig.Planet, space.transform);
            var starTemplate = Sphere("StarTemplate", rig.Star, space.transform);
            var galaxyTemplate = QuadMesh("GalaxyTemplate", rig.Galaxy, space.transform);
            var disk = QuadMesh("Disk", rig.Disk, space.transform);
            foreach (var r in new[] { planetTemplate, starTemplate, galaxyTemplate, disk }) r.gameObject.SetActive(false);
            Assign(space, ("viewCamera", cam), ("planetTemplate", planetTemplate), ("starTemplate", starTemplate),
                   ("galaxyTemplate", galaxyTemplate), ("disk", disk), ("ui", ui));
        }

        static void Scopes<TScene>() where TScene : Component
        {
            new GameObject("RootLifetimeScope").AddComponent<RootLifetimeScope>();
            var sceneScope = new GameObject(typeof(TScene).Name).AddComponent<TScene>();
            var so = new SerializedObject(sceneScope);
            so.FindProperty("parentReference.TypeName").stringValue = typeof(RootLifetimeScope).FullName;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Assign(Object target, params (string field, Object value)[] fields)
        {
            var so = new SerializedObject(target);
            foreach (var (field, value) in fields)
            {
                var prop = so.FindProperty(field) ?? throw new System.Exception($"{target.GetType().Name}.{field} not found");
                prop.objectReferenceValue = value;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static Material MaterialAsset(string name, string shaderName)
        {
            var path = $"{MaterialsDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            if (!AssetDatabase.IsValidFolder(MaterialsDir)) AssetDatabase.CreateFolder("Assets/_Project/Art", "Materials");
            mat = new Material(Shader.Find(shaderName) ?? throw new System.Exception($"Shader not found: {shaderName}"));
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static UnityEngine.Camera Camera()
        {
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<UnityEngine.Camera>();
            cam.orthographic = false;
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.1f;
            // Outer orbits ~2500 and the forming system's dust disk reaches 1.2x past them (~3000 radius): seen at
            // an angle from the title camera, its far side went past a 5000 far plane and was cut off in a straight
            // line. 20000 keeps the whole disk (space has nothing close enough for depth precision to matter).
            cam.farClipPlane = 20000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.position = new Vector3(0, 0, -6);
            return cam;
        }

        static void Sky(Material starfield)
        {
            RenderSettings.skybox = starfield;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black; // all shaders are unlit; no baked/ambient lighting
            RenderSettings.fog = false;
        }

        static MeshRenderer QuadMesh(string name, Material material, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad); // billboarded in the shader
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static MeshRenderer Sphere(string name, Material material, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); // 1 unit diameter
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>One PanelSettings for every screen: 1280x720 reference, scales with the screen, our theme.</summary>
        static PanelSettings PanelSettingsAsset()
        {
            var path = PanelSettingsPath;
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, path);
            }
            ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>($"{UiDir}/Theme/LuckyNumbers.tss")
                                 ?? throw new System.Exception("UI theme not found");
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1280, 720);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            EditorUtility.SetDirty(ps);
            // Saving re-imports the asset and invalidates this instance: hand out a fresh load instead.
            AssetDatabase.SaveAssetIfDirty(ps);
            return AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
        }

        static UIDocument Document(string name, PanelSettings panel, string screen)
        {
            var doc = new GameObject(name).AddComponent<UIDocument>();
            // Load by path each time: an instance held across SaveScene/SaveAssets can go stale.
            doc.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath)
                                ?? throw new System.Exception("PanelSettings asset missing");
            doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>($"{UiDir}/Screens/{screen}.uxml")
                                  ?? throw new System.Exception($"UI screen not found: {screen}.uxml");
            return doc;
        }
    }
}
