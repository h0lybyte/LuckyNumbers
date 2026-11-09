# Getting started

Requires Unity **6000.6.8f1** (Web Build Support module) and Python 3.

1. Unity Hub → Add project from disk → the **`LuckyNumbers/`** subfolder (not repo root).
2. `python3 tools/ln.py doctor`: shows detected Unity path, editor state, build, butler.
3. Play in editor: `Assets/_Project/Scenes/SampleScene.unity`.

## Smart Merge (optional)

`.gitattributes` routes Unity YAML (scenes, prefabs, assets) to `unityyamlmerge`. Configure once per machine:

```
git config --global merge.unityyamlmerge.driver '"/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/Helpers/UnityYAMLMerge" merge -p %O %B %A %A'
```

Windows: point at `UnityYAMLMerge.exe` inside your Unity install. Unconfigured, git uses a normal text merge.

Line endings: LF everywhere, enforced by `.gitattributes` and `.editorconfig`.
