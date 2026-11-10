# moon

[moon](https://moonrepo.dev) is the task runner on top of `tools/ln.py`: one entry point, dependency order,
and cached checks (a task whose inputs didn't change is skipped). `ln.py` still does the work and still runs
on its own.

```
proto install                # moon pinned in .prototools (and Node 24 via .moon/toolchains.yml)
moon run game:check          # content:check first, then headless compile (editor closed)
moon run game:build-web      # content:check + web:typecheck, then Web build (editor closed)
moon run game:serve          # serve Builds/WebGL with COOP/COEP
moon run web:dev             # rebuild the overlay on save
moon run content:gen         # regenerate C# / TS / credits from proto/ + content/
moon check --all             # every cacheable check
moon tasks                   # list everything
```

| Project | Folder | Tasks |
|---|---|---|
| `content` | `content/` (+ `proto/`) | `validate` (protoc), `gen`, `check` (`gen --check`) |
| `web` | `web/` | `typecheck`, `build`, `dev` |
| `game` | `LuckyNumbers/` | `check`, `build-web`, `scenes`, `roslyn`, `serve`, `deploy` |

- Config: `.moon/workspace.yml`, `.moon/toolchains.yml`, `<project>/moon.yml`. Cache in `.moon/cache` (gitignored).
- Unity tasks share mutex `unity`, so moon never runs two at once (project lock).
- Not cached: anything that writes committed files (`gen`, `roslyn`, `scenes`) or the build itself
  (`build-web`, `web:build`); Unity's `Library/` is the real build cache.
- Node and npm come from moon (Node 24, same as CI); `npm ci` runs automatically when `web/package-lock.json`
  changes. Python and Unity come from the system. Windows: `python3` must be on PATH (or use `py tools/ln.py`).
- CI: `.github/workflows/ci.yml` runs `moon ci` on PRs and main (affected `content:validate`, `content:check`,
  `web:typecheck`). Unity tasks are `runInCI: false`; the release build (`web.yml`) still calls `ln.py` and game-ci.
