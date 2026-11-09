# LuckyNumbers

Unity 6 (6000.6.8f1, URP) game targeting **WebGL**, published on itch.io.

## Opening the project

Open the `LuckyNumbers/` folder (not the repo root) in Unity Hub with editor version **6000.6.8f1**.

## Dev tasks (`tools/ln.py`)

Python 3, standard library only, same commands on macOS and Windows. Run from anywhere:

```
python3 tools/ln.py doctor       # what was detected: Unity path, editor open?, build present, butler
python3 tools/ln.py check        # headless compile; prints C# errors, exit 1 on failure
python3 tools/ln.py build-web    # headless Web build (WebGPU + WebGL 2) -> LuckyNumbers/Builds/WebGL
python3 tools/ln.py build-web --gfx webgl2   # WebGL 2 only -> Builds/WebGL-WebGL2 (serve --gfx webgl2)
python3 tools/ln.py serve --open # serve it at http://localhost:8080 with COOP/COEP headers
python3 tools/ln.py deploy --target user/game [--version 0.1.0] [--dry-run]   # butler push to itch
python3 tools/ln.py secrets [--dry-run]   # push .env values to GitHub Actions secrets
```

- `check` / `build-web` need the **Unity editor closed** (the tool detects a live editor, ignores stale lockfiles).
- Unity is found from `ProjectVersion.txt` + the Unity Hub folder. Override with `UNITY_PATH`.
- Logs: `LuckyNumbers/Logs/ln-*.log`.
- Windows: use `py tools/ln.py ...` if `python3` isn't on PATH.

## CI/CD (`.github/workflows/web.yml`)

GitHub Actions builds the Web player with [GameCI](https://game.ci) (`unityci/editor:ubuntu-6000.6.8f1-webgl-3`)
using the same `WebBuild.Build` entry point as `ln.py build-web`, then pushes to itch.io with butler.

**Releases are driven by `CHANGELOG.md`.** The top `## x.y.z` entry is the game version
(`python3 tools/ln.py version`); it is stamped into the build as `Application.version`.

| Trigger | Result |
|---|---|
| Push to `main` changing `CHANGELOG.md` with a **new** version | build → itch.io `html5` as `x.y.z` → GitHub tag + release `vx.y.z` (notes = that entry) |
| Push to `main` changing only notes (version already tagged) | nothing (version job reports "already released") |
| Manual (Actions → Web release → Run workflow) | build current version; deploy if "deploy" ticked |
| Any other push / PR | no build |

To release: add `## 0.2.0 (date)` with notes at the top of `CHANGELOG.md`, merge to `main`.
A `## Unreleased` section above it is ignored, so notes can accumulate there first.

One-time setup: copy `.env.example` to `.env` (gitignored), fill it in, then:

```
python3 tools/ln.py secrets --dry-run   # shows which keys are set (never values)
python3 tools/ln.py secrets             # pushes them via gh (secrets over stdin)
```

- Unity license (https://game.ci/docs/github/activation): Unity Hub → Licenses → **Add** → "Get a free
  personal license", even if Hub already shows "Personal". That writes
  `/Library/Application Support/Unity/Unity_lic.ulf` (Windows: `C:\ProgramData\Unity\Unity_lic.ulf`);
  `.env` points `UNITY_LICENSE_FILE` at it. Email + password are also required by GameCI for Personal.
- `BUTLER_API_KEY` must belong to the itch.io account that owns `ITCH_TARGET` (currently `kbve/lucky-numbers`).
- Create the itch.io project first (Kind: **HTML**). After the first push, set the `html5` upload as
  "played in the browser", embed size 1280×720, and tick **SharedArrayBuffer support** (needed for threads).
- PRs from forks get no secrets, so their build job fails at license activation. Expected for a public repo.

## Web build settings

Applied by `LuckyNumbers → Web → Apply Settings` (also run by every build), see `Assets/_Project/Editor/WebBuild.cs`:
multithreading **on**, **Gzip + decompression fallback** (itch.io sends no `Content-Encoding`), 1280×720 canvas,
graphics **WebGPU with WebGL 2 fallback** (WebGL-2-only builds failed in Safari with a lost WebGL context;
the WebGPU build works in Chrome and Safari, both multithreaded).
Multithreading needs the page to be cross-origin isolated: `ln.py serve` sends the headers locally;
on itch.io enable **SharedArrayBuffer support** in the game's embed options.

## Project layout

Everything we make lives under `Assets/_Project/` (the underscore sorts it to the top).
Third-party packages go in `Assets/Plugins/` or come through the Package Manager, never into `_Project`.

```
LuckyNumbers/Assets/
└── _Project/
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

Empty folders are not created ahead of time: git does not track empty folders, and Unity
would generate `.meta` files for them that other machines don't have.

## Script conventions

- **Namespace matches folder:** `Scripts/Gameplay/Foo.cs` → `namespace LuckyNumbers.Gameplay`.
- **One assembly definition per Scripts folder** (`LuckyNumbers.<Folder>.asmdef`). This keeps
  recompiles fast and makes dependencies explicit: if `UI` needs `Gameplay`, add it to the
  UI asmdef's `references`. Dependencies should point one way:
  `UI → Gameplay → Core`, never back.
- One public class per file, file named after the class.

## Git rules

- **Always commit `.meta` files** together with their asset. They hold the IDs Unity uses for references.
- **Move or rename assets inside the Unity editor**, or move the `.meta` alongside the file.
- Line endings are LF everywhere (`.gitattributes` + `.editorconfig`), so Mac and Windows don't fight.
- Scenes and prefabs merge best with Unity Smart Merge. To set it up once per machine, see `.gitattributes`.
