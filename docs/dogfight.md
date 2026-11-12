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

Rogue ships are copies of the fighter in their own skins. Each one:

- chases you and leads its shots, with a few degrees of spread, so dodging pays
- breaks off when it gets inside `pass` metres, then comes round again

| Rogue | Role | Joins from |
|---|---|---|
| Scout | fast, fragile | danger 1 |
| Raider | standard | danger 1 |
| Gunship | slow, armoured, hits hard | danger 3 |

A kill pays `xp` and a `bounty` in credits straight away.

## Satellite uplink job

`JOB_KIND_UPLINK` in `content/jobs.txtpb`:

1. The relay sits `satellite.distance` ship lengths out from the docking bay. A ring of lit markers shows its range.
2. Stay inside the ring while it uploads. Leaving the ring pauses the upload.
3. Rogue waves arrive as the upload runs. Each wave has `target` + `target_per_danger` x danger rogues, and there
   are `waves` + danger / 2 waves.
4. Upload done: dock, and Dispatch pays the job.

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
| `Station/Dogfight.cs` | the fight: rolls, layers, rogue AI, waves, upload |
| `Station/OrbitPresenter.cs` | guns, the satellite, the memory restore |
| `Station/StationPresenter.cs` | uplink launch, the payout on docking, the restore call, game over |
| `Core/DebtWatch.cs` | debt tiers |
| `Core/SavePresenter.cs` | `RunEnded` wipes the slot |
| `web/src/dogfight.tsx` | HUD, debt chip, game-over card |
