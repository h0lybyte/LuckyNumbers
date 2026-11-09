# Project structure

## Repository

```
LuckyNumbers/                 repo root
├── LuckyNumbers/             the Unity project (open this folder in Unity Hub)
│   ├── Assets/_Project/      everything we make
│   ├── Packages/             package manifest + lock
│   ├── ProjectSettings/
│   └── Builds/WebGL/         local Web builds (gitignored)
├── tools/ln.py               dev task CLI, see dev-tools.md
├── docs/                     you are here
├── .github/workflows/        CI/CD, see releasing.md
├── CHANGELOG.md              version + release notes; drives releases
└── .env.example              template for local CI secrets (.env is gitignored)
```

## Inside `Assets/_Project/`

Everything we make lives here (the underscore sorts it to the top).
Third-party code goes in `Assets/Plugins/` or comes through the Package Manager, never into `_Project`.

```
_Project/
├── Art/           Sprites, Materials, Fonts, Textures        (add when needed)
├── Audio/         Music, SFX                                 (add when needed)
├── Prefabs/                                                  (add when needed)
├── Editor/        Editor-only tooling (WebBuild: settings + headless build)
├── Scenes/        SampleScene.unity
├── Spikes/        Throwaway experiments, one folder each, delete when done
│   └── WebThreadsTest/  Burst job main-thread vs parallel, web isolation check
├── Scripts/       One folder per area, each with its own .asmdef
│   ├── Core/      Game state, bootstrap, services            (add when needed)
│   ├── Gameplay/  Number logic, rules, scoring               (add when needed)
│   ├── UI/        Menus, HUD                                 (add when needed)
│   └── Rendering/ RenderResolutionCap (720p internal cap)
└── Settings/      URP pipeline assets, renderers, volume profiles, input actions
```

Empty folders are not created ahead of time: git doesn't track empty folders, and Unity would
generate `.meta` files for them that other machines don't have. Create a folder with its first file.

## Script conventions

- **Namespace matches folder:** `Scripts/Gameplay/Foo.cs` → `namespace LuckyNumbers.Gameplay`.
- **One assembly definition per Scripts folder** (`LuckyNumbers.<Folder>.asmdef`). Keeps recompiles
  fast and makes dependencies explicit: if `UI` needs `Gameplay`, add it to the UI asmdef's `references`.
  Dependencies point one way: `UI → Gameplay → Core`, never back.
- One public class per file, file named after the class.
- Spikes get their own asmdef with `autoReferenced: false` so nothing depends on them by accident.

## Git rules

- **Always commit `.meta` files** together with their asset. They hold the IDs Unity uses for references.
- **Move or rename assets inside the Unity editor**, or move the `.meta` alongside the file.
- Never commit `Library/`, `Temp/`, `Logs/`, `Builds/`, `UserSettings/`, `.env` or `*.ulf`. `.gitignore`
  covers all of them.
- Large binary art/audio: set up Git LFS **before** the first big file lands (moving files into LFS
  later rewrites history).
