# 0001: WebGPU with WebGL 2 fallback (2026-11-08)

**Context:** WebGL-2-only build failed in Safari (`prevViewport` null; WebGL context lost/missing). Chrome was fine.

**Decision:** graphics APIs `[WebGPU, WebGL 2]`: project setting, `ln.py build-web` default, CI.

**Result:** Chrome + Safari run on WebGPU, threaded. Build 13.4 MB vs 12.4 MB (shaders compiled twice).
WebGL 2 fallback in a browser without WebGPU: not yet tested.
