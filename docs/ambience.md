# Ambience

Ambient motion so the worlds feel alive: motes drifting round you, sparks off the wreck, cloud shadows sweeping the
ground, fliers wheeling far off, lanterns flickering in the holds.

- Schema: `proto/luckynumbers/v1/ambience.proto` (`Ambience`, `Motes`, `Clouds`, `Flock`).
- Content: `content/ambience/*.txtpb`. Each universe names one for its worlds (`Universe.ambience_ref` in
  `content/universes.txtpb`); the holds have their own (`HoldConfig.ambience_ref` in `content/hold.txtpb`).
- Code: `Scripts/Planet/AmbientLayer.cs` (builds a layer), `Scripts/Planet/AmbiencePresenter.cs` (the crash site),
  `Scripts/HoldScene/HoldAmbiencePresenter.cs` (the holds).
- Shaders: `Art/Shaders/Ambient.shader` (motes and fliers, material `Resources/Ambient.mat`), plus cloud shadows in
  `Ground.shader` and lantern flicker in `HoldKit.shader`.

## Cost

Everything moves in shaders, from `_Time` and the camera. A layer is one static mesh built once (positions unused,
never culled) and one draw. Per frame the CPU does one height lookup under the camera (for the fliers' height) and
sets one global vector; the holds do nothing per frame. No GameObject or script per mote or bird, no allocations.

| Part | Where | Per frame |
|---|---|---|
| Motes (`Ambience.motes`, repeated) | `Ambient.shader`, vertex: drift + wander + wrap into a box round the camera, gust push, billboard | 1 draw per layer, 4 vertices per mote, tiny soft quads (overdraw is negligible) |
| Wreck sparks (`Ambience.wreck`) | same, box standing on the wreck (only when there is one: not on a Clear job) | 1 draw |
| Cloud shadows (`Ambience.clouds`) | `Ground.shader`, per vertex: 3-octave 2D fBm moving with the wind, takes `shadow` of the sunlight | 3 noise octaves per ground vertex, nothing per pixel |
| Fliers (`Ambience.flock`) | `Ambient.shader`, vertex: circle round a flock centre that wraps round the camera, bank, flap and glide | 1 draw, 6 vertices per bird |
| Lantern flicker (`Ambience.lantern_flicker`) | `HoldKit.shader`, per vertex: baked light above `HoldConfig.ambient` scaled by a slow 3D noise + a quick shiver | 1 noise per hold vertex |

## Low-end tier

`Rendering/LowEnd.Active`: each mote layer draws `low_end_share` of its `count` (0.4 for the camera motes, 0.5 for
the wreck sparks in the shipped content). Cloud shadows, fliers and lantern flicker stay: they are per vertex and
cheap. The embers are HDR and bloom on the normal tier; with no post-processing on low-end they are just bright.

## Globals

| Global | Set by | Read by |
|---|---|---|
| `_LnWind` (xy wind m/s, z cloud scale m, w cloud shadow) | `AmbiencePresenter` (zeroed on leaving, and in the hold) | `Ground.shader` clouds, `Ambient.shader` gusts and flock drift |
| `_LnClouds` (x cover, y softness, z ground height under the camera) | `AmbiencePresenter` | `Ground.shader`, `Ambient.shader` (fliers' height) |
| `_LnLanternFlicker` (x amount, y speed, z ambient level) | `HoldAmbiencePresenter` (zeroed on leaving, so the planet's gate stays steady) | `HoldKit.shader` |

Dust motes are lit by `_LnSunColor` and `_LnSkyColor` (the crash site's star and sky tint; the hold's lantern-warm
light), so they take each place's colour.

## Shipped content

| | Home (`ambience/home`) | The Emberdeep (`ambience/emberdeep`) | Holds (`ambience/hold`) |
|---|---|---|---|
| Motes | 220 pollen specks, 24 m box, on the breeze, gusts 1.6 m | 200 grey ash flakes falling, plus 70 rising embers (HDR orange, twinkling) | 160 warm dust specks, 10 m box, hanging almost still |
| Wreck | 40 sparks rising | 70 sparks, a thicker plume | - |
| Clouds | shadow 0.45, 140 m clouds, cover 0.45, wind (4, 1.6) m/s | smoke: shadow 0.55, 110 m, cover 0.5, wind (6, 2.5) | - |
| Fliers | 10 birds in 3 flocks, 110-260 m out, 40-85 m up, 2.6 m span | 5 ash kites in 2 flocks, 140-300 m out, 55-110 m up, 5 m span, slow beats | - |
| Lanterns | - | - | flicker 0.16 at 3.2 |

To try a change: edit the txtpb, `python3 tools/ln.py gen`, enter play mode (or `?ln-universe=emberdeep` on the web).
