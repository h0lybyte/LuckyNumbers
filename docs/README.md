# LuckyNumbers docs

| Doc | Read it when you want to… |
|---|---|
| [Getting started](getting-started.md) | open the project, set up your machine (Mac or Windows) |
| [Project structure](project-structure.md) | know where files go, script and git conventions |
| [Dev tools (`tools/ln.py`)](dev-tools.md) | compile, build, serve or deploy from the command line |
| [Web build](web-build.md) | understand the Web settings: threads, WebGPU, compression, 720p |
| [Releasing](releasing.md) | ship a new version to itch.io, CI setup and secrets |
| [Decisions](decisions/) | know *why* something is the way it is |

## Decisions

| # | Decision |
|---|---|
| [0001](decisions/0001-webgpu-with-webgl2-fallback.md) | Web builds use WebGPU with WebGL 2 fallback |
| [0002](decisions/0002-changelog-driven-releases.md) | Releases are triggered by the version in `CHANGELOG.md` |

New decision: copy the format of an existing one, next number, add it to this table.
