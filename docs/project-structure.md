# Project structure

```
LuckyNumbers/            Unity project
  Assets/_Project/       all our assets and code
    Art/Shaders/         Starfield (skybox), Planet3D, Star, Galaxy, Disk, Ship, Include/Noise.hlsl; Art/Materials/
    Art/Models/Fighter/  fighter1.fbx + Skins/ (RetroVandal, see CREDITS.md)
    Art/Characters/Arms/ first-person arms rig + 18 clips (DrillImpact, CC0)
    Art/Fonts/Exo2/      UI font + SDF assets
    Editor/              WebBuild, SceneBuilder, ShaderPreview, FontSetup, ArtImport (model/skin import rules)
    link.xml             keeps reflection-built assemblies through code stripping
    Generators/          Roslyn generator DLL + [Presenter]/[View] attributes
    Generated/           C# generated from proto/ + content/ (do not edit)
    Scenes/              Title, Intro (built by SceneBuilder)
    Scripts/Core/        RootLifetimeScope, SceneFlow, InputBridge
    Scripts/Space/       SpaceWorld + OrbitSystem (bodies as entities), UniverseGenerator (seed -> system, sky,
                         disk), SpaceView, SpacePresenter (orbit camera, big bang, ISpaceDirector for cutscenes)
    Scripts/Title/       TitleView, TitlePresenter (Play transition), DiceView, DicePresenter, TitleScope
    Scripts/Intro/       IntroPresenter (cutscene player), IntroView, ShipView, IntroScope
    Scripts/Hands/       HandRigMap (bone map from content/hands/*), TwoBoneIK (reach, elbow pole, shrug, palm, flatten)
    Scripts/Planet/      CrashSitePresenter (ground, sky, sun, wreck, wake-up), FirstPersonPresenter (walk/look),
                         GroundView, ArmsView, PlayerView, PlanetView, PlanetScope
    Scripts/Rendering/   RenderResolutionCap
    Settings/            URP assets, input actions
    UI/                  UI Toolkit: Screens/*.uxml, Theme/Theme.uss + LuckyNumbers.tss, PanelSettings
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

## Game flow

Title: dice -> lucky number (`UniverseSeedChanged`, buffered) -> `UniverseGenerator` builds the system (big bang).
Play: UI fades, camera pushes in, pose saved to `CameraHandoff`. Intro: same seed -> same system (`instant`),
starts from that pose; `IntroPresenter` plays `content/intro.txtpb` through `ISpaceDirector`; at impact it
saves `LandingSite`. Planet: regenerates that planet from the same seed for the ground colours, sky and sun,
puts the wreck there, wakes you up in first person (`content/planet.txtpb`). Root scope (brokers, states)
survives scene loads.
