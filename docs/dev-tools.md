# Dev tools: `tools/ln.py`

One entry point for everyday tasks. Python 3 standard library only; the same commands work on
macOS and Windows (`py tools/ln.py …` on Windows if `python3` isn't on PATH). Run from anywhere.

| Command | What it does |
|---|---|
| `doctor` | Shows the detected Unity path, editor open/closed, Web build present, `butler` installed |
| `check` | Headless compile; prints `error CS…` lines, exits 1 on failure |
| `build-web [--gfx both\|webgl2\|webgpu] [--version x.y.z]` | Headless Web build → `LuckyNumbers/Builds/WebGL` |
| `serve [--open] [--port 8080] [--gfx …]` | Serves a Web build with the headers multithreading needs |
| `deploy [--target user/game] [--version x.y.z] [--dry-run]` | `butler push` to itch.io (`html5` channel) |
| `version [--notes]` | Prints the version (or its notes) from the top of `CHANGELOG.md` |
| `secrets [--dry-run]` | Pushes `.env` values to GitHub Actions secrets; never prints values |

## Notes

- **`check` and `build-web` need the Unity editor closed.** The tool checks whether a Unity process
  actually holds `Temp/UnityLockfile`, so a lockfile left behind by a crash doesn't block you.
  `--force` skips the check.
- **Finding Unity:** version from `ProjectSettings/ProjectVersion.txt`, then the default Unity Hub
  folder (and Hub's custom install location if set). Override with `UNITY_PATH`.
- **Logs:** `LuckyNumbers/Logs/ln-*.log` (full Unity output); the terminal shows only errors and
  `[WebBuild]` lines.
- **Versions:** `build-web` and `deploy` default to the `CHANGELOG.md` version, so local builds match
  what CI would ship.
- **`--gfx`:** `both` (WebGPU + WebGL 2 fallback) is the default and what ships. `webgl2` and `webgpu` build
  into `Builds/WebGL-WebGL2` / `Builds/WebGL-WebGPU` for comparisons and restore the project setting
  afterwards; serve them with `serve --gfx webgl2`.
- **Unity menu equivalents:** `LuckyNumbers → Web → Apply Settings / Build (WebGPU + WebGL 2) / Build (WebGL 2 only)`.

## Typical loops

```
# changed code
python3 tools/ln.py check

# try it in a browser
python3 tools/ln.py build-web && python3 tools/ln.py serve --open
```
