# Items, relics, weapons and equipment (design, 2026-11-10)

Where item data lives today, the rules we've agreed on, and the steps to get from one to the other. Nothing here is
built yet unless it says so.

## Today

| What | Where | Notes |
|---|---|---|
| Item definitions, the grid pack, starting kits | `inventory.proto`, `content/items.txtpb` | `ItemDef` has kind, slot, shape, bulk, value, colour, use effects |
| Equip / unequip, gear bonuses | `inventory.proto` (landed, #33) | `InventoryEquipRequested`, `InventoryUnequipRequested`; ability, skill, HP, Stamina, relic and bulk bonuses on `ItemDef` |
| Named relics (lore, d20 table, model) | `cargo.proto` `RelicArtifact`, `content/relics/*.txtpb` | the Water Amulet |
| What a relic takes up in the pack | `items.txtpb` `relic_items` → `relic_amulet`, ... | the shape and bulk are matched from the relic's kind |
| Generated relics | `cargo.proto` `RelicCulture`, `RelicKind`, `min/max_relics` | turned off (`max_relics: 0`) |
| Old relic strip | `RelicInventory`, `RelicUseRequested` | the pack's use path duplicates them |
| How guns and melee weapons are held and fired | `WeaponView` / `MeleeView` serialized fields (`feat/niko-weapons`) | stored on the prefab, not in content |

## Rules

1. **An item is in exactly one place:** the world, the pack, or an equipment slot. Equipping takes it out of the
   pack, and taking it off puts it back. Equipping into a filled slot swaps the two items.
2. **Relics are unique:** one of each per universe. A relic is never duplicated and never stacks.
3. **Stacking:** different items never stack. Copies of the same item stack only if it isn't unique and keeps no
   state of its own (`stack > 1`: med-patches, rations, cells). Guns don't stack, because each copy has its own
   ammo and charge.
4. **Gear bonuses count only while equipped.** The character sheet adds up what's in the slots and ignores the
   pack.

## Gaps

- **A relic is split across two definitions.** The `RelicArtifact` in cargo holds lore, the d20 table and the
  model. A separate pack item, matched through `relic_items`, holds the shape and bulk. Fix: a named artifact
  becomes one `ItemDef` with a relic part. Then `relic_items` and `default_relic_item` go.
- **There is no `unique` flag and no stacking.** Kits repeat ids, so every med-patch gets its own uid and cell.
- **Two use paths.** `RelicInventory` and `RelicUseRequested` do the same job as `PlayerInventory` and
  `InventoryUseRequested`. Fix: using a relic becomes an ordinary inventory use that rolls its d20 table.
- **Effects are named after relics.** `RelicEffect` is already used by consumables. Rename it `ItemEffect` and
  move it, together with `RelicRoll`, into a shared roll table (`dice.proto`) that items and the sheet can both
  use.
- **Weapon data isn't content.** Grip, foregrip, muzzle, hold offset and scale, bolts, cooldown and recoil sit on
  `WeaponView`, and grip, tip, edge and swing on `MeleeView`. Fix: item parts in the proto. The scene builder's
  measured points become defaults, and the proto can override them.
- **`PlayerInventory` packs each item into a pipe string.** Move to a repeated message if the `(ln.web)` mirror
  gets support for nested messages.

## Proposed shape

`ItemDef` keeps fields 1-19 and gains optional parts. An item has only the parts it needs:

```proto
message ItemDef {
  // 1-19 as today: id, name, description, kind, slot, shape, bulk, value, color, effects..., gear bonuses.
  bool unique = 20;          // one per universe (relics)
  int32 stack = 21;          // > 1: this many share a cell; 0/1: one per cell
  string model = 22;         // Art/Models/...; empty: a placeholder block
  ItemLore lore = 23;        // figure, era, text (relics)
  ItemUse use = 24;          // cooldown + d20 RollTable (relics, some tools)
  ItemHold hold = 25;        // grip / foregrip points, offset, held scale, palm directions
  ItemRanged ranged = 26;    // muzzle, bolt, speed, range, cooldown, impulse, recoil
  ItemMelee melee = 27;      // tip, edge, swing seconds, radius, impulse
}
```

| Item | Parts |
|---|---|
| Water Amulet | `unique`, `lore`, `use`, `model` (+ an equip slot if amulets become wearable) |
| Salvage rifle | `slot: WEAPON`, gear bonuses, `hold`, `ranged`, `model` |
| Plasma cutter | `slot: TOOL`, `hold`, `melee` |
| Med-patch | `consumable`, `effects`, `stack: 10` |
| Pressure helmet | `slot: HEAD`, gear bonuses |

`content/items.txtpb` can stay one file for common items. Named relics keep a file each
(`content/items/relics/<id>.txtpb`, pulled in with `(ln.ref)`), because their lore and tables are long.
`cargo.proto` shrinks to the ship, the museum, the manifest (`artifact_refs` → item ids), the luck bonus and the
scatter at the crash site.

## Order

1. ~~`feat/equipment` lands.~~ Done (#33).
2. `feat/niko-weapons` lands.
3. Merge the relic definitions: `RelicArtifact` becomes an `ItemDef` with lore and use. Move `content/relics`,
   delete `relic_items`, `RelicInventory`, `RelicUseRequested`, `RelicCulture` and `RelicKind`.
4. Add `unique` and `stack`, and stack the kits. Dropping puts the item in the world (see Decisions). In progress
   (`feat/item-stacks`); it touches only the pack, so it doesn't wait for 2 and 3.
5. Move the weapons' hold, ranged and melee data into the proto.
6. Add the shared roll table (`dice.proto`).

Each step is its own PR. Do 3 after both branches land so nothing collides.

## Decisions

- **Unequipping (or a swap) with a full pack drops the item at your feet**, and so does Drop: the item goes into the
  world, where E picks it back up (rule 1). It shows as a placeholder block until it has a model. With no world
  around (the title, character creation), Drop removes it and a full-pack unequip is refused.

## Open questions

- **Selling a relic to the museum:** is it gone for that universe?
- **Wearable relics:** add a `NECK` / `ACCESSORY` slot so the amulet can be worn for a luck bonus?
- **Hands:** is the held item just whatever is equipped in `WEAPON` / `TOOL`, plus a drawn / holstered state?
- **Gear and the d20:** `relic_bonus` exists. Should more gear feed the luck die?
