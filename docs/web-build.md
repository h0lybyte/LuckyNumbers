# Web build

Set by `Assets/_Project/Editor/WebBuild.cs` (applied on every build):

| Setting | Value |
|---|---|
| Graphics | WebGPU, WebGL 2 fallback ([0001](decisions/0001-webgpu-with-webgl2-fallback.md)) |
| Multithreading | on |
| Compression | Gzip + decompression fallback (itch.io sends no `Content-Encoding`) |
| Canvas | 1280×720 |
| Memory | Heap starts at 32 MB, grows geometrically, capped at 1 GB (threads reserve the whole cap up front; peak use is ~260 MB) |
| Size | Managed stripping Medium, IL2CPP OptimizeSize, wasm DiskSizeLTO (~10 MB). `Assets/_Project/link.xml` keeps our assemblies, VContainer and MessagePipe whole (built by reflection) |
| Template | `Assets/WebGLTemplates/LuckyNumbers`: Unity's Default + an `errorHandler` that keeps a refused pointer lock from stopping the game |
| Model import | `Editor/ArtImport.cs`: anything in `Art/Models/` imports as a static mesh (no rig, animation or materials), skins capped at 512 px and crunched |
| Version | `-lnVersion` from `CHANGELOG.md` → `Application.version` |
| Textures | DXT, plus an ASTC copy of the data file and bundles (`Build/<name>-astc.data.gz`, `StreamingAssets-astc/`). `index.html` loads the ASTC copy when the GPU has ASTC and no DXT (phones, tablets). `ln.py build-web --tex dxt` skips that second pass for quick local builds |

`RenderResolutionCap` limits the internal 3D render resolution to 1280×720 via URP `renderScale`.

## Threads need cross-origin isolation

Required headers: `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Embedder-Policy: require-corp`.
- Local: `ln.py serve` sends them.
- itch.io: enable **SharedArrayBuffer support** in embed options.

Without them the build runs single-threaded. Plain `System.Threading` is single-threaded on Web regardless.

## Why two texture formats

Phone and tablet GPUs can't sample DXT. Given a DXT-only build, Unity unpacks every texture on the CPU at load into
uncompressed RGBA, which takes 4-8x the GPU memory and slows the load. That's where a phone browser tab is likeliest to
run out of memory. The ASTC copy keeps them compressed. The code (wasm, framework) doesn't depend on the texture format:
`WebBuild.BuildAstc` checks this and fails the build if they differ. `?ln-tex=astc` or `?ln-tex=dxt` forces one, and
the console logs `[web] textures: ...`.

## Testing on a phone

- **Frame rate on screen:** add `?ln-fps` to the URL. It shows the average fps, the median and 95th-percentile frame
  time, and the texture format the page picked.
- **Low-end tier:** `Rendering/LowEnd.cs` turns off post-processing (bloom) and the HDR colour buffer, and makes Low
  (540p) the default render quality until the player picks one. It's on when the browser reports 4 GB of memory or less
  (`navigator.deviceMemory`), or for a phone or tablet on WebGL 2. `?ln-low` forces it on and `?ln-low=0` forces it
  off. The console logs `[LowEnd] on (...)`. Blob shadows (docs/shadows.md) fade out nearer and skip pebbles and crates.
- **Frame time, garbage and memory in the console:** add `?ln-perf`. Every 5 s it logs
  `[perf] <scene>: frame ms median / p95 | gc alloc/frame | GCs | mono used/heap | total used/reserved`.
  `webshot` steps `{"throttle":4}` (Chrome's CPU slowdown), `{"mem":1}` (wasm and JS heap) and
  `{"profile":[6,"out.cpuprofile"]}` (a CPU profile) go with it for low-end runs.
- **Android, with threads:**
  1. Connect the phone by USB.
  2. Run `adb reverse tcp:8080 tcp:8080`.
  3. Open `http://localhost:8080` in Chrome on the phone.

  `localhost` counts as a secure context, so cross-origin isolation (and the job threads) works.
- **iPhone, or any phone over Wi-Fi:** `http://<your-ip>:8080` loads, but it isn't a secure context, so it runs
  single-threaded and the frame rate reads low. For a representative run, push to an itch.io draft (`ln.py deploy`)
  and open it there.
- **Try touch on desktop:** `?ln-touch` turns on the touch controls with a mouse. A touch screen held upright gets a
  "turn sideways" prompt.

## Verified (2026-11-08, local server)

| Build | Chrome | Safari |
|---|---|---|
| WebGPU + WebGL 2 | WebGPU, isolated, 5 job workers | runs |
| WebGL 2 only | runs, 5 job workers | fails: `prevViewport` null |

`Spikes/WebThreadsTest` prints graphics API, isolation and worker count on screen.
