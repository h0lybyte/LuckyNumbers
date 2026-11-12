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

## Mines (the endless deep)

- Regions of `region_cells` x `region_levels` x `region_cells`; region (rx, ry, rz) has
  `Origin = (rx * region_cells, ry * region_levels, rz * region_cells)`, ry < 0 going down.
- Each region is generated from `hash(seed, rx, ry, rz)` only. Openings on a shared edge come from a hash of that
  edge (ordered so both neighbours compute the same one), so regions always line up. Shafts between a region and
  the one below come from a hash of that pair.
