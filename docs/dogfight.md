# Space combat, satellite jobs, memory restore, debt

Fighting other ships in space, outside the station (`GameScene.Orbit`). All numbers live in
`content/dogfight.txtpb` (`dogfight.proto`), so tuning needs no code.

**Try it:** `?ln-dogfight` on the page URL puts you outside the station with a practice uplink and rogue waves
(no job needed). In the inspector (`` ` ``), **Rogues** sends a wave at you. In the game, take a **Satellite uplink**
job off Dispatch's board: boarding flies you out of the bay instead of down to a planet.

## A shot

Bolts fly and hit for real, so aim decides whether a shot lands. A bolt that connects rolls
**d20 + bonus against the target's evasion** to grade the hit:

| Roll | Grade | Damage |
|---|---|---|
| natural 1, or more than 2 under evasion | graze | half the dice |
| up to 2 under evasion, or up to 4 over | hit | the dice |
| 5 or more over | strong | the dice + 1 die, at least the dice's best |
| natural 20 | crit | the dice's best + another roll |

Your bonus (**gunnery**) is the sheet's Piloting skill, plus 0 to 2 from your lucky number, plus 1 from the cyborg
targeting implant. A rogue's bonus is its `attack`. Every roll shows in the roll feed.

## Defences

Damage goes through three layers:

1. **Shield**: a pool of points that soaks damage first. It recharges after `shield_delay` seconds without a hit.
2. **Armour**: a flat cut on every hit once the shield is down. It's strong against many small hits and weak
   against big ones.
3. **Hull**: at 0 the ship is destroyed.

Your stats come from the skin and its upgrades:

- hull = `defense.hull` x toughness
- shield = `defense.shield` x shield
- armour = `defense.armor` x armor
- evasion = `defense.evasion` + `evasion_per_agility` x (agility - 1)

Upgrades trade defence for handling. Heavy Plating adds armour but costs speed and agility. The Shield Capacitor
adds shield but costs fuel efficiency. The Aegis Projector (black market) adds a lot of both and is very slow.

The three guns naturally counter each other:

| Gun | Cooldown | Dice | Good against |
|---|---|---|---|
| Prism repeater | 0.11 s | 1d3 | shields (armour shrugs it off) |
| Basic laser | 0.28 s | 1d4 | everything, a little |
| Plasma lance | 0.75 s | 2d8 | armour and hull |

## Rogues

Rogue ships are copies of the fighter in their own skins:

| Rogue | Role | Joins from |
|---|---|---|
| Scout | fast, fragile, jinks when hurt and runs to recharge | danger 1 |
| Raider | standard | danger 1 |
| Gunship | slow, armoured, hits hard, stays on you | danger 3 |

A kill pays `xp` and a `bounty` in credits straight away.

### Pilot AI (`Station/RogueBrain.cs`)

Each rogue runs a small state machine. Every frame it picks a goal, a speed and whether it may fire.

| State | What it does | Leaves when |
|---|---|---|
| Patrol | circles where it came in | you come within `aggro_range`, or you shoot it |
| Approach | closes on you | in range: it attacks if an attack token is free, else it flanks |
| Attack | flies at where you'll be (its lead) and fires | inside `pass` metres, or after `attack_seconds`: break off |
| Break off | overshoots out to one side, then switches sides for next time | after 1.5 to 2.6 s: attack again (chance: `aggression`), else flank |
| Flank | swings round to your side and behind you, following as you turn | it gets there: attack if a token is free, else flank the other side |
| Evade | jinks to a new heading every half second, away from your nose | after about 2 s: flank |
| Retreat | runs out to recharge its shield | its shield is full, or after 8 s: approach |

- **Evade** starts when it's hit with its shield down and its hull below `evade_hull`.
- **Retreat** starts when its hull is below `retreat_hull` with its shield down. 0 means never.
- **Attack tokens:** only `attackers` rogues (default 2) may make a run at once. The rest flank and wait their
  turn, so a wave comes at you from several sides instead of in one line.

### Flying (`Station/RogueFlight.cs`)

Rogues fly like fighters, not turrets:

- **Bank and pull:** for a big turn a rogue rolls until the goal is "overhead", then pulls its nose up into it,
  with a little rudder. Small corrections just nudge the nose and level the wings.
- **Eased rates:** pitch, yaw and roll each build up and die away at a limited angular acceleration, so turns start
  and stop smoothly.
- **Energy:** hard turns bleed speed, and the speed itself changes at a limited rate.
- **Slide:** the velocity swings round after the nose, so a hard turn carries a little drift.
- **Weave:** on the way in for an attack run, a rogue weaves side to side and up and down, then steadies to shoot
  as it closes.
- **Break off:** usually a climb up and over (sometimes a dive under) and out to one side, flown as an arc.

### Pathfinding (`Station/SpaceNav.cs`)

Space is empty except for the station's hull blocks, so two things are enough:

- **Detours:** if the straight line to the goal crosses the hull, the rogue flies to the best detour point instead.
  Detour points are the corners and faces of a box round the whole station, pushed out by `clearance`. It picks
  the one it can see whose two legs are shortest. This runs every frame, so the detour moves on as it rounds the
  hull.
- **Steering:** a ray `lookahead` seconds down its velocity. If the ray hits a hull block, the rogue is pushed
  mostly along that face (to keep going round) and partly out from it.
- **Spacing:** a soft push keeps rogues apart and off your nose when they aren't attacking. A hard push-out stops
  any ship from passing through yours, or rogues through each other.
- **Line of sight:** a rogue never fires through the station.

The HUD target card shows the state of the rogue you're on (for example "FLANK").

## Satellite uplink job

`JOB_KIND_UPLINK` in `content/jobs.txtpb`:

1. The relay sits `satellite.distance` ship lengths out from the docking bay. A ring of lit markers shows its range.
2. Stay inside the ring while it uploads. Leaving the ring pauses the upload.
3. Rogue waves arrive as the upload runs, and they ramp up so the start isn't overwhelming: the first wave is a
   single rogue, and each wave after it brings one more, up to `target` + `target_per_danger` x danger. There are
   `waves` + danger / 2 waves.
4. Upload done: dock, and Dispatch pays the job.

### Tiers

A satellite job's danger (1 to 5) sets its tier (`uplink_tiers` in `content/dogfight.txtpb`). The tier's name and
blurb show on the job board, and its bonus is added to the pay. Bounties pay on top.

| Danger | Tier | Waiting at the satellite | Ambush from behind | Biggest wave | Job pay |
|---|---|---|---|---|---|
| 1 | Routine | none | none | 1 | 700 |
| 2 | Contested | 1 | none | 2 | 1,200 |
| 3 | Hostile | 1 | 1, at 50% of the upload | 2 | 1,850 |
| 4 | Ambush | 2 | 2, at 40% | 3 | 2,550 |
| 5 | Black site | 2 | 3, at 35% | 3 | 3,300 |

- **Waiting rogues** circle the satellite, then jump you once you come within `ambush_range` ship lengths (or you
  shoot one). The page flashes "AMBUSH: rogues at the satellite!".
- **The ambush** spawns behind you partway through the upload: "AMBUSH: behind you!".

`?ln-dogfight` runs a practice uplink at danger 3 (Hostile) with no job behind it, so only bounties pay. In orbit,
the dev warps `satellite` (jump beside the relay), `uplink` (finish the upload) and `dock` (dock at once) help test a job's payout.

The relay is a placeholder (`PLACEHOLDER_satellite`, plain blocks) until it has a model.

## Destroyed: memory restore

Every pilot is half human, half cyborg (`content/character.txtpb` heritage). The memory chip keeps a backup of you,
made at every dock. When the hull hits 0:

1. The ship explodes and the page shows the uplink card.
2. You wake at the station in a new body, beside the rebuilt ship.
3. Dispatch calls with the bill: `rebuild_share` x (`hull_value` + what you paid for upgrades), plus the body `fee`.
   You keep your upgrades.
4. Carried relics are lost, and a satellite job you were on fails.

## Debt

Credits can go negative, from a rebuild or from the planet's rescue-drone fee.

| Credits | Tier | Effect |
|---|---|---|
| below 0 | in debt | a chip on the HUD; jobs and bounties pay it down |
| below -(`collections` + `per_level` x (level - 1)) | collections | the outfitter notes the hold (you can't afford anything anyway) |
| below -(`game_over` + `per_level` x (level - 1)) | game over | the creditors take your chip |

Game over is checked when the station's homecoming or restore call ends. The page shows the card, the run's save
slot is deleted (`RunEnded`), and the button reloads to the title.

## Balance targets

Tune toward these fight lengths:

- A baseline fighter kills a Raider in about 6 to 8 seconds of real dogfighting.
- One Raider alone takes about 25 seconds or more to kill you; two take about half that.

Rough maths: two basic lasers do about 17 damage per second if every shot lands. A Raider has 30 shield and 40 hull
behind 1 armour, so it lasts about 4 seconds at full accuracy, or about 8 at 50%.

## Code

| File | What it does |
|---|---|
| `Station/Dogfight.cs` | the fight: rolls, layers, rogue flying, waves, upload |
| `Station/RogueBrain.cs` | the rogues' pilot state machine and attack tokens |
| `Station/SpaceNav.cs` | routing round the station, hull avoidance, line of sight |
| `Station/RogueFlight.cs` | the rogues' flight model: bank and pull, eased rates, energy |

## Later

- Squadrons on both sides: wingmen flying with you against enemy squads. The attack tokens and flanking already
  give enemy groups some shape.
| `Station/OrbitPresenter.cs` | guns, the satellite, the memory restore |
| `Station/StationPresenter.cs` | uplink launch, the payout on docking, the restore call, game over |
| `Core/DebtWatch.cs` | debt tiers |
| `Core/SavePresenter.cs` | `RunEnded` wipes the slot |
| `web/src/dogfight.tsx` | HUD, debt chip, game-over card |
