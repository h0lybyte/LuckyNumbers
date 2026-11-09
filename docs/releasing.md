# Releasing

Version = top `## x.y.z` heading in `CHANGELOG.md` ([0002](decisions/0002-changelog-driven-releases.md)).

**Release:** add `## x.y.z (date)` + notes at the top, merge to `main`. Workflow `.github/workflows/web.yml`:
1. Read version; stop if tag `vx.y.z` exists.
2. GameCI WebGL build (`unityci/editor:ubuntu-6000.6.8f1-webgl-3`) via `WebBuild.Build`.
3. `ln.py deploy` → itch.io `ITCH_TARGET:html5`, version `x.y.z`.
4. Tag + GitHub release `vx.y.z` with the entry's notes.

No PR builds. Manual run: Actions → Web release → Run workflow (deploy optional).

## Setup

Secrets `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`, `BUTLER_API_KEY`; variable `ITCH_TARGET` (`kbve/lucky-numbers`):

```
cp .env.example .env   # fill in
python3 tools/ln.py secrets --dry-run
python3 tools/ln.py secrets
```

- License: Unity Hub → Licenses → Add → free personal license (even if one is shown). Writes
  `/Library/Application Support/Unity/Unity_lic.ulf`. See https://game.ci/docs/github/activation.
- `BUTLER_API_KEY` must be from the itch.io account owning `ITCH_TARGET`.
- itch.io project: Kind HTML; after first upload set `html5` playable in browser, 1280×720, SharedArrayBuffer support on.
