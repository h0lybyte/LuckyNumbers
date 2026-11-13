# Blob shadows

The game has no shadow maps, on purpose (low-end Web first). Things on the ground get a fake shadow instead: a soft
dark patch multiplied over the ground under them. `Scripts/Planet/BlobShadows.cs` draws every one in the scene in a
single instanced path (shader `LuckyNumbers/BlobShadow`, material `Resources/BlobShadow.mat`).

## Who casts one

| Caster | Where it joins | Ground |
|---|---|---|
| Monsters (VAT crowd, skinned, placeholder blocks) | `MonsterView` (`BlobShadows.Add`) | told: `MonsterView.Grounded` (CombatPresenter's raycast; DeepCreatures' cell floor) |
| You on foot (planet, hold, station) | `FirstPersonPresenter.Start` | probed; hidden while you're in your ship's seat |
| Your fighter (landing, parked) | `LandingPresenter` via `CrashSitePresenter.ShadowShip` | probed; shows from `ship_fade_height` up |
| The freighter wreck | `CrashSitePresenter` once the curtain opens | probed |
| Pebbles (minor) | `CrashSitePresenter.Build` | probed |
| Relics lying round the wreck, dropped items | `RelicsPresenter` | told (the surface they hover over) |
| Hold relic caches (chests) | `RelicCaches.Build` | told (the cell floor) |
| Station range crates (minor) | `RangeRoom` | probed |

Not casting: rogues and the satellite (they fly in space, no ground), hoist cages, puzzle keys and doors in the hold.

## Adding a caster

```csharp
var spec = BlobSpec.Centred(size);          // root in the middle of its box; or new BlobSpec { Size, Bottom = 0 } for a root at its feet
spec.Probe = true;                          // find its own ground; else call SetGround(y) on what Attach returns
spec.Minor = true;                          // small stuff: skipped on low-end devices
BlobCaster.Attach(gameObject, spec);        // joins while the GameObject is enabled, leaves when it's disabled or destroyed
```

`BlobSpec.Fit(root, renderers, out spec)` measures a model's box (a ship's hull, a relic). `FadeHeight` (gone this far
off the ground), `FarScale` (seen further), `Strength` and `Offset` (footprint off the root) are optional.

A probed caster reads its ground only when it has moved 2 cm across and is in range: the terrain's surface
(`GroundView.SurfaceAt`, the near chunk's collider alone) where there is terrain, else one ray down
(`ProbeMask`, default layers). Static things probe once.

## Shape

- The footprint's ellipse (`spread` x the caster's box) along its facing, so a long body casts a long blob.
- A soft rim (`softness`), the body (`body_strength`) and a darker contact core under its middle (`core_strength`,
  `core_size`).
- Under a sun (`BlobShadows.Sunlit`: the crash site sets it; `_LnSunPosition`): the ellipse is dragged away from the sun
  by `sun_stretch` x its height x cot(elevation) (cot capped at `max_cot`, the sweep at `max_sweep` x its height). The
  sweep fades to `tip_fade` at its far end and softens as it goes; a low sun makes it fainter overall (`low_sun_fade`).
  A high sun gives a tight, dark blob.
- Indoors (the hold, the station: not `Sunlit`): straight down, full strength.
- Off the ground (a hop, a jump, a hovering relic, a ship coming in): fainter, smaller (`rise_shrink`), softer
  (`rise_soften`) and swept further from the sun; gone at its own height (or `FadeHeight`).
- Fades out from `fade_near` to `fade_far` from the camera (measured to the blob's near edge; ships x `ship_far_scale`).
- On terrain each blob is bent to the hills: seven height samples give its slope (in its matrix) and curvature (per
  instance, on a 6 x 6 grid in the vertex shader).

Tunables: `content/shadows.txtpb` (`BlobShadowConfig`, `GameContent.Shadows`). Unset fields fall back to the defaults
in `BlobShadows.Read`.

## Cost

One Burst job over the casters' transforms (fade, shape, cull, terrain bend), then one `RenderMeshInstanced` per 64
visible blobs (2 matrices + 2 vectors each: 10 KB of WebGL 2's 16 KB uniform buffer). No per-frame allocations.
Low-end (`Rendering/LowEnd`): fade distances x `low_end_distance`, minor casters off.

- `?ln-noblobs` (or `-lnNoBlobs`): none at all, for A/B frame-time tests.
- `?ln-perf`: every 5 s, `[BlobShadows] N blob(s), M drawn in K instanced draw(s)`.
