# Proto schema + content

Game data and messages are defined in `proto/`; authored values live in `content/` (protobuf text format).
Code generation from these is the next step and doesn't exist yet.

```
proto/ln/options.proto           custom options: (ln.event) (ln.state) (ln.config)
proto/luckynumbers/v1/*.proto    common, flow, title, cutscene, input, player
content/*.txtpb                  title, intro cutscene, player config
```

| Option | Meaning for the generator |
|---|---|
| `(ln.event)` | MessagePipe message: struct + `RegisterMessageBroker<T>` |
| `(ln.state)` | runtime state, exposed reactively (R3) |
| `(ln.config)` | authored data loaded from a `content/*.txtpb` |

## Rules

- proto3, `package luckynumbers.v1`, `csharp_namespace = "LuckyNumbers.Model"`; enum values prefixed with the enum name.
- Data only, no behaviour. Never renumber or reuse field numbers; add new fields instead.
- Each `.txtpb` starts with `# proto-file:` and `# proto-message:` headers naming its schema.

## Validate

```
python3 tools/ln.py proto
```

Compiles all schemas and encodes every `content/*.txtpb` against its message. Uses a pinned protoc
(36.2, SHA-256 verified) downloaded to `.cache/` on first run.
