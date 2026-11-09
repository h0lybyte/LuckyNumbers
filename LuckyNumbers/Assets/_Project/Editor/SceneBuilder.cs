using LuckyNumbers.Core;
using LuckyNumbers.Space;
using LuckyNumbers.Intro;
using LuckyNumbers.Title;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene($"{ScenesDir}/Title.unity", true),
                new EditorBuildSettingsScene($"{ScenesDir}/Intro.unity", true),
            };
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>($"{ScenesDir}/SampleScene.unity") != null)
                AssetDatabase.DeleteAsset($"{ScenesDir}/SampleScene.unity"); // URP template scene
            AssetDatabase.SaveAssets();
            Debug.Log("[SceneBuilder] built Title, Intro; build settings = [Title, Intro]");
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

            Scopes<TitleScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Title.unity");
        }

        static void BuildIntro(Rig rig)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var ui = Document("UI", rig.Panel, "Intro");
            SpaceRig(rig, ui);   // same universe as the title (IntroScope registers the intro backdrop)
            Scopes<IntroScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Intro.unity");
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
            cam.farClipPlane = 5000f; // outer orbits ~2500, sky galaxies at 2000
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
