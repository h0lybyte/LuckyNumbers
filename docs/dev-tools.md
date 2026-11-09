# tools/ln.py

Python 3 stdlib. Windows: `py tools/ln.py …`.

| Command | Does |
|---|---|
| `doctor` | Show Unity path, editor open/closed, build present, butler |
| `check` | Headless compile; prints C# errors; exit 1 on failure |
| `build-web [--gfx both\|webgl2\|webgpu] [--version x.y.z]` | Headless Web build → `LuckyNumbers/Builds/WebGL` |
| `serve [--open] [--port N] [--gfx …]` | Serve build with COOP/COEP headers (default :8080) |
| `deploy [--target user/game] [--version] [--dry-run]` | `butler push` to itch.io `html5` |
| `proto` | Validate `proto/` schemas and `content/*.txtpb` (pinned protoc) |
| `gen [--check]` | Generate C# from `proto/` + `content/` into `Assets/_Project/Generated` |
| `roslyn` | Build the Roslyn source generator (`tools/roslyn/`) into `Assets/_Project/Generators` |
| `scenes` | Rebuild Title/Intro scenes from code (`Editor/SceneBuilder.cs`), editor closed |
| `preview [space\|ship]` | Render the space background or the fighter to `LuckyNumbers/Logs/preview-<what>.png` |
| `webshot [--wait text] [--steps JSON]` | Drive the served Web build (click, drag, scroll, sleep seconds, shot, `{"fps":5}`). `LN_UNCAPPED=1` lifts vsync to show real frame cost (Node 22+, Chrome) |
| `version [--notes]` | Version or notes from top of `CHANGELOG.md` |
| `secrets [--dry-run]` | Push `.env` to GitHub Actions secrets; values never printed |

- `check`/`build-web` need the editor closed (stale lockfiles are ignored; `--force` overrides).
- Unity located via `ProjectVersion.txt` + Unity Hub folder; override with `UNITY_PATH`.
- Version defaults to `CHANGELOG.md`. Logs: `LuckyNumbers/Logs/ln-*.log`.
- `--gfx webgl2|webgpu` build to `Builds/WebGL-<Variant>` and leave project settings unchanged.
