# Project structure

```
LuckyNumbers/            Unity project
  Assets/_Project/       all our assets and code
    Art/Shaders/         Starfield (skybox), Planet3D, Include/Noise.hlsl; Art/Materials/
    Editor/              WebBuild, SceneBuilder, ShaderPreview
    Generators/          Roslyn generator DLL + [Presenter]/[View] attributes
    Generated/           C# generated from proto/ + content/ (do not edit)
    Scenes/              Title, Intro (built by SceneBuilder)
    Scripts/Core/        RootLifetimeScope, SceneFlow, InputBridge
    Scripts/Space/       SpaceWorld + OrbitSystem (bodies as entities), SpaceView, SpacePresenter (orbit camera)
    Scripts/Title/       TitleView, TitlePresenter, TitleScope
    Scripts/Rendering/   RenderResolutionCap
    Settings/            URP assets, input actions
    Spikes/WebThreadsTest/  temporary threading benchmark
  Builds/                build output (gitignored)
proto/, content/         schema + authored data (source of truth)
tools/ln.py              dev CLI (protogen.py, webshot.mjs, roslyn/)
CHANGELOG.md             version source, triggers releases
.env.example             template for CI secrets (.env gitignored)
```

Planned when needed: `Art/`, `Audio/`, `Prefabs/`, `Scripts/{Core,Gameplay,UI}/`. Don't create empty folders; git doesn't track them.

## Conventions

- Third-party code: Package Manager or `Assets/Plugins/`, never `_Project/`.
- Namespace = folder: `Scripts/Gameplay/` → `LuckyNumbers.Gameplay`.
- One asmdef per `Scripts/` folder (`LuckyNumbers.<Folder>`). Dependencies one way: `UI → Gameplay → Core`.
- Commit `.meta` files with their assets; move or rename assets inside Unity.
- Set up Git LFS before adding large binaries.
