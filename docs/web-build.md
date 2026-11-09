# Web build

All settings are applied in code by `Assets/_Project/Editor/WebBuild.cs` (`ApplySettings`, also run by
every build), so local builds, the Unity menu and CI produce the same player.

| Setting | Value | Why |
|---|---|---|
| Graphics | **WebGPU, WebGL 2 fallback** | WebGL-2-only failed in Safari; see [decision 0001](decisions/0001-webgpu-with-webgl2-fallback.md) |
| Multithreading | **on** | Burst jobs run on worker threads in the browser |
| Compression | **Gzip + decompression fallback** | itch.io sends no `Content-Encoding` header; the loader decompresses in JS |
| Canvas / window | **1280×720** | matches the internal render cap |
| Internal render resolution | **≤ 1280×720** | `RenderResolutionCap` lowers URP render scale on bigger screens |
| Version | from `CHANGELOG.md` | stamped into the build as `Application.version` (`-lnVersion`) |

## Multithreading needs cross-origin isolation

Browsers only allow `SharedArrayBuffer` (which Unity's worker threads need) on pages served with:

```
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

- **Locally:** `python3 tools/ln.py serve` sends them. A plain `python -m http.server` doesn't, and threads
  silently fall back to one.
- **itch.io:** tick **SharedArrayBuffer support** in the game's embed options.
- Side effect: the page can't load cross-origin resources that don't opt in (some CDNs, ads, analytics).

What runs threaded: Unity C# jobs (Burst) and engine internals like PhysX. Plain `System.Threading`
code is still single-threaded on the Web.

## Render resolution cap

`Scripts/Rendering/RenderResolutionCap.cs` starts before the first scene and sets
`renderScale = min(asset scale, 1280/width, 720/height)` whenever the window size changes. A 1080p
window renders the 3D scene at 720p and URP upscales; UI in Screen Space - Overlay stays native.
It restores the pipeline asset's value when play mode ends (asset edits persist in the editor).

## Verified results (v0.1.0, 2026-11-08)

| Browser | Graphics | Cross-origin isolated | Job workers |
|---|---|---|---|
| Chrome | WebGPU | yes | 5 |
| Safari | WebGPU | yes | 5 |
| Safari, WebGL-2-only build | — | — | fails: `prevViewport` null (lost WebGL context) |

Unity caps Web job workers well below the core count (5 on a 16-core Mac). Reproduce with
`Spikes/WebThreadsTest`, which runs automatically in any build and prints its report on screen.

## Known console messages (harmless)

- *"You can reduce startup time… Content-Encoding: gzip"*: expected with decompression fallback.
- *"AudioContext was not allowed to start"*: browser autoplay policy; audio starts on first input.
- *"Shader 'Hidden/Universal Render Pipeline/…DepthOfField' not supported or stripped"*: unused URP
  post effects; disable them in the renderer once the real scene exists.
