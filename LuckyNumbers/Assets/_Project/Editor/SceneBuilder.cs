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
            BakeWeapons();               // Prefabs/Weapons: addressable, loaded by WeaponRack (not baked into scenes)
            BakeShips();                 // Prefabs/Ships: hulls, fitted by ShipYard into the scenes' shells
            BakeRelics();                // Prefabs/Relics: named relics' models, loaded by RelicsPresenter at the crash site
            BakeMonsters();              // Prefabs/Monsters: monsters' models, loaded by CombatPresenter on Planet
            AddressablesSetup.Apply();   // their addresses and AssetKeys, before any scene is built
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
            BuildFlightSchool(rig);
            BuildOrbit(rig);
            BuildHold(rig);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene($"{ScenesDir}/Title.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Intro.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Planet.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Station.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/FlightSchool.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Orbit.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Hold.unity", true),
            };
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"{ScenesDir}/SampleScene.unity") != null)
                AssetDatabase.DeleteAsset($"{ScenesDir}/SampleScene.unity"); // URP template scene
            AssetDatabase.SaveAssets();
            // The dev Range scene is gone: the range is a room in the station now (RangeRoom).
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"{ScenesDir}/Range.unity") != null)
                AssetDatabase.DeleteAsset($"{ScenesDir}/Range.unity");
            Debug.Log("[SceneBuilder] built Title, Intro, Planet, Station, FlightSchool, Orbit, Hold; build settings = [Title, Intro, Planet, Station, FlightSchool, Orbit, Hold]");
        }

        const string TitleMusicClip = "Assets/_Project/Art/Music/BreakBeatJazzFusion.mp3";

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
            // Music under the menu, character creation and the hangar; the scene's only listener rides along.
            var music = new GameObject("TitleMusic", typeof(AudioSource), typeof(AudioListener)).AddComponent<TitleMusic>();
            Assign(music, ("clip", AssetDatabase.LoadAssetAtPath<AudioClip>(TitleMusicClip)));

            Scopes<TitleScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Title.unity");
        }

        static void BuildIntro(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Intro");
            SpaceRig(rig, ui);   // same universe as the title (IntroScope registers the intro backdrop)
            FreighterShell(FreighterPrefab);   // the museum freighter that goes down (hull: ShipYard)
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
            Assign(ground, ("material", MaterialAsset("Ground", "LuckyNumbers/Ground")),
                   ("holdKit", AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/HoldKit.mat")));   // the surface gate's stone

            var (cam, _) = PlayerRig();   // the gun in your weapon slot comes from WeaponRack (Addressables)

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

            var wreck = FreighterShell(FreighterSolidPrefab);   // the wreck: the museum freighter, solid, so you walk around (and onto) it
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
            Assign(view, ("wreck", wreck), ("fighter", fighter), ("flightCamera", flightCam), ("wreckBeacon", beam.transform),
                   ("landingPad", pad.transform), ("amuletTemplate", amulet), ("vaseTemplate", vase), ("relicBeamTemplate", relicBeam));
            // Monsters' models: Prefabs/Monsters (BakeMonsters), loaded by CombatPresenter, not baked in here.
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
        /// <summary>
        /// Under a dwarven hold (HoldScene/HoldPresenter): the first-person rig in the dark, the overlay UI, and the
        /// hold's chunk meshes (HoldView, one HoldKit material). No sky: the camera clears to the haze colour.
        /// </summary>
        static void BuildHold(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Planet");   // fade, hint, crosshair
            var (eyes, _) = PlayerRig();
            eyes.clearFlags = CameraClearFlags.SolidColor;
            eyes.backgroundColor = new Color(0.035f, 0.05f, 0.05f);
            eyes.farClipPlane = 140f;
            eyes.nearClipPlane = 0.05f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.08f, 0.09f, 0.09f);
            RenderSettings.skybox = null;

            var view = new GameObject("PlanetView").AddComponent<PlanetView>();
            Assign(view, ("document", ui));
            var hold = new GameObject("Hold").AddComponent<LuckyNumbers.HoldScene.HoldView>();
            hold.Init(AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/HoldKit.mat")
                      ?? throw new System.Exception("HoldKit.mat missing: build the hold texture array first"));
            EditorUtility.SetDirty(hold);

            Scopes<LuckyNumbers.HoldScene.HoldScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Hold.unity");
        }

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

            // No weapons in the scene: the test gun by the spawn (StationPresenter), the equipped gun (FirstPersonPresenter)
            // and the range's (RangeRoom) are copies of Prefabs/Weapons, loaded through WeaponRack.

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
        const string CharactersDir = "Assets/_Project/Art/Characters";

        [System.Serializable] sealed class BakedClip { public string name; public float seconds, speed; }
        [System.Serializable] sealed class BakedClips { public BakedClip[] clips; }

        /// <summary>
        /// A monster's model template (off): Art/Characters/&lt;folder&gt;/&lt;name&gt;.fbx (baked by
        /// tools/blender/export_&lt;name&gt;.py) with its skin &lt;name&gt;_body.png on the Ship shader, every take as a
        /// clip and each clip's stride speed from &lt;name&gt;.json. Turned so it faces +Z: its front is worked out from
        /// its hips (thigh_l to thigh_r, or thigh.L to thigh.R, is its right; a Mixamo rig: its arms, LeftArm to RightArm), whatever axes the FBX came in with.
        /// </summary>
        static MonsterRig MonsterModel(Transform parent, string name, string folder)
        {
            var dir = $"{CharactersDir}/{folder}";
            var fbxPath = $"{dir}/{name}.fbx";
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath) ?? throw new System.Exception($"{fbxPath} missing");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            model.name = name;
            model.transform.SetParent(parent, false);
            var mat = MaterialAsset($"Monster_{name}", "LuckyNumbers/Ship");
            mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{name}_body.png");
            mat.SetFloat("_Glow", 0f);
            mat.SetFloat("_Rim", 0.2f);
            mat.SetFloat("_Lift", 0.4f);
            EditorUtility.SetDirty(mat);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var animator = model.GetComponent<Animator>();
            if (animator == null) animator = model.AddComponent<Animator>();   // Unity null: no ?? on components
            if (animator.avatar == null)
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                    if (o is Avatar avatar) animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            var baked = AssetDatabase.LoadAssetAtPath<TextAsset>($"{dir}/{name}.json");
            var strides = baked != null ? JsonUtility.FromJson<BakedClips>(baked.text).clips : System.Array.Empty<BakedClip>();
            var clips = new System.Collections.Generic.List<AnimationClip>();
            var speeds = new System.Collections.Generic.List<float>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            {
                if (o is not AnimationClip clip || clip.name.StartsWith("__preview")) continue;
                clips.Add(clip);
                var speed = 0f;
                foreach (var b in strides) if (b.name == MonsterRig.Short(clip.name)) speed = b.speed;
                speeds.Add(speed);
            }
            // Where a bone is in the skin's bind pose (world): the model's own transforms can hold a clip's first frame
            // instead (the skeleton's FBX comes in posed as its death's start, hips yawed 13 deg).
            Vector3? Bone(string bone)
            {
                foreach (var s in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    for (var i = 0; i < s.bones.Length; i++)
                        if (s.bones[i] != null && s.bones[i].name == bone)
                            return s.transform.TransformPoint(s.sharedMesh.bindposes[i].inverse.GetColumn(3));
                return null;
            }
            // The aberration's names; the skeleton's (Rigify); the arcanist's (Mixamo): its arms, as its bind pose's hips are
            // skewed 8 deg (its thighs' line), while its arms and feet are square to where its run goes.
            var left = Bone("thigh_l") ?? Bone("thigh.L") ?? Bone("LeftArm");
            var right = Bone("thigh_r") ?? Bone("thigh.R") ?? Bone("RightArm");
            if (left != null && right != null)
            {
                var side = Vector3.ProjectOnPlane(right.Value - left.Value, Vector3.up);
                var front = Vector3.Cross(side, Vector3.up);
                var turn = Vector3.SignedAngle(front, Vector3.forward, Vector3.up);
                model.transform.localRotation = Quaternion.Euler(0f, turn, 0f) * model.transform.localRotation;
                Debug.Log($"[SceneBuilder] monster {name}: {clips.Count} clips, turned {turn:0} deg to face +Z");
            }
            // Culling box: the bind pose's bounds are the import's (Blender axes: lying down, knee high), so a standing
            // creature would vanish at the screen's edges. A 2.6 m box round its body, in each skin's bone space.
            foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var space = skin.rootBone != null ? skin.rootBone : skin.transform;
                skin.updateWhenOffscreen = false;
                skin.localBounds = new Bounds(space.InverseTransformPoint(model.transform.position + Vector3.up * 1f), Vector3.one * 2.6f);
            }
            var rig = model.AddComponent<MonsterRig>();
            rig.Init(animator, clips.ToArray(), speeds.ToArray());
            EditorUtility.SetDirty(rig);
            model.SetActive(false);
            return rig;
        }

        const string MonsterPrefabsDir = "Assets/_Project/Prefabs/Monsters";

        /// <summary>Monsters with a model: (MonsterType.model, its folder in Art/Characters).</summary>
        static readonly (string name, string folder)[] MonsterModels = { ("aberration", "Aberration"), ("skeleton", "Skeleton"), ("arcanist", "Arcanist") };

        /// <summary>
        /// Every monster's model as a prefab in Prefabs/Monsters (the Monsters Addressables group, see
        /// AddressablesSetup.Rules), saved off like the scene templates were; CombatPresenter loads them by
        /// MonsterType.model and MonsterView.CreateModel copies one per monster. Prefabs nothing names anymore go.
        /// Each also gets its clips baked to vertex animation textures (VatBaker: the clips content/combat.txtpb's
        /// monsters of that model play), referenced by its MonsterRig, so they ride in the same bundle and
        /// MonsterCrowd draws them instanced.
        /// </summary>
        /// <summary>The clips the monsters of model `name` play (content/combat.txtpb); every clip if none names it.</summary>
        static System.Collections.Generic.IEnumerable<string> MonsterClipsOf(string name, MonsterRig rig)
        {
            var wanted = new System.Collections.Generic.List<string>();
            foreach (var m in LuckyNumbers.Model.GameContent.Combat?.Monsters ?? new())
                if (m.Model == name && m.Clips != null)
                    wanted.AddRange(new[] { m.Clips.Idle, m.Clips.Walk, m.Clips.Run, m.Clips.Attack, m.Clips.Cast, m.Clips.Hit, m.Clips.Die });
            wanted.RemoveAll(string.IsNullOrEmpty);
            return wanted.Count > 0 ? wanted : rig.ClipNames;
        }

        public static void BakeMonsters()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs")) AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
            if (!AssetDatabase.IsValidFolder(MonsterPrefabsDir)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Monsters");
            var names = new System.Collections.Generic.List<string>();
            foreach (var (name, folder) in MonsterModels)
            {
                var rig = MonsterModel(null, name, folder);
                var skin = rig.GetComponentInChildren<Renderer>(true).sharedMaterial;
                var vatMaterial = MaterialAsset($"MonsterVat_{name}", "LuckyNumbers/MonsterVat");
                rig.SetVat(VatBaker.Bake(rig, name, MonsterClipsOf(name, rig), skin, vatMaterial));
                EditorUtility.SetDirty(rig);
                PrefabUtility.SaveAsPrefabAsset(rig.gameObject, $"{MonsterPrefabsDir}/{name}.prefab");
                Object.DestroyImmediate(rig.gameObject);
                names.Add(name);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { MonsterPrefabsDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!names.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
        }

        const string RelicPrefabsDir = "Assets/_Project/Prefabs/Relics";

        /// <summary>
        /// Model scale per relic `model` (its FBX in Art/Models/Relics): sized to read on the ground. The amulet is 13 cm
        /// in its file; the vase 1.98 m and the block 2 m (0.35 makes them ~0.7 m, standing on the ground at the relic's
        /// 0.35 m rest height).
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, float> RelicScales = new()
        {
            ["water_amulet"] = 3f,
            ["roman_vase"] = 0.35f,
            ["aztec_block"] = 0.35f,
        };

        /// <summary>
        /// Painted relic parts, by `model/mesh` (meshes named by tools/blender/export_relic_parts.py): a material asset
        /// with the model's own texture (beside its FBX in Art/Models/Relics) under near-white tones (low, high) and a rim
        /// glow. Parts not listed are dressed by submesh (the amulet: gold, water gem, crest gem).
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, (string Asset, string Paint, Color Low, Color High, float Glow)> RelicPaints = new()
        {
            // RenderX's Roman Vase: grey marble (rim, shoulder, foot) and the two filigree panels round its belly.
            ["roman_vase/Body"] = ("RelicMarble", "roman_vase_marble.jpg", new Color(0.92f, 0.9f, 0.88f), new Color(1.1f, 1.08f, 1.04f), 0.25f),
            ["roman_vase/PanelA"] = ("RelicVasePanelA", "roman_vase_panel_a.jpg", new Color(1f, 0.96f, 0.9f), new Color(1.15f, 1.1f, 1f), 0.4f),
            ["roman_vase/PanelB"] = ("RelicVasePanelB", "roman_vase_panel_b.jpg", new Color(1f, 0.96f, 0.9f), new Color(1.15f, 1.1f, 1f), 0.4f),
            // RenderX's Aztec Block: carved limestone all round.
            ["aztec_block/Stone"] = ("RelicAztecStone", "aztec_block_stone.jpg", new Color(0.95f, 0.92f, 0.86f), new Color(1.1f, 1.06f, 1f), 0.3f),
        };

        /// <summary>
        /// Every named relic's model as a prefab in Prefabs/Relics (the Relics Addressables group, see
        /// AddressablesSetup.Rules): each relic item's `model`, dressed on the Relic shader. RelicsPresenter loads them
        /// by that name. Prefabs nothing names anymore go. Open one (double-click) to see it in Prefab Mode.
        /// </summary>
        public static void BakeRelics()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs")) AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
            if (!AssetDatabase.IsValidFolder(RelicPrefabsDir)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Relics");
            var names = new System.Collections.Generic.List<string>();
            foreach (var item in LuckyNumbers.Model.GameContent.Items.Items)
            {
                if (item.Kind != LuckyNumbers.Model.ItemKind.Relic || string.IsNullOrEmpty(item.Model) || names.Contains(item.Model)) continue;
                if (!RelicScales.TryGetValue(item.Model, out var scale))
                {
                    Debug.LogWarning($"[SceneBuilder] relic {item.Id}: model '{item.Model}' has no scale in SceneBuilder.RelicScales; using 1");
                    scale = 1f;
                }
                var root = ArtifactModel(null, item.Model, scale);
                root.gameObject.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(root.gameObject, $"{RelicPrefabsDir}/{item.Model}.prefab");
                Object.DestroyImmediate(root.gameObject);
                names.Add(item.Model);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { RelicPrefabsDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!names.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[SceneBuilder] relics: {string.Join(", ", names)}");
        }

        static Transform ArtifactModel(Transform parent, string name, float scale)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{RelicsDir}/{name}.fbx")
                      ?? throw new System.Exception($"{name}.fbx missing in {RelicsDir}");
            Material Mat(string asset, Color a, Color b, float glow, string paint = null)
            {
                var m = MaterialAsset(asset, "LuckyNumbers/Relic");
                m.SetColor("_ColorA", a);
                m.SetColor("_ColorB", b);
                m.SetFloat("_Glow", glow);
                // A painted part (RelicPaints): the paint under near-white tones, no mid band.
                m.SetTexture("_MainTex", paint == null ? null
                    : AssetDatabase.LoadAssetAtPath<Texture2D>($"{RelicsDir}/{paint}") ?? throw new System.Exception($"{paint} missing in {RelicsDir}"));
                m.SetFloat("_Band", paint == null ? 1f : 0f);
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
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                var count = mesh.subMeshCount;
                var mats = new Material[count];
                // By mesh name, not object: a one-part FBX puts its mesh on the root, named after the file.
                var painted = RelicPaints.TryGetValue($"{name}/{mesh.name}", out var p)
                    ? Mat(p.Asset, p.Low, p.High, p.Glow, p.Paint) : null;
                for (var i = 0; i < count; i++)
                    mats[i] = painted != null ? painted : count == 1 ? gold : i == 0 ? gold : i == 1 ? gem : crest;
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

        const string WeaponPrefabsDir = "Assets/_Project/Prefabs/Weapons";

        /// <summary>
        /// Every weapon as a prefab in Prefabs/Weapons (the Weapons Addressables group, see AddressablesSetup.Rules):
        /// each model in weapons.json (WeaponRig), plus a placeholder gun for each weapon item whose `model` is
        /// "PLACEHOLDER_&lt;id&gt;". Saved inactive, as the scenes used to park them. Prefabs nothing names anymore go.
        /// </summary>
        public static void BakeWeapons()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs")) AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
            if (!AssetDatabase.IsValidFolder(WeaponPrefabsDir)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Weapons");
            var names = new System.Collections.Generic.List<string>();
            foreach (var w in Weapons()) names.Add(w.name);
            foreach (var item in LuckyNumbers.Model.GameContent.Items.Items)
            {
                if (item.Slot != LuckyNumbers.Model.EquipSlot.Weapon || string.IsNullOrEmpty(item.Model) || names.Contains(item.Model)) continue;
                if (item.Model.StartsWith("PLACEHOLDER_")) names.Add(item.Model);
                else Debug.LogWarning($"[SceneBuilder] item {item.Id}: model '{item.Model}' isn't in {WeaponsDir}/weapons.json");
            }
            foreach (var name in names)
            {
                var rig = name.StartsWith("PLACEHOLDER_") ? PlaceholderGun(name) : WeaponRig(name);
                PrefabUtility.SaveAsPrefabAsset(rig, $"{WeaponPrefabsDir}/{name}.prefab");
                Object.DestroyImmediate(rig);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { WeaponPrefabsDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!names.Contains(System.IO.Path.GetFileNameWithoutExtension(path))) AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// A gun with no model yet (an item's `model: "PLACEHOLDER_<id>"`): obvious named blocks, a slide and a grip,
        /// +Z down the barrel like the real ones, with hand-set hold points. Replace with the item's own model.
        /// </summary>
        static GameObject PlaceholderGun(string model)
        {
            var metal = Tint("WeaponMetal", new Color(0.42f, 0.44f, 0.47f), new Color(0.72f, 0.74f, 0.76f), 0.12f, 0f);
            var bolt = Tint("WeaponBolt", new Color(0.35f, 1.6f, 2.6f), new Color(0.35f, 1.6f, 2.6f), 1.5f, 0f);
            var root = new GameObject($"Weapon {model}");
            void Part(string name, Vector3 at, Vector3 size, float pitch)
            {
                var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
                part.name = $"{model}_{name}";
                Object.DestroyImmediate(part.GetComponent<BoxCollider>());
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = at;
                part.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                part.transform.localScale = size;
                var r = part.GetComponent<MeshRenderer>();
                r.sharedMaterial = metal;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var grasp = part.AddComponent<MeshCollider>();   // the fingers close onto it while held
                grasp.sharedMesh = part.GetComponent<MeshFilter>().sharedMesh;
                grasp.enabled = false;
            }
            Part("slide", new Vector3(0f, 0.02f, 0.03f), new Vector3(0.035f, 0.045f, 0.2f), 0f);
            Part("grip", new Vector3(0f, -0.04f, -0.045f), new Vector3(0.03f, 0.1f, 0.045f), -15f);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -0.01f, 0.02f);
            box.size = new Vector3(0.04f, 0.14f, 0.21f);
            var body = root.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var view = root.AddComponent<WeaponView>();
            // Right hand low on the grip, left cupping it from below and in front (no foregrip on a pistol).
            view.Init(new Vector3(0f, -0.06f, -0.05f), new Vector3(0f, -0.06f, 0.0f), new Vector3(0f, 0.02f, 0.135f), bolt);
            EditorUtility.SetDirty(view);
            root.SetActive(false);
            Debug.Log($"[SceneBuilder] placeholder gun {model}");
            return root;
        }

        static WeaponItem Weapon(string name) =>
            System.Array.Find(Weapons(), w => w.name == name) ?? throw new System.Exception($"{name} not in {WeaponsDir}/weapons.json");


        /// <summary>
        /// The Flight School (Planet/FlightSchoolPresenter): a training deck in space (LuckyNumbers/TrainingGrid) with
        /// glowing pylons round it for reference, your fighter and the museum freighter (one shown at a time), the
        /// chase camera with bloom (so the flames glow) and the crash site's overlay for the HUD.
        /// </summary>
        static void BuildFlightSchool(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var school = LuckyNumbers.Model.GameContent.School;
            RenderSettings.skybox = rig.Starfield;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.25f, 0.27f, 0.32f);
            var ui = Document("UI", rig.Panel, "Planet");   // hint, flight HUD, fade
            var view = new GameObject("PlanetView").AddComponent<PlanetView>();

            var camGo = new GameObject("FlightCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 65f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 6000f;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            var volume = new GameObject("Glow").AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = GlowProfile();

            // The deck: one big plate (a Plane primitive is 10 m across), no collider (the presenter keeps you above it).
            var grid = MaterialAsset("TrainingGrid", "LuckyNumbers/TrainingGrid");
            grid.SetFloat("_Fade", school.DeckSize * 0.48f);
            EditorUtility.SetDirty(grid);
            var deck = GameObject.CreatePrimitive(PrimitiveType.Plane);
            deck.name = "Deck";
            Object.DestroyImmediate(deck.GetComponent<Collider>());
            deck.transform.localScale = new Vector3(school.DeckSize / 10f, 1f, school.DeckSize / 10f);
            var deckRenderer = deck.GetComponent<MeshRenderer>();
            deckRenderer.sharedMaterial = grid;
            deckRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Pylons in a ring (tall glowing posts): something to fly round and judge distance by.
            var pylon = Tint("SchoolPylon", new Color(0.1f, 0.35f, 0.6f), new Color(0.4f, 0.9f, 1f), 1.2f, 0.3f);
            var pylons = new GameObject("Pylons").transform;
            for (var i = 0; i < 12; i++)
            {
                var a = i / 12f * Mathf.PI * 2f;
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.name = $"Pylon {i}";
                Object.DestroyImmediate(post.GetComponent<Collider>());
                post.transform.SetParent(pylons, false);
                var h = i % 3 == 0 ? 120f : 60f;
                post.transform.localPosition = new Vector3(Mathf.Cos(a) * 260f, h / 2f, Mathf.Sin(a) * 260f);
                post.transform.localScale = new Vector3(4f, h, 4f);
                var r = post.GetComponent<MeshRenderer>();
                r.sharedMaterial = pylon;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            var fighter = ShipRig(name: "Fighter");
            var freighter = FreighterShell(FreighterPrefab);   // M flies it (hull: ShipYard)
            Assign(view, ("document", ui), ("fighter", fighter), ("wreck", freighter), ("flightCamera", cam));

            Scopes<FlightSchoolScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/FlightSchool.unity");
        }

        /// <summary>
        /// Outside the station (Station/OrbitPresenter): the universe (SpaceRig, its camera the base of the stack, as
        /// in the station) with the flight camera drawn over it, your fighter, the crash site's overlay for the HUD,
        /// and OrbitView (the hull's placeholder blocks and the docking beacon, built at runtime from the seeded plan).
        /// </summary>
        static void BuildOrbit(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Planet");   // hint, flight HUD, fade
            SpaceRig(rig, ui);
            var spaceCam = Object.FindAnyObjectByType<UnityEngine.Camera>();
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.25f, 0.27f, 0.32f);
            var view = new GameObject("PlanetView").AddComponent<PlanetView>();

            var camGo = new GameObject("FlightCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 65f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 6000f;
            var camData = cam.GetUniversalAdditionalCameraData();
            camData.renderType = CameraRenderType.Overlay;
            camData.renderPostProcessing = true;
            spaceCam.GetUniversalAdditionalCameraData().cameraStack.Add(cam);
            spaceCam.fieldOfView = cam.fieldOfView;
            var volume = new GameObject("Glow").AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = GlowProfile();

            var fighter = ShipRig(name: "Fighter");
            var freighter = FreighterShell(null);   // PlanetView wants one; never shown here, so no hull
            Assign(view, ("document", ui), ("fighter", fighter), ("wreck", freighter), ("flightCamera", cam));

            var orbit = new GameObject("StationOutside").AddComponent<LuckyNumbers.Station.OrbitView>();
            Assign(orbit,
                   ("hull", Tint("OrbitHull", new Color(0.16f, 0.17f, 0.2f), new Color(0.42f, 0.44f, 0.48f), 0.15f, 0f)),
                   ("trim", Tint("OrbitBayTrim", new Color(0.05f, 0.25f, 0.35f), new Color(0.3f, 0.85f, 1f), 1.4f, 0.2f)),
                   ("beacon", Tint("OrbitBeacon", new Color(0.1f, 0.6f, 0.4f), new Color(0.5f, 1f, 0.7f), 2.2f, 0.6f)));

            Scopes<LuckyNumbers.Station.OrbitScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Orbit.unity");
        }

        const string WeaponsDir = "Assets/_Project/Art/Models/Weapons";

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
            root.SetActive(false);   // placed by whoever builds the scene (StationPresenter, RangeRoom)
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

        const string ShipPrefabsDir = "Assets/_Project/Prefabs/Ships";
        const string FreighterPrefab = "Freighter", FreighterSolidPrefab = "FreighterSolid";

        /// <summary>
        /// The freighter's hull as prefabs in Prefabs/Ships (the Ships Addressables group): Freighter, and FreighterSolid
        /// with its collision (the wreck you walk round). Scenes hold only a shell (FreighterShell); ShipYard fits these.
        /// </summary>
        public static void BakeShips()
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs")) AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
            if (!AssetDatabase.IsValidFolder(ShipPrefabsDir)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Ships");
            foreach (var (name, collide) in new[] { (FreighterPrefab, false), (FreighterSolidPrefab, true) })
            {
                var hull = FreighterHull(collide, out _);
                hull.name = name;
                PrefabUtility.SaveAsPrefabAsset(hull, $"{ShipPrefabsDir}/{name}.prefab");
                Object.DestroyImmediate(hull);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// The museum freighter (CC BY, see CREDITS): one part per material, named after it (mat_0005 uses
        /// Textures/tex_0005), its own look kept (no skins). +Z forward; `length` measured from the model.
        /// With `collide`: the convex pieces of cargo_ship_collision.obj (tools/collision/decompose.py).
        /// </summary>
        static GameObject FreighterHull(bool collide, out float length)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{FreighterDir}/cargo_ship.fbx")
                        ?? throw new System.Exception("cargo_ship.fbx missing");
            var root = new GameObject("Freighter hull");
            var ship = (GameObject)PrefabUtility.InstantiatePrefab(model);
            ship.transform.SetParent(root.transform, false);
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
            Debug.Log($"[SceneBuilder] freighter: {renderers.Length} parts, {bounds.size.x:0.#} x {bounds.size.y:0.#} x {bounds.size.z:0.#} m");
            length = bounds.size.z;
            return root;
        }

        /// <summary>
        /// The museum freighter in a scene: a ShipView shell (pivot, length, engines) whose hull ShipYard fits from
        /// Prefabs/Ships at runtime (`prefab`: Freighter, or FreighterSolid for the wreck you walk round). Null: no
        /// hull at all (a scene whose PlanetView needs a freighter it never shows).
        /// </summary>
        static ShipView FreighterShell(string prefab)
        {
            var view = new GameObject("Freighter").AddComponent<ShipView>();
            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(view.transform, false);
            var length = 1f;
            if (prefab != null)
            {
                var path = $"{ShipPrefabsDir}/{prefab}.prefab";
                var hull = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path)
                                                                     ?? throw new System.Exception($"{path} missing (BakeShips)"));
                var renderers = hull.GetComponentsInChildren<Renderer>();
                var bounds = renderers[0].bounds;
                foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                length = bounds.size.z;   // measured as FreighterHull measures it
                Object.DestroyImmediate(hull);
            }
            var so = new SerializedObject(view);
            so.FindProperty("pivot").objectReferenceValue = pivot;
            so.FindProperty("modelLength").floatValue = length;
            so.FindProperty("renderers").arraySize = 0;
            so.FindProperty("skins").arraySize = 0;
            so.FindProperty("engines").stringValue = "freighter";   // content/engines.txtpb
            so.FindProperty("exhaust").objectReferenceValue = ExhaustMaterial();
            so.FindProperty("coldGas").objectReferenceValue = ColdGasMaterial();
            so.FindProperty("model").stringValue =
                prefab == null ? "" : AddressablesSetup.AddressOf($"{ShipPrefabsDir}/{prefab}.prefab");
            so.ApplyModifiedPropertiesWithoutUndo();
            pivot.gameObject.SetActive(false);
            return view;
        }

        /// <summary>The museum freighter whole, hull baked in (ShaderPreview's stand-alone look at it).</summary>
        internal static ShipView FreighterRig(bool collide = false)
        {
            var view = FreighterShell(null);
            var hull = FreighterHull(collide, out var length);
            hull.transform.SetParent(view.Pivot, false);
            var so = new SerializedObject(view);
            so.FindProperty("modelLength").floatValue = length;
            var renderers = hull.GetComponentsInChildren<Renderer>();
            var rs = so.FindProperty("renderers");
            rs.arraySize = renderers.Length;
            for (var i = 0; i < renderers.Length; i++) rs.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
            so.ApplyModifiedPropertiesWithoutUndo();
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
