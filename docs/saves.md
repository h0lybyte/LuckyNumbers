# Saves

Players keep their save slots from one release to the next. This page covers what that means when you change
content or the save format.

## Where saves live

There are three slots, `ln.save.0` to `ln.save.2`. Each holds one JSON record, written by `Core/SavePresenter`.
The record goes through `Core/LocalStore`:

- On the web it goes into the browser's localStorage. That storage is per origin, so it outlasts each itch upload's
  new folder.
- A copy also goes into PlayerPrefs, as a fallback.

Settings (`ln.settings.*`) are stored the same way.

The page's `lnRescueSaves()` (WebGLTemplates/LuckyNumbers/index.html) carries over saves from 0.4.1 and older. Those
builds kept saves only in Unity's per-folder IndexedDB.

## How a save is read (Core/SaveFormat)

1. **Parse.** `JsonUtility` reads the record. A field the save lacks reads as its default. A field this build no
   longer has is skipped.
2. **Migrate.** `SaveFormat.Steps` holds one step per version. The steps bring the save up to `SaveFormat.Version`.
3. **Reconcile.** Each content id in the save is mapped to today's content:
   - Ids covered: pack items, ship upgrades, skin, universe, class and origin.
   - A live id, or an id listed in a definition's `old_ids` (`old_skins` for ships), resolves to that definition.
   - An id that no longer exists is dropped:
     - An item pays back what it was worth when saved.
     - Skin and universe fall back to the defaults.
     - Class and origin fall back to the sheet's defaults.
   - An upgrade level above today's maximum is clamped.

Whatever changed is logged on load as `[Save] slot N: ...`.

A slot that doesn't parse shows as empty. Before a new run overwrites it, its text is kept as `ln.save.N.broken`.

## Changing content

- **Adding** items, upgrades, skins, universes, classes or origins needs nothing extra. Run
  `python3 tools/ln.py gen` as usual. The new ids join `content/shipped_ids.json`. Commit that file with the
  generated code.
- **Renaming** an id: keep the old one in the definition's `old_ids`:

  ```
  items {
    id: "carbine_mk2"
    old_ids: "carbine"
    ...
  }
  ```

- **Removing** a definition: put its id in a replacement's `old_ids`. Or, if nothing replaces it, keep the definition
  and stop handing it out. A save that holds the id would otherwise lose it.
- **Never reuse** a shipped id for something else.

`content/shipped_ids.json` lists every id the content has ever had, and nothing ever leaves it. `ln.py gen` fails if
any listed id no longer resolves. So does `gen --check`, which CI runs. The failure names each lost id. The fix is an
`old_ids` entry.

## Changing the save format

- **New field:** add it to `SaveFile` (or `JobSave`, `CharacterSave`, `SavedItem`). Older saves read it as 0, null or
  false, so write the restore code to handle that. No version bump is needed.
- **Field that changes meaning** (units, encoding, split or merged): bump `SaveFormat.Version` and add a step to
  `SaveFormat.Steps` that converts the old form. Keep the old field in the class, so the step can still read it.
- **Test:** add a fixture for the new version to `Tests/EditMode/SaveCompatTests.cs`. Each fixture there is a real
  save from a shipped version and must keep loading.
