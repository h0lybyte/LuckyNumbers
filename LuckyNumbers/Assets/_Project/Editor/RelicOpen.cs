using UnityEditor;
using UnityEngine;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Opens a relic's prefab (Prefabs/Relics, baked by SceneBuilder.BakeRelics) in Prefab Mode, framed in the Scene
    /// view. `tools/ln.py relic &lt;model&gt;` starts the editor with
    /// `-executeMethod LuckyNumbers.Editor.RelicOpen.FromCommandLine -lnRelic &lt;model&gt;`; in an open editor,
    /// double-click the prefab instead.
    /// </summary>
    public static class RelicOpen
    {
        const string Dir = "Assets/_Project/Prefabs/Relics";

        public static void FromCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            var name = "";
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-lnRelic") name = args[i + 1];
            // Wait for the editor's windows (the Scene view) before opening: executeMethod runs mid-startup.
            EditorApplication.delayCall += () => Open(name);
        }

        public static void Open(string name)
        {
            var path = $"{Dir}/{name}.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                var have = new System.Collections.Generic.List<string>();
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Dir }))
                    have.Add(System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid)));
                Debug.LogError($"[RelicOpen] no {path}; relics: {string.Join(", ", have)}");
                return;
            }
            EditorGUIUtility.PingObject(prefab);
            AssetDatabase.OpenAsset(prefab);   // Prefab Mode
            EditorApplication.delayCall += () =>
            {
                var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) Selection.activeGameObject = stage.prefabContentsRoot;
                SceneView.lastActiveSceneView?.FrameSelected();
            };
        }
    }
}
