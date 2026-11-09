# Web build

Set by `Assets/_Project/Editor/WebBuild.cs` (applied on every build):

| Setting | Value |
|---|---|
| Graphics | WebGPU, WebGL 2 fallback ([0001](decisions/0001-webgpu-with-webgl2-fallback.md)) |
| Multithreading | on |
| Compression | Gzip + decompression fallback (itch.io sends no `Content-Encoding`) |
| Canvas | 1280×720 |
| Version | `-lnVersion` from `CHANGELOG.md` → `Application.version` |

`RenderResolutionCap` limits the internal 3D render resolution to 1280×720 via URP `renderScale`.

## Threads need cross-origin isolation

Required headers: `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Embedder-Policy: require-corp`.
- Local: `ln.py serve` sends them.
- itch.io: enable **SharedArrayBuffer support** in embed options.

Without them the build runs single-threaded. Plain `System.Threading` is single-threaded on Web regardless.

## Verified (2026-11-08, local server)

| Build | Chrome | Safari |
|---|---|---|
| WebGPU + WebGL 2 | WebGPU, isolated, 5 job workers | runs |
| WebGL 2 only | runs, 5 job workers | fails: `prevViewport` null |

`Spikes/WebThreadsTest` prints graphics API, isolation and worker count on screen.
