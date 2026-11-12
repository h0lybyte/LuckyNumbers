# Items, relics, weapons and equipment (design, 2026-11-10)

Where item data lives today, the rules we've agreed on, and the steps to get from one to the other. Nothing here is
built yet unless it says so.

## Today

| What | Where | Notes |
|---|---|---|
| Item definitions, the grid pack, starting kits | `inventory.proto`, `content/items.txtpb` | `ItemDef` has kind, slot, shape, bulk, value, colour, use effects |
| Equip / unequip, gear bonuses | `inventory.proto` (landed, #33) | `InventoryEquipRequested`, `InventoryUnequipRequested`; ability, skill, HP, Stamina, relic and bulk bonuses on `ItemDef` |
| Named relics (lore, d20 table, model) | `ItemDef` `lore`, `use`, `model`, `unique`; `content/items/relics/<id>.txtpb` (via `item_refs`) | the Water Amulet, the Roman Vase, the Aztec Block. Landed (step 3) |
| What's aboard the freighter | `cargo.txtpb` `relics` (item ids) | always all of them; generated relics (cultures, kinds) are gone |
| Using a relic | `InventoryUseRequested` / R → `InventoryPresenter` rolls its d20 | `RelicRolled` for the page's d20; `RelicSite` for lying-out count and reveal bearing |
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

- ~~**A relic is split across two definitions.**~~ Fixed (step 3): one `ItemDef` with lore and use.
- **There is no `unique` flag and no stacking.** Kits repeat ids, so every med-patch gets its own uid and cell.
- ~~**Two use paths.**~~ Fixed (step 3): using a relic is an ordinary inventory use that rolls its d20 table.
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
| Roman Vase | `unique`, `lore`, `use`, `model` |
| Aztec Block | `unique`, `lore`, `use`, `model` |
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
2. ~~`feat/niko-weapons` lands.~~ Done (#44).
3. ~~Merge the relic definitions.~~ Done (`feat/relic-items`): `RelicArtifact` became an `ItemDef` with lore and use;
   `content/relics` moved to `content/items/relics`; `relic_items`, `RelicInventory`, `RelicUseRequested`,
   `RelicCulture` and `RelicKind` are gone.
4. ~~Add `unique` and `stack`, and stack the kits.~~ Done (#38). Dropping puts the item in the world (see Decisions).
5. Move the weapons' hold, ranged and melee data into the proto.
6. Add the shared roll table (`dice.proto`).

Each step is its own PR. Do 3 after both branches land so nothing collides.

## Decisions

- **Unequipping (or a swap) with a full pack drops the item at your feet**, and so does Drop: the item goes into the
  world, where E picks it back up (rule 1). It shows as a placeholder block until it has a model. With no world
  around (the title, character creation), Drop removes it and a full-pack unequip is refused.
- **The equipped weapon is in your hands.** A `WEAPON`-slot item with a `model` (a file in Art/Models/Weapons:
  carbine, salvage rifle) is drawn whenever your hands are free, and its hits roll with that item's stats
  (`combat.txtpb` `weapons`). E holsters it and E draws it again. Unequipping or dropping it puts it away.
  The sidearm has no model yet: it is drawn as named placeholder blocks (`model: "PLACEHOLDER_sidearm"`).
  A weapon with no `model` at all fires from the hip, unseen.

## Open questions

- **Selling a relic to the museum:** is it gone for that universe?
- **Wearable relics:** add a `NECK` / `ACCESSORY` slot so the amulet can be worn for a luck bonus?
- **Gear and the d20:** `relic_bonus` exists. Should more gear feed the luck die?
