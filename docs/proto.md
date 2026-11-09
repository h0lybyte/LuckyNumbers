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
