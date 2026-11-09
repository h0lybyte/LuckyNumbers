using LuckyNumbers.Core;
using LuckyNumbers.Space;
using LuckyNumbers.Title;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

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
        static readonly Color ButtonColor = new(0.08f, 0.08f, 0.18f, 0.8f);
        static readonly Color PanelColor = new(0.03f, 0.03f, 0.08f, 0.9f);

        [MenuItem("LuckyNumbers/Scenes/Rebuild All")]
        public static void BuildAll()
        {
            var starfield = MaterialAsset("Starfield", "LuckyNumbers/Starfield");
            var planet = MaterialAsset("Planet3D", "LuckyNumbers/Planet3D");
            var star = MaterialAsset("Star", "LuckyNumbers/Star");

            BuildTitle(starfield, planet, star);
            BuildIntroPlaceholder(starfield);

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

        static void BuildTitle(Material starfieldMat, Material planetMat, Material starMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Universe: perspective camera + starfield skybox; bodies spawned by SpacePresenter from content.
            var cam = Camera();
            Sky(starfieldMat);
            var space = new GameObject("Space").AddComponent<SpaceView>();
            var planetTemplate = Sphere("PlanetTemplate", planetMat, space.transform);
            var starTemplate = Sphere("StarTemplate", starMat, space.transform);
            planetTemplate.gameObject.SetActive(false);
            starTemplate.gameObject.SetActive(false);
            Assign(space, ("viewCamera", cam), ("planetTemplate", planetTemplate), ("starTemplate", starTemplate));

            // UI.
            var canvas = Canvas();
            var title = Label(canvas, "Title", 72, new Vector2(0.5f, 0.78f), FontStyle.Bold);
            var prompt = Label(canvas, "Prompt", 22, new Vector2(0.5f, 0.06f), FontStyle.Italic);

            var menu = new GameObject("Menu", typeof(RectTransform), typeof(HorizontalLayoutGroup)).GetComponent<RectTransform>();
            menu.SetParent(canvas, false);
            menu.anchorMin = menu.anchorMax = new Vector2(0.5f, 0.17f);
            menu.sizeDelta = new Vector2(600, 64);
            var layout = menu.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 24;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = false;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            var buttonTemplate = Button(canvas, "ButtonTemplate", new Vector2(220, 56));
            buttonTemplate.gameObject.SetActive(false);

            var panel = new GameObject("SettingsPanel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas, false);
            var panelRt = (RectTransform)panel.transform;
            panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.45f);
            panelRt.sizeDelta = new Vector2(560, 320);
            panel.GetComponent<Image>().color = PanelColor;
            var settingsTitle = Label(panelRt, "SettingsTitle", 40, new Vector2(0.5f, 0.82f), FontStyle.Bold);
            var settingsBody = Label(panelRt, "SettingsBody", 24, new Vector2(0.5f, 0.5f), FontStyle.Normal);
            var back = Button(panelRt, "Back", new Vector2(180, 52));
            var backRt = (RectTransform)back.transform;
            backRt.anchorMin = backRt.anchorMax = new Vector2(0.5f, 0.16f);
            panel.SetActive(false);

            var view = new GameObject("TitleView").AddComponent<TitleView>();
            Assign(view, ("title", title), ("prompt", prompt), ("menu", menu), ("buttonTemplate", buttonTemplate),
                   ("settingsPanel", panel), ("settingsTitle", settingsTitle), ("settingsBody", settingsBody),
                   ("settingsBack", back));

            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.transform.SetAsLastSibling();

            Scopes<TitleScope>();
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Title.unity");
        }

        // Placeholder until the cutscene player exists: proves the title -> intro switch.
        static void BuildIntroPlaceholder(Material starfieldMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Camera();
            Sky(starfieldMat);
            Label(Canvas(), "Placeholder", 36, new Vector2(0.5f, 0.5f), FontStyle.Normal).text = "Intro (cutscene player next)";
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Intro.unity");
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
            cam.farClipPlane = 500f;
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

        static Transform Canvas()
        {
            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            return go.transform;
        }

        static Button Button(Transform parent, string name, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            ((RectTransform)go.transform).sizeDelta = size;
            go.GetComponent<Image>().color = ButtonColor;
            var label = Label(go.transform, "Label", 28, new Vector2(0.5f, 0.5f), FontStyle.Normal);
            ((RectTransform)label.transform).sizeDelta = size;
            return go.GetComponent<Button>();
        }

        static Text Label(Transform parent, string name, int size, Vector2 anchor, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(1200, size * 1.6f);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false; // labels never block drags/clicks
            return text;
        }
    }
}
