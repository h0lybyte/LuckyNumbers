# Changelog

Releases are driven by this file: the **top `## x.y.z` entry is the game version**.
Bumping it on `main` builds the Web player, uploads it to itch.io with that version,
and tags/releases `vx.y.z` on GitHub. Editing notes without changing the version does not rebuild.

## 0.1.0 (2026-11-08)

- First Web build: WebGPU with WebGL 2 fallback, multithreaded Burst jobs (Chrome + Safari)
- Internal render resolution capped at 1280×720
- Project layout under `Assets/_Project`, dev task CLI (`tools/ln.py`), CI/CD to itch.io
