# Proto schema, content, code generation

Game data and messages are defined once in `proto/`; authored values live in `content/` (protobuf text
format); C# is generated into `LuckyNumbers/Assets/_Project/Generated/` (committed, never hand-edited).

```
proto/ln/options.proto           message options (below)
proto/luckynumbers/v1/*.proto    common, flow, title, cutscene, input, player
content/*.txtpb                  title, intro cutscene, player config
```

## Generated C#

| Option | C# | Plus |
|---|---|---|
| `(ln.event)` | `readonly struct` | `builder.RegisterModelEvents(options)` registers a MessagePipe broker for each |
| `(ln.state)` | class of R3 `ReactiveProperty<T>` / `ObservableList<T>` | `builder.RegisterModelState()` registers each as a singleton |
| `(ln.config)` | data class | each `content/<name>.txtpb` is baked into `GameContent.<Name>` |
| `(ln.value)` | `readonly struct` with value equality | |
| `(ln.material) = "Shader/Name"` | data class | `look.ApplyTo(material)`: fields → `_PascalCase` shader properties. gen fails if a property is missing, if content leaves a field unset, or if a scalar field isn't `optional` (needed so an explicit 0 counts as set) |
| none | data class; oneof → `<Name>Case` enum + property | |

Assemblies: `LuckyNumbers.Model` (types + content, no engine refs) and `LuckyNumbers.Model.Di`
(VContainer/MessagePipe registration). Names: `duration_seconds` → `DurationSeconds`,
`GAME_SCENE_TITLE` → `GameScene.Title`. Proto comments become XML docs.

## Content layout

```
content/title.txtpb, intro.txtpb, planet.txtpb, player.txtpb   game configs (top level, must be (ln.config)) -> GameContent.*
content/cargo.txtpb                              the museum freighter's seeded manifest: ships, museums, cultures, relic kinds
content/character.txtpb                          the player's character sheet: abilities, dice rule, point moving, classes, origins, skills, levels
content/credits.txtpb                            credits: the title's Credits panel; `gen` also writes CREDITS.md and README.md's block
content/space/systems/home.txtpb                 library: a whole solar system (camera, bodies, orbits)
content/space/looks/planet_base.txtpb            library: defaults every planet look extends
content/space/looks/planets/*.txtpb              library: planet presets
content/space/looks/stars/*.txtpb                library: star presets
content/space/skies/*.txtpb                      library: skybox looks (nebula, galactic band)
content/space/looks/galaxies/*.txtpb             library: distant galaxy presets
content/space/looks/disks/*.txtpb                library: dust disk presets (big bang)
content/space/generators/default.txtpb           library: seed -> solar system rules and palettes
content/hands/rig.txtpb                          library: first-person arms bone map (IK chains, fingers, poses)
content/hands/poses/*.txtpb                      library: finger poses (curl per joint, spread, fidget)
```

Text format has no includes; the generator adds two, using protobuf merge semantics
(set fields override, nested messages merge, lists append):

- **Templates:** `# proto-extends: space/looks/planet_base`: the base is applied first, this file overrides.
- **References:** a string field marked `[(ln.ref) = "planet"]` names a library file merged into that sibling field,
  before its inline values (so inline fields override the preset). A repeated ref field appends each file.
  Example: `planet_ref: "space/looks/planets/ice"` plus `planet { axial_tilt_degrees: 40 }`.
- Paths are relative to `content/`, without `.txtpb`. gen fails on a missing file, a type mismatch, or an extends cycle.
- Library files (subfolders) are only baked when referenced; ref fields don't exist at runtime.

## Commands

```
python3 tools/ln.py proto         # validate schemas + content
python3 tools/ln.py gen           # regenerate C# (~0.1 s, deterministic)
python3 tools/ln.py gen --check   # fail if committed C# is stale (CI runs this)
```

Both use a pinned protoc (36.2, SHA-256 verified) downloaded to `.cache/`. Generator: `tools/protogen.py`,
Python stdlib only.

## Rules

- proto3, `package luckynumbers.v1`, `csharp_namespace = "LuckyNumbers.Model"`; enum values prefixed with the enum name.
- Data only, no behaviour. Never renumber or reuse field numbers; add new fields instead.
- Events and values are flat (no repeated/oneof). Oneof members are messages. No nested type declarations.
- Each `.txtpb` starts with `# proto-file:` / `# proto-message:` headers; its message must be `(ln.config)`.
- After changing `proto/` or `content/`: run `ln.py gen` and commit the result.

## Roslyn generator (C# side)

`tools/roslyn/LuckyNumbers.Generators` (Microsoft.CodeAnalysis 4.3.1; Unity 6000.6 compiles with Roslyn 4.10)
runs inside Unity's compiler for assemblies referencing `LuckyNumbers.Attributes`:

- `[Presenter]` class → `RegisterEntryPoint<T>()`; `[View]` MonoBehaviour → `RegisterComponentInHierarchy<T>()`
- `[Ui("name")]` field in a `partial` class → generated `BindUi(root)` doing `root.Q<T>("name")`, throwing if the UXML lacks it
- emitted as `builder.Register<AssemblyName>()` (e.g. `RegisterLuckyNumbersTitle()`); diagnostics LN0001–LN0004
- rebuild after changing the generator: `python3 tools/ln.py roslyn`
