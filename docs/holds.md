# Dwarven holds

Structures on and under dwarven worlds: a cell grid, meshed into chunks, skinned from one texture array.

- Schema: `proto/luckynumbers/v1/hold.proto`. Config: `content/hold.txtpb` (`GameContent.Hold`); textures and
  palettes `content/holds/textures/delven.txtpb`; mine rules `content/holds/mines/deep.txtpb`.
- Code: `Scripts/Hold/` (assembly `LuckyNumbers.Hold`). Core types in `HoldGrid.cs`.

## Grid

- A cell is `cell_size` (2 m) square and `storey_height` (3 m) tall. `HoldGrid.Size` = (x east, levels up, z north).
- `HoldGrid.Origin` is the world cell of local (0,0,0). World position of a cell's floor corner:
  `(Origin + c) * (cell_size, storey_height, cell_size)`; its centre adds half a cell on x and z.
- Reads outside a grid are Solid, so a grid's border always gets walls.
- `HoldCell`: `Kind` (Solid, Floor, Corridor, Door, Gate, ShaftDown/ShaftUp, Mine), `Palette` (index into
  `HoldTextures.palettes`), `Flags` (Lantern, Pillar, OpenAbove).

## Mesh

- One mesh per `chunk_cells` x `chunk_cells` column of cells (all levels), one material for everything.
- Faces: floor, ceiling (unless OpenAbove), and a wall wherever a walkable cell meets Solid; door frames in trim;
  pillars as boxes. Texture slice per face from the cell's palette (`HoldPalettes.Resolve`).
- Vertex: position, normal, uv (u, v, slice) and colour (baked lantern light + ambient).
- Code: `HoldMesher.Build(grid, palettes, config)` (or `Schedule(...)` then `Complete()`): one Burst job per chunk,
  then Mesh.MeshData. Face rules (`HoldRules`: Linked, HasWall, HasFloor, HasCeiling) are shared with anything that
  must agree with the mesh. Vertex stream (40 bytes): Position float3, Normal float3, Color UNorm8x4 (light rgb, a 1),
  TexCoord0 float3 (u, v, slice); UVs world-aligned, 1 tile per cell_size metres. 32-bit indices.
- No floor/ceiling between two cells that are one space: lower has OpenAbove, upper is ShaftDown, or lower is ShaftUp.
- Colliders (world-space boxes per chunk): whole Solid cells that carry a wall (merged runs per level), floor slabs
  (1 m thick over rock, 0.1 m over a room), pillar shafts. Self-test: `LuckyNumbers.Editor.HoldMesherCheck.Run`.

## Textures and shader

- `Editor/HoldTextureBuilder.cs` (menu LuckyNumbers/Holds/Build Texture Array, `tools/ln.py hold-textures`) builds
  `Art/Textures/HoldLayers.asset` from `HoldTextures`: slice i = `layers[i]`, sources nearest-scaled to `size`,
  box-filtered mips, DXT1 (DXT5 if a layer has alpha), repeat, trilinear. It also wires `Art/Materials/HoldKit.mat`
  and compiles the shader for GLES3x + WebGPU. Rerun after changing the layers; the asset keeps its GUID.
- `LuckyNumbers/HoldKit`: uv0 = (u, v, slice), sharp bilinear (as StationKit) x vertex colour, up to 4 URP additional
  lights, haze to `_HazeColor` (full at `_HazeDistance`), `_ALPHATEST_ON` + `_Cutoff` for cutouts, `_LnPsx` PS1 look.
- `--preview` renders `Logs/preview-holdkit.png` (Editor/HoldKitPreview.cs).

## Mines (the endless deep)

- Regions of `region_cells` x `region_levels` x `region_cells`; region (rx, ry, rz) has
  `Origin = (rx * region_cells, ry * region_levels, rz * region_cells)`, ry < 0 going down.
- Each region is generated from `hash(seed, rx, ry, rz)` only. Openings on a shared edge come from a hash of that
  edge (ordered so both neighbours compute the same one), so regions always line up. Shafts between a region and
  the one below come from a hash of that pair.
- `MineGenerator.Region(seed, region, config, allocator)` digs one region (`Scripts/Hold/MineGenerator.cs`). Edge
  openings sit 3+ cells from corners; east-west edges on a region's lowest level always have one, so nothing is
  sealed. `MineGenerator.ShaftBelow(seed, (x, 1, z), ...)` says where region y 0's shaft up would meet the hold.
- Check: `-executeMethod LuckyNumbers.Editor.MineCheck.Run` (seams, shafts, connectivity, determinism, timing);
  ASCII maps in `LuckyNumbers/Logs/mines-ascii.txt`.

## Shafts and ladders

- A shaft is a ShaftDown cell over a ShaftUp cell. A ShaftDown on a grid's bottom level (or ShaftUp on its top) opens
  into the next region down (up), so `HoldRules.HasFloor` / `HasCeiling` leave it open.
- Every ShaftUp cell gets a timber ladder (rails: palette pillar slice, rungs: trim; rungs every 0.35 m) from its floor
  to `HoldRules.LadderAbove` (1 m) over the upper floor, `LadderDepth` (0.12 m) deep against the wall from
  `HoldRules.LadderSide(grid, shaftUp)`: Solid behind on the lower storey, then the upper, then a floor opposite on the
  upper level; ties from a hash of the world cell. The whole ladder belongs to the ShaftUp cell's grid (even the part
  in the region above), so the mesher and the climbing code ask the same grid. No collider.
- Climbing (`Scripts/HoldScene/HoldClimbPresenter.cs`): in the shaft within 0.8 m of the ladder, facing it, W grabs it
  (falling in, you catch it and W waits for a fresh press). `FirstPersonPresenter.Climbing` turns off gravity and
  walking; W/S at `climb_speed`; at the top you walk off onto the floor opposite the ladder (else beside it); S at the
  bottom puts you down; Space lets go (no grabbing again until you land).

## Life (caches and creatures)

- Schema: `proto/luckynumbers/v1/hold_life.proto` (`HoldConfig.life`); rules `content/holds/life/default.txtpb`.
  Finds are items in `content/items/dwarven/` (Kind RELIC, so the museum buys them at the station).
- `HoldLifePlan.Plan(seed, region, grid, life, ...)` (Scripts/HoldScene): from hash(seed, region) and the grid only.
  Chambers = groups of mine cells whose 8 neighbours are open; dead ends = mine cells with one way out. Nothing on or
  beside a shaft or within 2 cells of the region edge. Depth = -region.y; tier and creature weights grow with depth.
- `RelicCaches`: a strongbox (PLACEHOLDER_dwarven_strongbox + lid + glint, one shared point light on the nearest);
  E puts its find in the pack (`IInventory.TryAdd`). Opened caches stay open for the visit (by world cell).
- `DeepCreatures`: combat.txtpb monsters, wandering / chasing on the cell graph of their own region (`CellPaths`, BFS),
  noticing you by sight + a ray; the crash site's fight pieces (MonsterView/Brain, Marksman, Dice, Bolts, ShotCheck)
  and its rescue when you're down. Despawn on RegionHidden.
- Check: `-executeMethod LuckyNumbers.Editor.HoldLifeCheck.Run` (placement rules, determinism, counts by depth).

  sealed. Above region y 0 is the hold's top, so there the shaft rule is fixed: only region (0,0,0) has a shaft up,
  always, at `MineGenerator.Entrance(seed, ...)` (hashed, 2..cells-3); the hall's stair drops onto it.
- Check: `-executeMethod LuckyNumbers.Editor.MineCheck.Run` (seams, shafts, entrance, connectivity, determinism,
  timing); ASCII maps in `LuckyNumbers/Logs/mines-ascii.txt`.

## Hall (the hold's top)

- Rules: `HallRules` (`proto/luckynumbers/v1/hold_hall.proto`), content `content/holds/hall/default.txtpb`.
- `HallGenerator.Generate(seed, config, ...)` (`Scripts/Hold/HallGenerator.cs`) lays out region (0, 1, 0); every other
  region at y >= 1 is solid rock (`HallGenerator.Rock`). Plan: a Gate on the region edge farthest from the entrance
  column, a two-storey passage, the great hall (aisles, two pillar rows, a tall nave; lanterns low in the aisles and
  high in the nave), then the stair room round `MineGenerator.Entrance` and the other rooms one wall apart, each
  joined by a door through a single wall or the shortest corridor. Validated (`HallGenerator.Validate`: margin, gate
  edge, doors between two walls, one shaft at the entrance, level 1 only air over open-above cells, all reachable
  from the gate) and re-rolled until it passes. `HallPlan` keeps the gate cells, outward direction, spawn and shaft.
- Scene: `HallSource` (IRegionSource, top regions), `HallSpawn` (always just inside the gate, facing in),
  `HallGatePresenter` (prompt near the gate; into its outer edge: fade, `HoldTrip.Leaving`, Planet scene).
- Check: `-executeMethod LuckyNumbers.Editor.HallCheck.Run` (200 seeds; ASCII in `Logs/hall-ascii.txt`). Renders:
  `-executeMethod LuckyNumbers.Editor.HallPreview.Render` (graphics) -> `Logs/preview-hall.png`, `preview-hall-top.png`.
