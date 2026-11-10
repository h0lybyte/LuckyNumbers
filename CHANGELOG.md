# Changelog

Releases are driven by this file: the **top `## x.y.z` entry is the game version**.
Bumping it on `main` builds the Web player, uploads it to itch.io with that version,
and tags/releases `vx.y.z` on GitHub. Editing notes without changing the version does not rebuild.

## 0.2.4 (2026-11-10)

- The first named relic: Zhang Heng's Water Amulet (Eastern Han, 2nd century CE). Find it by the wreck on
  your way in from the landing pad, pick it up (E) and use it (R): it rolls a d20, plus up to 3 from your
  luck die. 1: Flood (slowed); 2-7: Trickle (a little health); 8-14: Spring (a good heal); 15-19: Dragon's
  Pearl (shows you the nearest relic, like his seismoscope); 20: Heavenly Tide (full health, every relic shown)
- The freighter's relics now lie round the wreck to collect, glowing, with a new Han Chinese culture among
  them; an inventory strip on the page, and the d20 rolling on screen when you use one
- New ship controls with flight assist on: the mouse aims the nose, W/S thrust, A/D strafe, Q/E roll,
  Space/C up and down, Shift boost, X brake. Aim and travel are separate (swing the nose and you slide on a
  moment); let go of a key and that motion eases out, so the ship stops on its own and holds its place. Near
  the ground it eases your descent and levels itself, so landing is: stop over the pad, hold C
- Landing HUD rebuilt: step-by-step guidance (fly to the pad, stop over it, descend, touch down) that follows
  what you're doing, an arrow to the pad, cockpit instruments, landing gauges with their safe zones, plain
  warnings ("Still moving: let go of W, or tap X"), a crash card that says why, and a key card
- Fixed: after landing you could fall through the ground over and over (you're now placed on the ground's
  actual surface)
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
