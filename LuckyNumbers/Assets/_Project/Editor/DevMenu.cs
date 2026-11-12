using LuckyNumbers.Core;
using LuckyNumbers.Model;
using MessagePipe;
using UnityEditor;
using MenuItem = UnityEditor.MenuItem;
using UnityEngine;
using VContainer;

namespace LuckyNumbers.Editor
{
    /// <summary>
    /// Play mode: the web page's dev warps from the editor's menu (LuckyNumbers/Dev), for trying things without a
    /// build. Go to Planet, land the ship, step out at the wreck, then drop a monster 10 m ahead.
    /// </summary>
    public static class DevMenu
    {
        const string Root = "LuckyNumbers/Dev/";

        static IObjectResolver Container()
        {
            var scope = Object.FindAnyObjectByType<RootLifetimeScope>();
            return scope != null ? scope.Container : null;
        }

        static void Warp(string to)
        {
            var c = Container();
            if (c == null) { Debug.LogWarning("[Dev] no RootLifetimeScope: enter Play mode first"); return; }
            c.Resolve<IPublisher<DevWarpRequested>>().Publish(new DevWarpRequested(to));
            Debug.Log($"[Dev] warp {to}");
        }

        static bool Playing() => Application.isPlaying;

        [MenuItem(Root + "Go to Planet", priority = 0)]
        static void Planet()
        {
            var c = Container();
            if (c == null) { Debug.LogWarning("[Dev] no RootLifetimeScope: enter Play mode first"); return; }
            c.Resolve<IPublisher<SceneChangeRequested>>().Publish(new SceneChangeRequested(GameScene.Planet));
        }

        [MenuItem(Root + "Go to the Hold Mines", priority = 4)]
        static void Hold()
        {
            var c = Container();
            if (c == null) { Debug.LogWarning("[Dev] no RootLifetimeScope: enter Play mode first"); return; }
            c.Resolve<IPublisher<SceneChangeRequested>>().Publish(new SceneChangeRequested(GameScene.Hold));
        }

        [MenuItem(Root + "Land the Ship", priority = 1)] static void Land() => Warp("land");
        [MenuItem(Root + "Step Out at the Wreck", priority = 2)] static void Wreck() => Warp("wreck");
        [MenuItem(Root + "Step Out at the Ship", priority = 3)] static void Ship() => Warp("ship");
        [MenuItem(Root + "Spawn Monster/Aberration", priority = 20)] static void Aberration() => Warp("monster:aberration");
        [MenuItem(Root + "Spawn Monster/Random", priority = 21)] static void Monster() => Warp("monster");

        [MenuItem(Root + "Go to Planet", true)] [MenuItem(Root + "Go to the Hold Mines", true)] [MenuItem(Root + "Land the Ship", true)]
        [MenuItem(Root + "Step Out at the Wreck", true)] [MenuItem(Root + "Step Out at the Ship", true)]
        [MenuItem(Root + "Spawn Monster/Aberration", true)] [MenuItem(Root + "Spawn Monster/Random", true)]
        static bool Validate() => Playing();
    }
}
