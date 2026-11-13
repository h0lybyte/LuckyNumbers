# Flight School

Test-fly the ships and watch their jets work (engine flames, cold-gas puffs).

**Get there:** in the station's Docking Bay, the console by the north wall: **E: Flight School**. Esc brings you back
beside it. Dev shortcut: `?ln-flight` on the page URL, or the inspector's **FlightSchool** button (`` ` ``).

**It is:** an open training deck in space (grid lines every 10 m, major every 100 m, a pad ring in the middle,
pylons round it). The ship flies exactly like the planet's hover ship (`planet.txtpb` `landing.hover`, the skin's
stats), holding itself up the same way, so the lift jets and cold gas behave as over a planet. No fuel, no
crashing; the deck is a floor.

| Key | Does |
|---|---|
| W/S, A/D, Space/C, Q/E, Shift, X, mouse (click to grab it) | Fly, as on the planet |
| V | Camera: chase, side, below, front, orbit |
| M | Swap your fighter for the museum freighter |
| R | Next skin (only here: your ship stays as set in the hangar) |
| Esc | Back to the station |

The HUD shows speed, height, the camera, every lit flame with its share (`main_l 82`) and the cold-gas jets puffing.

**Tuning:**
- **The school:** `content/school.txtpb` (`school.proto`) holds the ship lengths, the freighter's handling, the deck and the cameras.
- **The feel:** `planet.txtpb` `landing.feel` sets the camera's spring, lead and roll, the rumble, and the hull's bank
  and bob ([flight-feel.md](flight-feel.md)).
- **The jets:** `content/engines.txtpb` sets the jets themselves. Check them in the school, or with `ln.py preview exhaust`.

Code: `Planet/FlightSchoolPresenter.cs`, scene `FlightSchool` (`SceneBuilder.BuildFlightSchool`), deck shader
`Art/Shaders/TrainingGrid.shader`. The station side is `STATION_ACTION_FLIGHT_SCHOOL` and `PlayerVoyage.training`
(return beside the console).
