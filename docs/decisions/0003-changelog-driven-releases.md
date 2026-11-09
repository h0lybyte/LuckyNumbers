# 0003: Releases are triggered by the version in `CHANGELOG.md`

- **Status:** accepted
- **Date:** 2026-11-08

## Context

Unity Web builds take minutes and each itch.io upload is a public release. Building on every push or
PR wastes CI time and makes "what's live" unclear. We wanted a deliberate, reviewable release step.

## Decision

- The top `## x.y.z` heading of `CHANGELOG.md` **is** the game version.
- A push to `main` that changes `CHANGELOG.md` runs the release workflow. If tag `vx.y.z` doesn't
  exist yet, it builds, uploads to itch.io as `x.y.z`, and creates tag + GitHub release `vx.y.z`
  with the entry's notes. If the tag exists (a notes-only edit), it does nothing.
- No automatic PR or branch builds. A manual "Run workflow" exists for one-off builds.
- The version is stamped into the build (`Application.version`) via `-lnVersion`, not stored in
  `ProjectSettings`, so releases don't churn project files.

## Consequences

- Releasing = editing one file in a PR; the release notes are reviewed with the version bump.
- Git tags, GitHub releases, itch.io versions and `Application.version` always match.
- Code can break on `main` without CI noticing until the next release. Run `ln.py check` /
  `ln.py build-web` locally before merging. Revisit PR builds if that bites.
- Forgetting to bump the version means no release, which is safe by default.
