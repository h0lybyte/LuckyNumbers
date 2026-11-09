# Project structure

```
LuckyNumbers/            Unity project
  Assets/_Project/       all our assets and code
    Editor/              WebBuild (Web settings + headless build)
    Scenes/
    Scripts/Rendering/   RenderResolutionCap
    Settings/            URP assets, input actions
    Spikes/WebThreadsTest/  temporary threading benchmark
  Builds/                build output (gitignored)
tools/ln.py              dev CLI
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
