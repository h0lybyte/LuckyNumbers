# 0002: CHANGELOG-driven releases (2026-11-08)

**Context:** builds are slow and every itch.io upload is public; releases should be deliberate.

**Decision:** a new top `## x.y.z` in `CHANGELOG.md` on `main` triggers build → itch.io → tag/release.
Existing tag = no-op. No PR builds. Version stamped at build time, not stored in ProjectSettings.

**Trade-off:** `main` isn't built between releases; run `ln.py check` before merging.
