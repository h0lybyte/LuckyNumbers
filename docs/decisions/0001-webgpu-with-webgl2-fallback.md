# 0001: Web builds use WebGPU with WebGL 2 fallback

- **Status:** accepted
- **Date:** 2026-11-08

## Context

The first multithreaded Web build used Unity's default graphics API for Web, WebGL 2 only.

- **Chrome:** worked.
- **Safari:** failed at startup: `TypeError: null is not an object (evaluating 'prevViewport[0]')`.
  That's Emscripten's canvas-resize code reading `gl.getParameter(GL_VIEWPORT)` from a WebGL context
  that was lost or never created.

We then built with the graphics API list `[WebGPU, WebGL 2]` (`ln.py build-web --gfx both`).

| Browser | Graphics chosen | Threads | Result |
|---|---|---|---|
| Chrome | WebGPU | 5 workers, isolated | works |
| Safari | WebGPU | 5 workers, isolated | works |

## Decision

Ship **WebGPU first, WebGL 2 as fallback**. It's the committed project setting, the `ln.py build-web`
default and what CI builds. `--gfx webgl2` / `--gfx webgpu` remain for comparisons and don't change
the committed setting.

## Consequences

- Safari works; browsers with WebGPU get the modern API (compute shaders, lower CPU overhead).
- Every shader compiles twice: builds are slower and ~1 MB larger (13.4 MB vs 12.4 MB).
- Browsers without WebGPU fall back to WebGL 2. Safari without WebGPU would still hit the WebGL issue.
- New URP or rendering features must be checked on both backends. The test spike reports which
  backend is active (`Graphics:` line).
