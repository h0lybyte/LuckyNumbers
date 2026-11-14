# Changelog

Releases are driven by this file: the **top `## x.y.z` entry is the game version**.
Bumping it on `main` builds the Web player, uploads it to itch.io with that version,
and tags/releases `vx.y.z` on GitHub. Editing notes without changing the version does not rebuild.

## 0.4.3 (2026-11-13)

- A new run tells you where to go: head to the Hub, where Dispatch's call comes in
- Dialogue no longer covers the E prompt and objective line at the bottom of the screen
- The station objective shows once, top left, instead of twice
- Weapon pickups say how to use them, and the hand rig's debug numbers no longer pop up in game
- Quieter browser console while holding a weapon

## 0.4.2 (2026-11-13)

- Saves and settings now carry over between releases on the web. Earlier builds lost them with every new
  upload. Saves left by 0.4.1 and older come back the first time you launch this version
- Saves keep working as the game changes. Renamed items, upgrades, skins and universes still load. Removed
  items are paid back at what they were worth, and anything else that's gone falls back to the default. A slot
  that can't be read is kept as a backup instead of being overwritten
- Title music: Break Beat Jazz Fusion by Rory_Donovan, streamed by the page so it doesn't add to the download
- Fix: jumping in the station no longer puts you through the ceiling or into the sloped corridor walls

## 0.4.1 (2026-11-13)

- Circuit racing round the station: race jobs on Dispatch's board against ten rival pilots, paid by where you
  finish. Fly through mystery cubes to roll a d20 for boosts, shields, mines, EMPs, zaps, phasing or warps
- Defend jobs: plant a beacon on the surface and hold it against waves of monsters
- Salvage made clear: walk up to the wrecked freighter and press E to flag it for salvage. Dispatch's crews
  take it from there, and you fly back to the station to get paid. Relics lying round it still sell on top
- One E prompt at a time on foot, in the open and down in the holds: the nearest thing you can use wins, and
  the job objective moves to the top left so it no longer hides the prompt
- The docking bay's launch console now says it is free flight with no landing; jobs leave when you board your
  ship
- Livelier feel: smoother first-person movement, a smoother chase camera with hull sway, traffic round the
  station, ambient motion on worlds and in holds, and soft blob shadows under the player, ships, props and
  relics
- Smarter rogue pilots that steer like a flock and can crash. Deep creatures share one pathfinding field, so
  big chases cost less
- One Settings panel for the title and in game
- Fixes: a reboarded ship no longer sinks into the ground; touch sticks step aside while Settings is open; the
  race start snaps the chase camera

## 0.4.0 (2026-11-12)

- Space combat outside the station. Lasers fire real bolts, and a bolt that hits rolls d20 + your gunnery
  against the target's evasion for a graze, hit, strong hit or crit. Every ship has a shield that recharges,
  armour that cuts each hit, and a hull
- Rogue pilots fly like fighters: they bank into turns, make attack runs, break off, flank you and run to
  recharge. They route round the station and never fly through it or your ship
- Satellite uplink jobs on Dispatch's board: fly out to a relay and hold position while it uploads, as rogue
  waves come at you. Five tiers from Routine (700 cr) to Black site (3,300 cr). The dangerous ones have rogues
  waiting at the relay and an ambush from behind mid-upload. Kills pay bounties on top
- You're half human, half cyborg. When your ship is destroyed, your memory chip wakes you at the station in a
  new body and Dispatch bills you for the rebuild. Credits can go negative, and if you fall too far into debt
  the creditors take your chip: game over, and the save is gone
- Ship upgrades: the Outfitter and the Black Market in the docking bay sell parts for speed, handling, fuel,
  shields, armour and payouts, plus new guns (Plasma Lance, Prism Repeater). Heavier defences slow the ship
- The Emberdeep: a second universe of red stars and forge worlds, chosen on the title. Its planets have
  dwarven holds. Walk in through the gate to a great hall, then down into endless mines with ladders, stairs,
  great shafts with hoists, locked doors and keys, levers, sealed depths, drop shafts, relic strongboxes and
  creatures
- New monsters: the Rattleframe skeleton and the Plague Arcanist, who keeps its distance and lobs plague
  flasks that splash where they land
- A working Settings panel on the title: look speed, invert look, render quality and a frame-rate meter
- The station view now orbits with its planet, and planets move round their stars a third as fast
- Runs better on phones and low-end machines: phone-friendly textures, a turn-sideways prompt, less
  per-frame work and garbage, a low-end render tier and a 1 GB memory cap
- Fixes: the barrel roll spins the ship, not the camera; one standoff card at a time; the monsters-left count
  drops when one flees

## 0.3.4 (2026-11-11)

- perf(planet): build ground chunks in Burst jobs, cook colliders off main thread
- perf: pool bolt and flash shapes (no GameObject churn per shot)
- refactor: [Flags] bytes for input latches and monster state
- refactor: generic Pool<T> + shared Projectile message for gun bolts
- perf: one BoltField for all bolts (Burst rays, batched raycasts)
- fix: allow boarding the ship on Clear jobs
- build: turn off the Unity splash screen on Web
- feat: Addressables foundation; scene preload no longer holds loads
- perf: GPU-instanced bolts and flashes (no GameObjects)
- fix(spike): LibsSmokeTest passes on Web again
- feat: weapons load from Addressables instead of the scenes
- feat(planet): finds auto-roll and loot into the pack, no card
- feat(web): touch controls (dual sticks + buttons) via a virtual gamepad
- perf: cut per-frame and per-item string allocations (ZString)
- feat(jobs): no back-to-back Retrieve jobs for the same relic
- feat(relics): the Roman Vase (Pliny the Elder)
- Freighter hull via Addressables, behind a scene curtain
- feat(relics): ln.py preview relics
- Finds tumble a d20 before they open
- feat(relics): relic models as addressable prefabs; ln.py relic
- feat(relics): paint the Roman Vase with its own textures

## 0.3.3 (2026-11-11)

- feat(combat): per-gun fire rates and dice; strong/crit damage floors
- ci: warm retry before a cold build; Web release starts on the release commit
- feat(save): save slots on the title; name your character
- feat(station): Flight Academy room north of the docking bay
- feat(orbit): launch from the bay, fly round the station, dock again
- feat: ship on the map, 10x fuel, wreck only on salvage, range aisles, pick jobs at the ship
- fix(flight): barrel roll actually carries you sideways
- fix: no wreck talk on Clear sites; the sidearm is drawn as placeholder blocks
- refactor(control): one bit-mask for who holds the player
- chore: ln.py worktrees + moon game:worktrees / worktrees-prune
- feat(ship): landing legs that come out when it's landed or docked
- feat(combat): the Aberration, the first monster with a real model
- chore(moon): game:worktree-new, and worktrees-prune cleans origin too

## 0.3.2 (2026-11-11)

- chore: set up moon task runner
- feat(ship): fly anywhere, fuel, ship stats (#42), twin lasers
- feat(items): relics are items (items design step 3)
- feat(jobs): Dispatch job board, seeded planets, intro only once
- feat(ships): engine exhaust, every jet fires for its share of the move
- feat(combat): monsters, d20 fights, shooting on foot, rescue
- feat(ship): barrel roll on Shift+Q / Shift+E
- feat(station): Flight School, a training deck to test-fly the ships
- feat(encounters): dice-only standoffs and finds
- fix(station): cryo pod flicker, the real fix (lost from #45)
- feat(station): the shooting range is a room in the station (dev Range scene removed)
- feat(range): d20 attack roll on every shot at the range
- feat(combat): d20 panel for your planet attack rolls
- feat(combat): aim, stance and recoil move the d20; the margin sets the damage
- fix(items): the equipped gun is drawn into your hands

## 0.3.1 (2026-11-10)

- The flight home: on foot beside your landed fighter, E boards it. Lift off and climb to orbit (the HUD
  shows how far), and you're back at the station, stepping off beside your docked ship
- The museum calls as soon as you're back and buys every relic in your pack for its value: credits (shown
  next to Sheet / Pack) and XP per relic. Come back empty-handed and it tells you the cargo's still down there
- Item stacks: med-patches, rations, ping beacons, flares and trauma kits share a cell (x10, x5, x5, x3, x2).
  Using one takes one; bulk and value count each copy
- Drop puts the item at your feet on the planet (E picks it back up), and so does taking gear off with a full
  pack. Not from the cockpit: land first
- Every class starts with a Carbine in its weapon slot (the Ace's and Fixer's sidearm goes in the pack)
- Weapons' finger grips are baked ahead of time, so hands close on a gun or knife cleanly from the first frame
- Fixed: the cryo pods and corridor ribs in the station flickered (overlapping flush panels)
- Fixed: the station's test rifle stayed by the old spawn after the station was rebuilt from your lucky number
- Fixed: the hangar ship is framed the same size it is when you fly it

## 0.3.0 (2026-11-10)

- Your character: Play opens character creation, a d20 sheet rolled from your lucky number. Six abilities
  (4d6, drop the lowest) you can shift points between, five classes and five origins, Stamina over HP,
  trained skills, level and XP. Change it with C until you leave the station. Relic rolls add your WIS
  modifier, and picking things up earns XP
- Inventory (I): a 10x6 pack where items take up their own shape. Drag them into place, R turns them. Your
  class packs a starting kit; relics you find go in if there's room. Carry more than your Strength allows
  and you slow down on the planet
- Equipment: head, body, hands, weapon and tool slots. Worn gear adds to abilities, skills, Stamina, HP,
  relic rolls and how much you can carry
- The title's d20s are real 3D dice in holo neon: Roll throws them, and the faces that land on top are the
  roll
- Weapons: a futuristic arsenal by Niko-3d-models. Nine guns that fire energy bolts, a knife and a hammer
  that swing on click, and gun mods. Both hands hold them, fingers wrapped round each one's real shape. A
  test rifle waits by your spawn on the station
- A shooting range for trying them (?ln-range, or ?ln-range=knife to start at one): every weapon on a bench,
  crates to knock over and targets downrange; V shows your hands from the side
- The planet's ground now runs to the horizon, with hills round the crash site
- A bigger fighter (24 m nose to tail) with a closer chase camera, and sharper ship models up close
- Landing: CLIMB shows your true vertical speed, and the flare now allows for rising ground, so drifting
  over a hillside no longer crashes you on touchdown
- The title and the character panel fit short screens: the dice no longer cover Play

## 0.2.4 (2026-11-10)

- New look: a neon sci-fi theme across the menus, the station and planet screens and the page overlay
  (relics, the d20 and the landing HUD included), with readable hover states
- The title's dice are four d20s: star, worlds, moons (new) and luck. Hover one (or tap it) to see what it
  decides and what it rolled; every die also feeds the lucky number
- Every launch opens on a fresh universe: the title rolls its own lucky number instead of always starting
  on 3250720893
- Bigger, sharper, truer-coloured ships at the crash site (the wrecked freighter and your fighter)
- The first named relic: Zhang Heng's Water Amulet (Eastern Han, 2nd century CE). Find it by the wreck on
  your way in from the landing pad, pick it up (E) and use it (R): it rolls a d20, plus up to 3 from your
  luck die. 1: Flood (slowed); 2-7: Trickle (a little health); 8-14: Spring (a good heal); 15-19: Dragon's
  Pearl (shows you the nearest relic, like his seismoscope); 20: Heavenly Tide (full health, every relic shown)
- The freighter now carries just the named artifact (the MAYDAY names it): no more generated relics. An
  inventory strip on the page, and the d20 rolling on screen when you use it
- New ship controls with flight assist on: the mouse aims the nose, W/S thrust, A/D strafe, Q/E roll,
  Space/C up and down, Shift boost, X brake. Aim and travel are separate (swing the nose and you slide on a
  moment); let go of a key and that motion eases out, so the ship stops on its own and holds its place. Near
  the ground it eases your descent and levels itself, so landing is: stop over the pad, hold C
- Landing HUD rebuilt: step-by-step guidance (fly to the pad, stop over it, descend, touch down) that follows
  what you're doing, an arrow to the pad, cockpit instruments, landing gauges with their safe zones, plain
  warnings ("Still moving: let go of W, or tap X"), a crash card that says why, and a key card
- Fixed: after landing you could fall through the ground over and over, and the ship could sink into it as
  you touched down: touchdown, the parked ship, the chase camera and stepping out all use the ground's
  actual surface now
- Fixed: the title's forming-system dust disk was cut off in a straight line across its far side (the space
  camera now sees all the way to it)
- Amulet model: "Amulet" by Mierenttt (Blend Swap, CC BY 3.0), exported low-poly

## 0.2.3 (2026-11-10)

- Flying in is much easier, with a flight assist (on by default; content can turn it off for the raw model):
  - W/S set a speed the ship holds, and air brakes really slow you when you let off
  - Hold Space to hover down: a controlled descent (quick up high, gentle at the ground, a soft cushion at
    the end) that stops your drift and levels you to the slope, ready to touch down
  - Smoother steering: the mouse turns your heading and pitch at a steady rate (no accidental loops), and
    the ship banks into its turns; the chase camera leans with it and widens with speed
- A green landing pad on the flattest spot by the wreck, with its distance and direction on the HUD
- Landing gauges once you're low: descent, drift and tilt against the touchdown limits, in green, amber
  or red

## 0.2.2 (2026-11-10)

- Your space station: Play now takes you there first (Title, Station, Intro, Planet). You wake in your
  cabin; walking into the hub rings Dispatch, who names the freighter going down and the planet your
  station orbits; then your ship waits docked in the bay. Board it (E) to head out
- The station is laid out from your lucky number: a hub with the observation gallery, docking bay, comms,
  three crew cabins, storage and engineering around it, joined by corridors, so each universe gets its own
- Built from the PSX Starship and Sci-Fi Station kits (CC BY 4.0, heyheythere): cream panels, leaning
  corridor walls, two-storey halls, sliding doors, props, handrails. Windows are real openings onto your
  universe: the planet below, your star and sky
- Station map (M) with every room, your ship's dock and where you are; room names as you walk in
- Lights: on, off or emergency (red, pulsing) from the engineering console, with a soft lamp glow, and an
  optional PS1 look (vertex wobble, affine textures, 15-bit dither) from the next console
- Fly your fighter in and land it near the wreck: mouse pitch and turn, A/D roll, W/S throttle, Shift boost,
  Space lift. A flight HUD and a beacon over the wreck guide you in; land slow and level, or crash and retry
- Page overlays: an incoming-call panel with Answer, the objective, and Station tools in the dock (lights,
  PS1 look and a live layout editor that rebuilds the station as you paint it)
- Smoother textures without losing the PS1 crispness (no more shimmering lines far off), no flicker at wall
  corners, and the whole station in about 35 draw calls
- CI: a failed cached build now really retries cold, and the Library is cached only after a good build

## 0.2.1 (2026-11-10)

- New opening: a museum freighter carrying ancient relics goes down on a planet of your universe, picked by
  your lucky number. Its MAYDAY names the ship, the museum and the cargo (Tibetan, Roman and unknown alien
  amulets and vases), all generated from the lucky number. The title already orbits that planet, so Play
  carries straight into the crash
- The crash site is the freighter's wreck, with solid collision (no getting stuck inside it)
- Map (M): the planet as a smooth hex globe (hexagons and the 12 pentagons Euler requires) in your real
  universe, with your star, moons and sky. It opens from straight above the tile you're on
- Hands: pebbles stay in the palm while you move, no wrist pinch when the palm turns, and the arm ends stay
  out of view when you reach
- Your first words now come as you wake up on the planet
- Credits on the title screen (also in CREDITS.md and the README, all from content/credits.txtpb)
- Page overlays: a Unity-page bridge with React overlays (HUD, Map button, toasts), Tab or Esc frees the
  mouse, a corner hint, and the dice no longer cover the menu on narrow screens
- Fixes: jumping to a scene other than the preloaded one no longer hangs, health starts full, and HTML
  text fields take typing
- CI: the Unity Library cache is keyed by editor version and retried cold if a restored one fails (cached
  builds take about 7.5 min, 13 cold)

## 0.2.0 (2026-11-09)

- Title screen: roll three dice for a lucky number that generates your universe (star, worlds, luck), born
  in a dust-disk big bang, with a seeded nebula sky and distant galaxies
- Solar systems with believable spacing, Keplerian orbits and moons, lit by their star
- Intro cutscene in your own universe: your ship is pulled into the home planet on a spiral and crashes;
  Play carries straight on from the title camera (also when pressed mid big bang)
- Fighter ship with 22 community skins; the skin is the player's choice
- Crash site on the planet you hit: endless ground in its colours, its sun and sky, the smouldering
  wreck, boulders and loose stones
- First person: walk, run, jump; PSX arms with procedural life (breathing, stride swing, run pump,
  arms up in the air, a short tremor after the crash)
- Hands: two-bone IK on a mapped rig, finger poses as data, palms flat on surfaces you face, pick up
  (E), drop (E) and throw (click) stones; arms render on their own layer so they never clip
- UI Toolkit with the Exo 2 font; content-driven configs (proto + txtpb) with generated C#
- Smaller Web build (~11.8 MB): stripping, size-optimised IL2CPP and wasm, lean model imports

## 0.1.0 (2026-11-08)

- First Web build: WebGPU with WebGL 2 fallback, multithreaded Burst jobs (Chrome + Safari)
- Internal render resolution capped at 1280×720
- Project layout under `Assets/_Project`, dev task CLI (`tools/ln.py`), CI/CD to itch.io
