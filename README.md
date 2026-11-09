# LuckyNumbers

Unity 6 (6000.6.8f1, URP) game for the **Web**: WebGPU with WebGL 2 fallback, multithreaded Burst jobs,
published on itch.io at **[kbve.itch.io/lucky-numbers](https://kbve.itch.io/lucky-numbers)**.

## Quick start

1. Open the **`LuckyNumbers/`** folder (not the repo root) in Unity Hub with **6000.6.8f1**.
2. Check your machine: `python3 tools/ln.py doctor`
3. Try it in a browser (editor closed):
   ```
   python3 tools/ln.py build-web
   python3 tools/ln.py serve --open
   ```

## Release

Add a new `## x.y.z` entry at the top of [`CHANGELOG.md`](CHANGELOG.md) and merge to `main`.
CI builds, uploads to itch.io, and tags `vx.y.z`. See [Releasing](docs/releasing.md).

## Docs

| | |
|---|---|
| [Getting started](docs/getting-started.md) | setup on Mac/Windows, Smart Merge, line endings |
| [Project structure](docs/project-structure.md) | where files go, script + git conventions |
| [Dev tools](docs/dev-tools.md) | `tools/ln.py`: check, build, serve, deploy, secrets |
| [Web build](docs/web-build.md) | threads, WebGPU, compression, 720p cap, verified browsers |
| [Releasing](docs/releasing.md) | CHANGELOG-driven CI/CD, secrets, itch.io setup |
| [Decisions](docs/README.md#decisions) | why: WebGPU fallback, CHANGELOG-driven releases |

## Ground rules

- Everything we make goes under `LuckyNumbers/Assets/_Project/`.
- Always commit `.meta` files with their assets; move/rename assets inside the Unity editor.
- Never commit `.env`, `*.ulf` or build output (all gitignored).
