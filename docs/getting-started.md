# Getting started

## Requirements

- **Unity 6000.6.8f1** via Unity Hub, with the **Web Build Support** module
- **Python 3** (macOS ships it; Windows: install from python.org, then use `py` instead of `python3`)
- **Git**, and optionally the GitHub CLI `gh` (needed only for [releasing](releasing.md) setup)

## Open the project

1. Clone the repo.
2. In Unity Hub: **Add → Add project from disk** → pick the **`LuckyNumbers/`** folder inside the repo
   (not the repo root; the Unity project lives one level down).
3. Open it with 6000.6.8f1. The first import takes a few minutes.

Check your setup from the repo root:

```
python3 tools/ln.py doctor
```

It prints the Unity path it found, whether the editor is open, and whether a Web build or `butler` exist.
If Unity is installed somewhere unusual, set `UNITY_PATH` to the editor binary.

## Run it

- **In the editor:** open `Assets/_Project/Scenes/SampleScene.unity` and press Play.
- **In the browser:** close the editor, then
  ```
  python3 tools/ln.py build-web
  python3 tools/ln.py serve --open
  ```
  See [Web build](web-build.md) for what the build does and why it needs a special local server.

## One-time git setup: Unity Smart Merge (recommended)

Scenes, prefabs and assets are YAML text. `.gitattributes` routes them through Unity's merge tool,
which resolves most conflicts that a plain text merge can't. Point git at it once per machine:

```
# macOS
git config --global merge.unityyamlmerge.driver '"/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/Tools/UnityYAMLMerge" merge -p %O %B %A %A'

# Windows
git config --global merge.unityyamlmerge.driver '"C:/Program Files/Unity/Hub/Editor/6000.6.8f1/Editor/Data/Tools/UnityYAMLMerge.exe" merge -p %O %B %A %A'
```

Without it, git falls back to a normal text merge; nothing breaks.

## Line endings

Everything is LF on every OS (`.gitattributes` forces it, `.editorconfig` tells editors to write it).
Windows users don't need to change `core.autocrlf`.
