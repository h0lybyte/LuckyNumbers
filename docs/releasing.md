# Releasing

Releases are driven by **`CHANGELOG.md`** ([decision 0002](decisions/0002-changelog-driven-releases.md)).
The top `## x.y.z` entry is the game version.

## Ship a new version

1. Add an entry at the top of `CHANGELOG.md`:
   ```
   ## 0.2.0 (2026-12-01)
   - What changed
   ```
2. Merge it to `main`.

CI (`.github/workflows/web.yml`, "Web release") then:

1. reads the version (`python3 tools/ln.py version`) and stops if tag `v0.2.0` already exists
2. builds the Web player with [GameCI](https://game.ci) (`unityci/editor:ubuntu-6000.6.8f1-webgl-3`),
   using the same `WebBuild.Build` entry point as `ln.py build-web`, stamped `0.2.0`
3. uploads it to itch.io (`ITCH_TARGET`, channel `html5`) with `--userversion 0.2.0`
4. creates the git tag + GitHub release `v0.2.0` with that entry's notes

| Trigger | Result |
|---|---|
| `main` push changing `CHANGELOG.md` with a **new** version | build → itch.io → tag + GitHub release |
| `main` push changing only notes (version already tagged) | nothing |
| Manual: Actions → Web release → Run workflow | build current version; deploy if "deploy" ticked |
| Anything else (other pushes, PRs) | no build |

A `## Unreleased` section above the latest version is ignored, so notes can collect there first.
Versions must be `x.y.z` (optionally `-suffix`, e.g. `1.0.0-beta.1`).

## One-time setup

### Secrets

The workflow needs repository secrets `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`,
`BUTLER_API_KEY` and the variable `ITCH_TARGET`. Keep them in a local `.env` (gitignored) and push
them with the CLI:

```
cp .env.example .env              # then fill it in
python3 tools/ln.py secrets --dry-run   # which keys are set (never shows values)
python3 tools/ln.py secrets             # pushes via gh; secrets go over stdin
```

- **Unity license** ([GameCI guide](https://game.ci/docs/github/activation)): Unity Hub → Licenses →
  **Add** → "Get a free personal license", even if Hub already shows "Personal". That writes
  `/Library/Application Support/Unity/Unity_lic.ulf` (Windows: `C:\ProgramData\Unity\Unity_lic.ulf`).
  `.env`'s `UNITY_LICENSE_FILE` points at it. Email + password are also required for Personal.
  A dedicated Unity account for CI limits the impact if credentials ever leak.
- **`BUTLER_API_KEY`** must belong to the itch.io account that owns `ITCH_TARGET`
  (https://itch.io/user/settings/api-keys).
- **`ITCH_TARGET`** is the itch.io `user/game-slug` (currently `kbve/lucky-numbers`). It's independent
  of where the GitHub repo lives.

### itch.io project

1. Create the project, **Kind of project: HTML**.
2. After the first CI upload: mark the `html5` upload "This file will be played in the browser",
   viewport **1280×720**, and tick **SharedArrayBuffer support** (needed for multithreading).

## Manual / local release

```
python3 tools/ln.py build-web
python3 tools/ln.py deploy --target kbve/lucky-numbers --dry-run   # then without --dry-run
```

Requires `butler` on PATH and `butler login` (or `BUTLER_API_KEY` set).

## Troubleshooting

- **Build fails in the first minute:** Unity license activation. Re-do the Hub "Add license" step,
  re-run `ln.py secrets`, re-run the workflow.
- **PRs from forks** get no secrets; that's why there are no PR builds.
- **Release didn't trigger:** the version heading didn't change, or the tag already exists. Check the
  "Read version" job summary.
