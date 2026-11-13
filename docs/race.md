# Circuit racing

Racing other pilots round a seeded course outside the station (`GameScene.Orbit`). All numbers live in
`content/race.txtpb` (`race.proto`), so tuning needs no code.

**Try it:** add `?ln-race` to the page URL for a practice race (no job, no pay). In the game, take a **Circuit race**
job off Dispatch's board: boarding launches you onto the grid instead of going down to a planet.

## The course (`Station/RaceTrack.cs`)

- The seed comes from your lucky number, the job and the trip count, so every race gets a new course and the same
  race is always the same course.
- `points` control points sit round a loop `radius` m across. Each is pulled in by up to `radius_jitter` and moved up
  or down by up to `height` m. A smooth curve (Catmull-Rom) runs through them.
- The start line is `start_distance` m out from the docking beacon, and the loop curves away from the station.
- Gates are spaced evenly every ~`gate_spacing` m. Gate 0 is the start / finish line (gold); the next gate you need
  glows cyan. Fly through gates in order. Missing one says so, and you have to go back through it.
- A row of `cube_row` mystery cubes sits between two gates every `cube_every` gates. A taken cube comes back after
  `cube_respawn` s.
- `hazards` rocks sit off to the sides, never on the line. They're solid.

Gates, cubes, rocks and mines are placeholder blocks (`PLACEHOLDER_race_*`, `PLACEHOLDER_mystery_cube_*`) to swap for
models later.

## Flying

- You fly as you do outside the station, at `speed_scale` of your ship's speed (racing trim). You also get an extra
  `accel` m/s² while you thrust forward, so the launch doesn't crawl.
- Your ship's speed and agility still matter.
- Guns are cold: the cubes are the only weapons. No rogues come.
- You're frozen for the `countdown` on the grid, with the rivals two abreast round you in the middle of the pack.

## Rivals (`Station/Race.cs`)

There are `rivals` AI pilots. Each gets a name and a fighter skin from `pilots`, shuffled per race.

- **Line:** a rival follows the line `lookahead` m ahead, in its own lane.
- **Cubes:** with chance `cube_seek`, it swerves for a cube in the next row.
- **Avoidance:** it keeps off other ships' paths (Steering.AvoidShip) and the rocks and hull (SpaceNav).
- **Flight model:** it flies like a rogue (RogueFlight): banking into turns and bleeding speed in hard ones.
- **Pace:** a share of the baseline fighter's boosted top speed, rolled between `pace_min` and `pace_max`, plus
  `pace_per_danger` per danger level over 1.
- **Rubber band:** a rival is up to `rubber_band` faster when 1000 m behind you, and that much slower when ahead.

## Mystery cubes

Fly through a cube and it rolls **d20 + bonus**:

- The bonus is your lucky number's share (0..`roll.luck_max`, players only), plus `catch_up_per_place` for each place
  behind the leader, up to `catch_up_max`.
- The cube's colour picks its table. The outcome is the one with the highest `min` at or under the total.
- A **natural 1** is the `fumble` (a backfire) on any cube. A **natural 20** gives the cube's `nat20`.

Rivals take cubes and roll too (catch-up bonus only). Every roll shows in the feed. Yours also gets a card with the die,
the sum and what it did.

| Cube | Low | Mid | High | Nat 20 |
|---|---|---|---|---|
| Green **Spark** (common) | Nudge (x1.2 speed) | Shield charge | Turbo (x1.45) / Overdrive (x1.65) | Afterburner (x1.9, 5 s) |
| Blue **Static** (tactical) | Mine behind you | EMP (slows all within 220 m) | Zap the racer ahead / the leader | Storm (EMP 600 m) |
| Gold **Gilded** (rare) | Turbo | Phase (4 s) | Warp 1 gate / 2 gates | Fold space (3 gates) |
| Red **Risk** (risky) | Stall (you're slowed) | Dud | Overdrive (x1.9) / Swap with the racer ahead | Hyperdrive (x2.2) |

### Effects

| Effect | What it does |
|---|---|
| Boost | Top speed x `power` for `seconds`, plus a kick forward |
| Shield | `count` charges (at most `max_shields`). Each soaks one hostile effect |
| EMP | Everyone else within `radius` slowed to `power` for `seconds` |
| Zap | The racer just ahead (or the leader: `leader`) slowed |
| Mine | Dropped behind you; the next rival through it is slowed |
| Phase | Ghost through ships and rocks for `seconds`; hostile effects miss |
| Warp | Jump `count` gates on (never across the finish line) |
| Stall | You're slowed (the backfire) |
| Swap | Change places with the racer just ahead |

Hostile effects (EMP, zap, mine, swap) are stopped by phase, or by a shield charge, which is used up.

## The finish

Cross the line after the last lap. You see your place, and `finish_seconds` later (or E) you're back at the station.

- **Pay:** the job's pay x `pay_shares[place]` (1st gets all of it). Past the list, nothing, and the job counts as
  failed. Dispatch pays at the station, like the other jobs.
- **XP:** `xp[place]`. Practice races pay no credits and no XP.
- **Did not finish:** if you're still racing `dnf_seconds` after the winner, you're out.
- Docking at the beacon mid-race abandons it.

## The HUD (`web/src/race.tsx`, `RaceState`)

- **Top:** lap, place, time (and best lap), metres to the next gate, shield charges and running effects.
- **Right:** standings (your line highlighted), and the last six cube rolls under them.
- **Centre:** a banner for the countdown, laps, boosts and hits; your roll card; the finish card.
