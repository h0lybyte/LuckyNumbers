# Web build

Set by `Assets/_Project/Editor/WebBuild.cs` (applied on every build):

| Setting | Value |
|---|---|
| Graphics | WebGPU, WebGL 2 fallback ([0001](decisions/0001-webgpu-with-webgl2-fallback.md)) |
| Multithreading | on |
| Compression | Gzip + decompression fallback (itch.io sends no `Content-Encoding`) |
| Canvas | 1280×720 |
| Size | Managed stripping Medium, IL2CPP OptimizeSize, wasm DiskSizeLTO (~10 MB). `Assets/_Project/link.xml` keeps our assemblies, VContainer and MessagePipe whole (built by reflection) |
| Template | `Assets/WebGLTemplates/LuckyNumbers`: Unity's Default + an `errorHandler` that keeps a refused pointer lock from stopping the game |
| Model import | `Editor/ArtImport.cs`: anything in `Art/Models/` imports as a static mesh (no rig, animation or materials), skins capped at 512 px and crunched |
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
