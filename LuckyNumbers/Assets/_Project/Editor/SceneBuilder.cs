using LuckyNumbers.Core;
using LuckyNumbers.Title;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Builds the game scenes from code (reproducible, reviewable): tools/ln.py scenes.
    /// Look and text come from content/*.txtpb at runtime; scenes only hold structure.
    /// </summary>
    public static class SceneBuilder
    {
        const string ScenesDir = "Assets/_Project/Scenes";
        const string MaterialsDir = "Assets/_Project/Art/Materials";

        [MenuItem("LuckyNumbers/Scenes/Rebuild All")]
        public static void BuildAll()
        {
            var starfield = MaterialAsset("Starfield", "LuckyNumbers/Starfield");
            var planet = MaterialAsset("Planet2D", "LuckyNumbers/Planet2D");

            BuildTitle(starfield, planet);
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

        static void BuildTitle(Material starfieldMat, Material planetMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Camera();
            var starfield = Quad("Starfield", starfieldMat, new Vector3(0, 0, 5), new Vector3(10, 10, 1));
            var planet = Quad("Planet", planetMat, Vector3.zero, Vector3.one);

            var canvas = Canvas();
            var title = Label(canvas, "Title", 72, new Vector2(0.5f, 0.72f), FontStyle.Bold);
            var prompt = Label(canvas, "Prompt", 28, new Vector2(0.5f, 0.18f), FontStyle.Normal);

            var view = new GameObject("TitleView").AddComponent<TitleView>();
            var so = new SerializedObject(view);
            so.FindProperty("starfield").objectReferenceValue = starfield;
            so.FindProperty("planet").objectReferenceValue = planet;
            so.FindProperty("title").objectReferenceValue = title;
            so.FindProperty("prompt").objectReferenceValue = prompt;
            so.ApplyModifiedPropertiesWithoutUndo();

            new GameObject("RootLifetimeScope").AddComponent<RootLifetimeScope>();
            var titleScope = new GameObject("TitleScope").AddComponent<TitleScope>();
            var scopeSo = new SerializedObject(titleScope);
            scopeSo.FindProperty("parentReference.TypeName").stringValue = typeof(RootLifetimeScope).FullName;
            scopeSo.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Title.unity");
        }

        // Placeholder until the cutscene player exists: proves the title -> intro switch.
        static void BuildIntroPlaceholder(Material starfieldMat)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Camera();
            Quad("Starfield", starfieldMat, new Vector3(0, 0, 5), new Vector3(10, 10, 1));
            Label(Canvas(), "Placeholder", 36, new Vector2(0.5f, 0.5f), FontStyle.Normal).text = "Intro (cutscene player next)";
            EditorSceneManager.SaveScene(scene, $"{ScenesDir}/Intro.unity");
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

        static void Camera()
        {
            var cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<UnityEngine.Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.transform.position = new Vector3(0, 0, -10);
        }

        static MeshRenderer Quad(string name, Material material, Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = position;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            return r;
        }

        static Transform Canvas()
        {
            var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 0.5f;
            return go.transform;
        }

        static Text Label(Transform canvas, string name, int size, Vector2 anchor, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(canvas, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.sizeDelta = new Vector2(1200, size * 1.6f);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            return text;
        }
    }
}
