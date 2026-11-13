# Flight feel

What the camera and the hull do on top of the flight model, so flying reads as smooth and alive. Everything here is
looks only: aim, bolts, hitboxes, collisions and landing checks stay on the steady ship.

## Ship and camera (`content/planet.txtpb` `landing.feel`)

One `FlightFeel` block, shared by the planet, the Flight School and outside the station. Lengths are ship lengths,
rates are per second, and 0 turns a part off.

| Field | Default | Does |
|---|---|---|
| `follow_seconds` | 0.18 | The chase camera springs after its spot (critically damped, no jerk). The lag grows with speed. |
| `look_ahead` | 0.35 | How far it aims into a turn at full turn rate, so you see where you're going. |
| `look_ahead_rate` | 4 | How quickly that lead eases in and out. |
| `roll_lag` | 8 | The view's up trails the ship's, so a roll shows on the hull before the view follows. |
| `roll_follow` | 0.3 | Flight School chase camera: the share of the ship's roll the view takes. The planet uses `assist.camera_bank`; space takes all of it. |
| `speed_fov` | 5 | Degrees wider at top speed (Flight School, outside the station). The planet keeps `assist.speed_fov`. |
| `rumble_angle`, `rumble_offset`, `rumble_frequency` | 0.35, 0.004, 22 | Engine rumble while boosting (full) or braking hard (0.6). Smooth noise. |
| `bank_per_yaw` | 0.18 | The hull banks into turns: degrees of roll per degree/s of yaw. |
| `strafe_bank` | 10 | Degrees of bank at full strafe speed. |
| `max_bank`, `bank_rate` | 24, 5 | The bank's limit and easing. |
| `bob`, `bob_seconds`, `bob_degrees` | 0.012, 3.4, 0.8 | Hovering or slow, the hull bobs and wobbles a little. Less close to the ground or the deck. |
| `bob_speed` | 0.25 | Share of top speed above which there's no bob. |
| `approach_altitude`, `approach_height` | 30, 0.35 | Planet: low (metres) and slow, the camera rises and looks down past the ship, so the pad comes into view. |

Trauma shake (hits, crashes) outside the station still uses `orbit.txtpb`'s `shake_*`, and boost's extra widening
uses its `fov_kick` (see [dogfight.md](dogfight.md#feel-contentorbittxtpb)).

## Space round the station (`content/orbit.txtpb` `life`)

| Field | Default | Does |
|---|---|---|
| `traffic` | 2 | Ships at once crossing far off. They're copies of the fighter in its skins, with no hitboxes. |
| `traffic_distance`, `traffic_speed`, `traffic_seconds` | 14, 90, 14 | Nearest pass beyond the hull (ship lengths), m/s, and about how often one comes by (x0.5 to x1.5). |
| `debris`, `debris_radius`, `debris_size`, `debris_spin` | 40, 14, 0.18, 25 | `PLACEHOLDER_debris` cubes tumbling in a flattened shell round the hull and drifting round it. One instanced draw. |
| `blink_seconds` | 1.6 | `PLACEHOLDER_nav_light`s on the hull's far edges flash in two alternating sets. |
| `radar_spin` | 30 | The `PLACEHOLDER_radar_mast` on the roof turns (degrees a second). |
| `spin` | 0.35 | The station turns in its orbit: the planet and stars wheel slowly round it (degrees a second). |
| `parallax` | 0.04 | The space camera moves with yours: at the edge of `range` it's this share of the planet's diameter, so the planet shifts as you fly. |

The SpaceDust motes (`dust`) are also part of this; see [dogfight.md](dogfight.md).

## Code

| File | What it does |
|---|---|
| `Planet/ChaseCamera.cs` | spring follow, lead into turns, roll lag, rumble and trauma shake, speed FOV |
| `Planet/ShipSway.cs` | the hull's bank and idle bob |
| `Planet/HoverFlight.cs` | exposes `Spin` and `Rumble`, and `PoseWith(tilt)` |
| `Station/OrbitScenery.cs` | traffic, debris, blinking lights, the radar mast |
| `Station/OrbitView.cs` | `Dressing`: the lights and the mast (placeholders) |

Used by `Planet/LandingPresenter.cs`, `Planet/FlightSchoolPresenter.cs` and `Station/OrbitPresenter.cs`.
