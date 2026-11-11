"""proto/ + content/ -> C# in LuckyNumbers/Assets/_Project/Generated (stdlib only).

protoc (pinned, see ln.py) compiles the schemas into a FileDescriptorSet and encodes the
content files; this module reads both straight from the protobuf wire format and emits C#.

Message options (proto/ln/options.proto) decide the C# shape:
  (ln.event)  readonly struct + MessagePipe broker registration
  (ln.state)  sealed class of R3 ReactiveProperty / ObservableList fields
  (ln.config) sealed data class; content/*.txtpb of this type is baked into GameContent
  (ln.value)  readonly struct with value equality
  (ln.web)    on a state/event: mirrored to the web page (Di/WebMirror.g.cs + web/src/generated/ln.g.ts)
  (ln.web_in) on an event: the page may publish it too
  (none)      sealed data class (oneofs get a <Name>Case enum + property)

Enum options:
  (ln.flags)  a bit set: [System.Flags] (values 0 or single bits); the page gets the set members' names
"""
from __future__ import annotations

import json
import re
import struct
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

# ---------------------------------------------------------------------------- wire format

def _varint(b: bytes, i: int) -> tuple[int, int]:
    shift = result = 0
    while True:
        byte = b[i]
        i += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, i
        shift += 7


def _fields(b: bytes):
    """Yield (field_number, wire_type, value): int for varint, bytes otherwise."""
    i = 0
    while i < len(b):
        key, i = _varint(b, i)
        num, wt = key >> 3, key & 7
        if wt == 0:
            v, i = _varint(b, i)
        elif wt == 1:
            v, i = b[i:i + 8], i + 8
        elif wt == 2:
            n, i = _varint(b, i)
            v, i = b[i:i + n], i + n
        elif wt == 5:
            v, i = b[i:i + 4], i + 4
        else:
            raise ValueError(f"unsupported wire type {wt}")
        yield num, wt, v


def _packed_varints(b: bytes):
    i = 0
    while i < len(b):
        v, i = _varint(b, i)
        yield v

# ---------------------------------------------------------------------------- descriptors

# FieldDescriptorProto.Type -> C# type for the scalar types we support.
SCALARS = {1: "double", 2: "float", 3: "long", 5: "int", 8: "bool", 9: "string", 13: "uint"}
T_MESSAGE, T_ENUM, LABEL_REPEATED = 11, 14, 3
OPT_EVENT, OPT_STATE, OPT_CONFIG, OPT_VALUE, OPT_MATERIAL, OPT_WEB, OPT_WEB_IN = 50001, 50002, 50003, 50004, 50005, 50006, 50007
FIELD_OPT_REF = 50101
ENUM_OPT_FLAGS = 50201


@dataclass
class Field:
    name: str
    number: int
    type: int
    type_name: str  # ".pkg.Msg" for message/enum
    repeated: bool
    oneof: int | None
    doc: str = ""
    optional: bool = False  # proto3 `optional` (explicit presence)
    ref_target: str = ""    # (ln.ref): content path(s) merged into this sibling field


@dataclass
class Message:
    full: str  # ".luckynumbers.v1.Vec2"
    name: str
    fields: list[Field]
    oneofs: list[str]
    options: set[int]
    file: str
    doc: str = ""
    material: str = ""  # (ln.material) shader name


@dataclass
class Enum:
    full: str
    name: str
    values: list[tuple[str, int, str]]  # (proto name, number, doc)
    file: str
    doc: str = ""
    flags: bool = False  # (ln.flags): a bit set


@dataclass
class Model:
    messages: dict[str, Message] = field(default_factory=dict)
    enums: dict[str, Enum] = field(default_factory=dict)
    order: list[tuple[str, str]] = field(default_factory=list)  # (file, full name) in declaration order


def _str(b: bytes) -> str:
    return b.decode("utf-8")


def _comments(fd: bytes) -> dict[tuple, str]:
    """SourceCodeInfo path -> leading comment."""
    out = {}
    for num, _, v in _fields(fd):
        if num != 9:
            continue
        for lnum, _, loc in _fields(v):
            if lnum != 1:
                continue
            path, lead = (), ""
            for n, wt, x in _fields(loc):
                if n == 1:
                    path = tuple(_packed_varints(x)) if wt == 2 else path + (x,)
                elif n == 3:
                    lead = _str(x).strip()
            if lead:
                out[path] = lead
    return out


def load_descriptors(data: bytes, skip_prefixes=("google/", "ln/")) -> Model:
    model = Model()
    for num, _, fd in _fields(data):
        if num != 1:
            continue
        fname = pkg = ""
        msgs, enums = [], []
        for n, _, v in _fields(fd):
            if n == 1:
                fname = _str(v)
            elif n == 2:
                pkg = _str(v)
            elif n == 4:
                msgs.append(v)
            elif n == 5:
                enums.append(v)
        if fname.startswith(skip_prefixes):
            continue
        docs = _comments(fd)
        for ei, e in enumerate(enums):
            _load_enum(model, e, pkg, fname, docs, (5, ei))
        for mi, m in enumerate(msgs):
            _load_message(model, m, f".{pkg}", fname, docs, (4, mi))
    return model


def _load_enum(model, b, scope, fname, docs, path):
    name, values, flags = "", [], False
    for n, _, v in _fields(b):
        if n == 1:
            name = _str(v)
        elif n == 3:  # EnumOptions
            flags = any(on == ENUM_OPT_FLAGS and ov for on, _, ov in _fields(v))
        elif n == 2:
            vname = vnum = None
            for vn, _, vv in _fields(v):
                if vn == 1:
                    vname = _str(vv)
                elif vn == 2:
                    vnum = vv if vv < 2**31 else vv - 2**64
            values.append((vname, vnum, docs.get(path + (2, len(values)), "")))
    full = f".{scope}.{name}" if not scope.startswith(".") else f"{scope}.{name}"
    if flags:
        bad = [vname for vname, vnum, _ in values if vnum < 0 or vnum & (vnum - 1)]
        if bad or not any(vnum == 0 for _, vnum, _ in values):
            raise ValueError(f"{full}: (ln.flags) values must be 0 or single bits (1, 2, 4, ...); bad: {bad}")
    model.enums[full] = Enum(full, name, values, fname, docs.get(path, ""), flags)
    model.order.append((fname, full))


def _load_message(model, b, scope, fname, docs, path):
    name, fields, oneofs, options, material = "", [], [], set(), ""
    nested = []
    for n, _, v in _fields(b):
        if n == 1:
            name = _str(v)
        elif n == 2:
            f = dict(name="", number=0, type=0, type_name="", repeated=False, oneof=None, optional=False, ref_target="")
            for fn, _, fv in _fields(v):
                if fn == 1:
                    f["name"] = _str(fv)
                elif fn == 3:
                    f["number"] = fv
                elif fn == 4:
                    f["repeated"] = fv == LABEL_REPEATED
                elif fn == 5:
                    f["type"] = fv
                elif fn == 6:
                    f["type_name"] = _str(fv)
                elif fn == 9:
                    f["oneof"] = fv
                elif fn == 17:
                    f["optional"] = bool(fv)
                elif fn == 8:  # FieldOptions
                    for on, _, ov in _fields(fv):
                        if on == FIELD_OPT_REF:
                            f["ref_target"] = _str(ov)
            fields.append(Field(**f, doc=docs.get(path + (2, len(fields)), "")))
        elif n == 3:
            nested.append(v)
        elif n == 7:
            for on, _, ov in _fields(v):
                if on in (OPT_EVENT, OPT_STATE, OPT_CONFIG, OPT_VALUE, OPT_WEB, OPT_WEB_IN) and ov:
                    options.add(on)
                elif on == OPT_MATERIAL:
                    material = _str(ov)
        elif n == 8:
            oneofs.append(next(_str(x) for k, _, x in _fields(v) if k == 1))
    if nested:
        raise ValueError(f"{scope}.{name}: nested types aren't supported; declare them at file level")
    # proto3 `optional` is encoded as a synthetic one-field oneof (always after the real ones): drop it.
    synthetic = {f.oneof for f in fields if f.optional}
    for f in fields:
        if f.optional:
            f.oneof = None
    oneofs = oneofs[:len(oneofs) - len(synthetic)]
    full = f"{scope}.{name}"
    model.messages[full] = Message(full, name, fields, oneofs, options, fname, docs.get(path, ""), material)
    model.order.append((fname, full))

# ---------------------------------------------------------------------------- C# emit

def pascal(snake: str) -> str:
    return "".join(p[:1].upper() + p[1:] for p in snake.split("_") if p)


def enum_member(enum: Enum, proto_name: str) -> str:
    prefix = re.sub(r"(?<!^)(?=[A-Z])", "_", enum.name).upper() + "_"
    stripped = proto_name[len(prefix):] if proto_name.startswith(prefix) else proto_name
    return pascal(stripped.lower())


def _doc(text: str, indent: str) -> list[str]:
    if not text:
        return []
    lines = [l.strip() for l in text.splitlines()]
    esc = [l.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;") for l in lines]
    return [f"{indent}/// <summary>"] + [f"{indent}/// {l}".rstrip() for l in esc] + [f"{indent}/// </summary>"]


class Emitter:
    def __init__(self, model: Model):
        self.m = model
        for msg in model.messages.values():
            kinds = msg.options & {OPT_EVENT, OPT_STATE, OPT_CONFIG, OPT_VALUE}
            if len(kinds) > 1:
                raise ValueError(f"{msg.full}: use only one of (ln.event/state/config/value)")
            if msg.options & {OPT_EVENT, OPT_VALUE}:
                if any(f.repeated or f.oneof is not None for f in msg.fields):
                    raise ValueError(f"{msg.full}: events and values must be flat (no repeated/oneof)")
            for f in msg.fields:
                if f.oneof is not None and f.type != T_MESSAGE:
                    raise ValueError(f"{msg.full}.{f.name}: oneof members must be messages")
                if f.ref_target:
                    target = next((t for t in msg.fields if t.name == f.ref_target), None)
                    if f.type != 9 or target is None or target.type != T_MESSAGE or target.repeated != f.repeated:
                        raise ValueError(f"{msg.full}.{f.name}: (ln.ref) must be a string field naming a sibling message "
                                         f"field with the same repeated-ness ('{f.ref_target}')")
        self.content: dict[str, tuple[str, bytes]] = {}  # library: path -> (message full name, merged payload)

    def ctype(self, f: Field) -> str:
        if f.type in SCALARS:
            return SCALARS[f.type]
        if f.type == T_ENUM:
            return self.m.enums[f.type_name].name
        if f.type == T_MESSAGE:
            return self.m.messages[f.type_name].name
        raise ValueError(f"field {f.name}: unsupported proto type {f.type}")

    def file_body(self, fname: str) -> str:
        out = []
        for f, full in self.m.order:
            if f != fname:
                continue
            if full in self.m.enums:
                out += self.enum(self.m.enums[full])
            else:
                out += self.message(self.m.messages[full])
            out.append("")
        return "\n".join(out).rstrip() + "\n"

    def enum(self, e: Enum) -> list[str]:
        lines = _doc(e.doc, "    ") + (["    [System.Flags]"] if e.flags else []) + [f"    public enum {e.name}", "    {"]
        for name, num, doc in e.values:
            lines += _doc(doc, "        ") + [f"        {enum_member(e, name)} = {num},"]
        return lines + ["    }"]

    def message(self, m: Message) -> list[str]:
        if OPT_EVENT in m.options or OPT_VALUE in m.options:
            return self.struct(m, value=OPT_VALUE in m.options)
        if OPT_STATE in m.options:
            return self.state(m)
        return self.data_class(m)

    def struct(self, m: Message, value: bool) -> list[str]:
        name = m.name
        iface = f" : IEquatable<{name}>" if value else ""
        lines = _doc(m.doc, "    ") + [f"    public readonly struct {name}{iface}", "    {"]
        for f in m.fields:
            lines += _doc(f.doc, "        ") + [f"        public readonly {self.ctype(f)} {pascal(f.name)};"]
        if m.fields:
            params = ", ".join(f"{self.ctype(f)} {self.param(f)}" for f in m.fields)
            lines += ["", f"        public {name}({params})", "        {"]
            lines += [f"            {pascal(f.name)} = {self.param(f)};" for f in m.fields]
            lines += ["        }"]
        if value:
            eq = " && ".join(f"{pascal(f.name)}.Equals(other.{pascal(f.name)})" for f in m.fields) or "true"
            hash_args = ", ".join(pascal(f.name) for f in m.fields)
            lines += [
                "",
                f"        public bool Equals({name} other) => {eq};",
                f"        public override bool Equals(object obj) => obj is {name} other && Equals(other);",
                f"        public override int GetHashCode() => HashCode.Combine({hash_args});",
                f"        public static bool operator ==({name} a, {name} b) => a.Equals(b);",
                f"        public static bool operator !=({name} a, {name} b) => !a.Equals(b);",
            ]
        return lines + ["    }"]

    @staticmethod
    def param(f: Field) -> str:
        p = pascal(f.name)
        p = p[:1].lower() + p[1:]
        return "@" + p if p in ("object", "string", "event", "base", "default", "class", "params") else p

    def state(self, m: Message) -> list[str]:
        lines = _doc(m.doc, "    ") + [f"    public sealed class {m.name} : IDisposable", "    {"]
        props = []
        for f in m.fields:
            t, p = self.ctype(f), pascal(f.name)
            lines += _doc(f.doc, "        ")
            if f.repeated:
                lines.append(f"        public readonly ObservableList<{t}> {p} = new ObservableList<{t}>();")
            else:
                lines.append(f"        public readonly ReactiveProperty<{t}> {p} = new ReactiveProperty<{t}>();")
                props.append(p)
        lines += ["", "        public void Dispose()", "        {"]
        lines += [f"            {p}.Dispose();" for p in props]
        return lines + ["        }", "    }"]

    def data_class(self, m: Message) -> list[str]:
        lines = _doc(m.doc, "    ") + [f"    public sealed class {m.name}", "    {"]
        for f in m.fields:
            if f.ref_target:
                continue  # resolved at generation time; nothing at runtime
            t, p = self.ctype(f), pascal(f.name)
            lines += _doc(f.doc, "        ")
            if f.repeated:
                lines.append(f"        public List<{t}> {p} {{ get; }} = new List<{t}>();")
            else:
                lines.append(f"        public {t} {p} {{ get; set; }}")
        lines += ["", "        /// <summary>Shallow copy (nested message instances and lists are shared).</summary>",
                  f"        public {m.name} Clone() => ({m.name})MemberwiseClone();"]
        for oi, oname in enumerate(m.oneofs):
            cases = [f for f in m.fields if f.oneof == oi]
            enum_name = f"{pascal(oname)}Case"
            lines += ["", f"        public enum {enum_name}", "        {", "            None = 0,"]
            lines += [f"            {pascal(f.name)} = {f.number}," for f in cases]
            lines += ["        }", ""]
            expr = "\n".join(f"            {pascal(f.name)} != null ? {enum_name}.{pascal(f.name)} :" for f in cases)
            lines += [f"        /// <summary>Which <c>{oname}</c> case is set.</summary>",
                      f"        public {enum_name} {pascal(oname)} =>", expr, f"            {enum_name}.None;"]
        return lines + ["    }"]

    # ------------------------------------------------------------------ content values

    def literal(self, full: str, payload: bytes, indent: str) -> str:
        """C# expression constructing message `full` from wire bytes."""
        msg = self.m.messages[full]
        values: dict[int, list] = {}
        by_num = {f.number: f for f in msg.fields}
        for num, wt, v in self._expand(msg, payload):
            f = by_num.get(num)
            if f is None:
                raise ValueError(f"{msg.full}: unknown field {num} in content")
            if f.repeated and wt == 2 and f.type not in (9, T_MESSAGE):
                values.setdefault(num, []).extend(self.scalar(f, x, 0) for x in self._packed(f, v))
            else:
                # Repeated items sit one level deeper (inside the collection braces).
                item_indent = indent + ("        " if f.repeated else "    ")
                values.setdefault(num, []).append(self.value(f, v, wt, item_indent))
        if msg.material:
            missing = [f.name for f in msg.fields if f.number not in values]
            if missing:
                raise ValueError(f"content for {msg.name} (material {msg.material}) must set every field; "
                                 f"missing: {', '.join(missing)}")
        if OPT_VALUE in msg.options:
            args = ", ".join(values.get(f.number, [self.default(f)])[-1] for f in msg.fields)
            return f"new {msg.name}({args})"
        inner = indent + "    "
        parts = []
        for f in msg.fields:
            if f.number not in values:
                continue
            p, vals = pascal(f.name), values[f.number]
            if f.repeated:
                items = ",\n".join(f"{inner}    {x}" for x in vals)
                parts.append(f"{inner}{p} =\n{inner}{{\n{items},\n{inner}}},")
            else:
                parts.append(f"{inner}{p} = {vals[-1]},")
        if not parts:
            return f"new {msg.name}()"
        return f"new {msg.name}\n{indent}{{\n" + "\n".join(parts) + f"\n{indent}}}"

    def _expand(self, msg: Message, payload: bytes) -> list:
        """Resolve (ln.ref) fields into entries of their target field (refs first, so inline values override),
        then merge repeated occurrences of singular message fields: concatenating wire bytes is protobuf's merge."""
        by_name = {f.name: f for f in msg.fields}
        by_num = {f.number: f for f in msg.fields}
        refs, rest = [], []
        for num, wt, v in _fields(payload):
            f = by_num.get(num)
            if f is not None and f.ref_target:
                target = by_name[f.ref_target]
                path = _str(v)
                if path not in self.content:
                    raise ValueError(f"{msg.name}.{f.name}: reference '{path}' not found (content/{path}.txtpb)")
                ref_full, ref_payload = self.content[path]
                if ref_full != target.type_name:
                    raise ValueError(f"{msg.name}.{f.name}: '{path}' is a {ref_full.split('.')[-1]}, "
                                     f"expected {target.type_name.split('.')[-1]}")
                refs.append((target.number, 2, ref_payload))
            else:
                rest.append((num, wt, v))
        out, merged = [], {}
        for num, wt, v in refs + rest:
            f = by_num.get(num)
            if f is not None and f.type == T_MESSAGE and not f.repeated:
                merged[num] = merged.get(num, b"") + v
            else:
                out.append((num, wt, v))
        return out + [(n, 2, b) for n, b in merged.items()]

    def _packed(self, f: Field, b: bytes):
        if f.type == 2:
            return [b[i:i + 4] for i in range(0, len(b), 4)]
        if f.type == 1:
            return [b[i:i + 8] for i in range(0, len(b), 8)]
        return list(_packed_varints(b))

    def value(self, f: Field, v, wt, indent: str) -> str:
        if f.type == T_MESSAGE:
            return self.literal(f.type_name, v, indent)
        if f.type == 9:
            return json.dumps(_str(v), ensure_ascii=False)
        return self.scalar(f, v, wt)

    def scalar(self, f: Field, v, wt) -> str:
        if f.type == 2:
            x = struct.unpack("<f", v)[0]
            return f"{float(f'{x:.7g}')!r}".replace("inf", "float.PositiveInfinity") + "f"
        if f.type == 1:
            return repr(struct.unpack("<d", v)[0])
        if f.type == 8:
            return "true" if v else "false"
        if f.type == T_ENUM:
            e = self.m.enums[f.type_name]
            name = next((n for n, num, _ in e.values if num == v), None)
            return f"{e.name}.{enum_member(e, name)}" if name else f"({e.name}){v}"
        if f.type in (5, 3):
            return str(v if v < 2**63 else v - 2**64)
        return str(v)

    def default(self, f: Field) -> str:
        return {"string": '""', "bool": "false"}.get(self.ctype(f), f"default({self.ctype(f)})")


def material_bindings(model: Model, em: "Emitter", namespace: str) -> str:
    """ApplyTo(Material) per (ln.material) message + value-type -> Unity conversions."""
    lines = ["    public static class MaterialBindings", "    {"]
    values = [m for _, full in model.order if (m := model.messages.get(full)) and OPT_VALUE in m.options]
    for v in values:
        floats = [f for f in v.fields if f.type == 2]
        if v.name == "Rgba" and len(floats) == 4:
            lines.append(f"        public static Color ToColor(this {v.name} c) => new Color(c.R, c.G, c.B, c.A);")
        elif len(floats) == len(v.fields) and len(floats) in (2, 3, 4):
            unity = {2: "Vector2", 3: "Vector3", 4: "Vector4"}[len(floats)]
            args = ", ".join(f"v.{pascal(f.name)}" for f in floats)
            lines.append(f"        public static {unity} To{unity}(this {v.name} v) => new {unity}({args});")
    for _, full in model.order:
        m = model.messages.get(full)
        if not m or not m.material:
            continue
        lines += ["", f"        public const string {m.name}Shader = {json.dumps(m.material)};"]
        for f in m.fields:
            lines.append(f"        static readonly int {m.name}_{pascal(f.name)} = Shader.PropertyToID(\"_{pascal(f.name)}\");")
        lines += ["", f"        /// <summary>Copies every field to the matching {m.material} property.</summary>",
                  f"        public static void ApplyTo(this {m.name} look, Material material)", "        {"]
        for f in m.fields:
            pid, val = f"{m.name}_{pascal(f.name)}", f"look.{pascal(f.name)}"
            if f.repeated:
                raise ValueError(f"{m.full}.{f.name}: repeated fields can't bind to a material")
            if f.type in (1, 2):
                lines.append(f"            material.SetFloat({pid}, {val});")
            elif f.type in (3, 5, 13):
                lines.append(f"            material.SetInteger({pid}, (int){val});")
            elif f.type == 8:
                lines.append(f"            material.SetFloat({pid}, {val} ? 1f : 0f);")
            elif f.type == T_MESSAGE and model.messages[f.type_name].name == "Rgba":
                lines.append(f"            material.SetColor({pid}, {val}.ToColor());")
            elif f.type == T_MESSAGE and OPT_VALUE in model.messages[f.type_name].options:
                n = len(model.messages[f.type_name].fields)
                conv = {2: "ToVector2()", 3: "ToVector3()", 4: "ToVector4()"}[n]
                lines.append(f"            material.SetVector({pid}, {val}.{conv});")
            else:
                raise ValueError(f"{m.full}.{f.name}: type can't bind to a material property")
        lines.append("        }")
    lines.append("    }")
    return (HEADER.format(src="proto/**/*.proto (ln.material)") + "\nusing UnityEngine;\n"
            + f"\nnamespace {namespace}\n{{\n" + "\n".join(lines) + "\n}\n")


HEADER = "// <auto-generated>\n// Generated by tools/ln.py gen from {src}. Do not edit; change the source and regenerate.\n// </auto-generated>\n"


def generate(model: Model, content: list[tuple[str, str, bytes]], namespace: str,
             library: dict[str, tuple[str, bytes]] | None = None) -> dict[str, str]:
    """Return {relative path: text} for everything under Generated/. `content` = baked top-level configs,
    `library` = every content file by path (targets for (ln.ref))."""
    em = Emitter(model)
    em.content = library or {}
    files: dict[str, str] = {}
    usings = "using System;\nusing System.Collections.Generic;\nusing ObservableCollections;\nusing R3;\n"
    for fname in sorted({f for f, _ in model.order}):
        stem = pascal(Path(fname).stem)
        files[f"Model/{stem}.g.cs"] = (HEADER.format(src=f"proto/{fname}") + "\n" + usings
                                      + f"\nnamespace {namespace}\n{{\n" + em.file_body(fname) + "}\n")

    # Baked content: one static readonly field per content file.
    body = []
    for rel, full, payload in sorted(content):
        msg = model.messages.get(full)
        if msg is None or OPT_CONFIG not in msg.options:
            raise ValueError(f"{rel}: message {full} must have option (ln.config)")
        prop = pascal(Path(rel).stem)
        body += [f"        /// <summary>From <c>{rel}</c>.</summary>",
                 f"        public static readonly {msg.name} {prop} = {em.literal(full, payload, '        ')};", ""]
    files["Model/GameContent.g.cs"] = (HEADER.format(src="content/*.txtpb") + "\n" + usings
                                      + f"\nnamespace {namespace}\n{{\n    public static class GameContent\n    {{\n"
                                      + "\n".join(body).rstrip() + "\n    }\n}\n")

    # DI: MessagePipe broker per (ln.event); state classes as singletons.
    events = [m for _, full in model.order if (m := model.messages.get(full)) and OPT_EVENT in m.options]
    states = [m for _, full in model.order if (m := model.messages.get(full)) and OPT_STATE in m.options]
    di = ["    public static class ModelRegistration", "    {",
          "        /// <summary>Registers a MessagePipe broker for every (ln.event) message.</summary>",
          "        public static void RegisterModelEvents(this IContainerBuilder builder, MessagePipeOptions options)",
          "        {"]
    di += [f"            builder.RegisterMessageBroker<{m.name}>(options);" for m in events]
    di += ["        }", "",
           "        /// <summary>Registers every (ln.state) class as a singleton.</summary>",
           "        public static void RegisterModelState(this IContainerBuilder builder)", "        {"]
    di += [f"            builder.Register<{m.name}>(Lifetime.Singleton);" for m in states]
    di += ["        }", "    }"]
    files["Di/ModelRegistration.g.cs"] = (HEADER.format(src="proto/**/*.proto") + "\nusing MessagePipe;\nusing VContainer;\n"
                                        + f"\nnamespace {namespace}\n{{\n" + "\n".join(di) + "\n}\n")

    # (ln.web): page bridge. "@web/" paths land in web/src/generated (see ln.py gen).
    from webgen import web_files
    files.update(web_files(model, em, namespace))

    files["Unity/MaterialBindings.g.cs"] = material_bindings(model, em, namespace)
    files["Unity/LuckyNumbers.Model.Unity.asmdef"] = json.dumps({
        "name": "LuckyNumbers.Model.Unity", "rootNamespace": namespace,
        "references": ["LuckyNumbers.Model"], "autoReferenced": True}, indent=4) + "\n"

    files["Model/LuckyNumbers.Model.asmdef"] = json.dumps({
        "name": "LuckyNumbers.Model", "rootNamespace": namespace, "references": [],
        "autoReferenced": True, "noEngineReferences": True}, indent=4) + "\n"
    files["Di/LuckyNumbers.Model.Di.asmdef"] = json.dumps({
        "name": "LuckyNumbers.Model.Di", "rootNamespace": namespace,
        "references": ["LuckyNumbers.Model", "VContainer", "MessagePipe", "MessagePipe.VContainer",
                       "UniTask"],  # MessagePipe's Subscribe overloads mention UniTask
        "autoReferenced": True}, indent=4) + "\n"
    return files


def check_materials(model: Model, shader_root: Path) -> None:
    """Every (ln.material) field must match a property declared by that shader (silent no-op otherwise)."""
    shaders = {}
    for f in shader_root.rglob("*.shader"):
        text = f.read_text(encoding="utf-8")
        m = re.search(r'^\s*Shader\s+"([^"]+)"', text, re.M)
        props = re.search(r"Properties\s*\{(.*?)\n\s*\}", text, re.S)
        if m:
            shaders[m.group(1)] = set(re.findall(r"^\s*(_\w+)\s*\(", props.group(1), re.M)) if props else set()
    for msg in model.messages.values():
        if not msg.material:
            continue
        plain = [f.name for f in msg.fields if f.type != T_MESSAGE and not f.optional]
        if plain:
            raise ValueError(f"{msg.name}: declare scalar fields `optional` so an explicit 0 counts as set: "
                             f"{', '.join(plain)}")
        if msg.material not in shaders:
            raise ValueError(f"{msg.name}: (ln.material) shader '{msg.material}' not found under {shader_root}")
        missing = [f"_{pascal(f.name)}" for f in msg.fields if f"_{pascal(f.name)}" not in shaders[msg.material]]
        if missing:
            raise ValueError(f"{msg.name}: shader '{msg.material}' has no properties {', '.join(missing)}")


def decode(model: Model, full: str, payload: bytes) -> dict:
    """A content payload as plain Python values (field name -> str/int/float/bool/dict, lists for repeated),
    for tools that render content to other formats (e.g. Markdown). Unset fields are absent."""
    msg = model.messages[full]
    by_num = {f.number: f for f in msg.fields}
    out: dict = {}
    for num, wt, v in _fields(payload):
        f = by_num.get(num)
        if f is None:
            continue
        if f.type == T_MESSAGE:
            if f.repeated:
                out.setdefault(f.name, []).append(decode(model, f.type_name, v))
            else:   # repeated occurrences of a singular message merge
                out[f.name] = {**out.get(f.name, {}), **decode(model, f.type_name, v)}
            continue
        if f.type == 9:
            x = _str(v)
        elif f.type == 2:
            x = struct.unpack("<f", v)[0] if wt == 5 else [struct.unpack("<f", v[i:i + 4])[0] for i in range(0, len(v), 4)]
        elif f.type == 8:
            x = bool(v)
        else:
            x = v
        if f.repeated:
            out.setdefault(f.name, []).extend(x if isinstance(x, list) else [x])
        else:
            out[f.name] = x
    return out


def load(protoc: str, include: Path, proto_dir: Path, content_dir: Path, header_re) -> tuple[Model, dict, dict]:
    """Descriptors + every content file: (model, resolved {path: (full type, payload)}, raw)."""
    import tempfile
    protos = sorted(str(p.relative_to(proto_dir)) for p in proto_dir.rglob("*.proto"))
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp) / "descriptors.pb"
        subprocess.run([protoc, f"-I{proto_dir}", f"-I{include}", "--include_imports",
                        "--include_source_info", f"--descriptor_set_out={out}", *protos],
                       capture_output=True, check=True)
        desc = out.read_bytes()
    model = load_descriptors(desc)

    # Every content file by path ("space/looks/planets/ice"); `# proto-extends: <path>` merges a base first.
    raw = {}
    for txt in sorted(content_dir.rglob("*.txtpb")):
        key = txt.relative_to(content_dir).with_suffix("").as_posix()
        header = dict(header_re.findall(txt.read_text(encoding="utf-8")))
        with open(txt, "rb") as f:
            payload = subprocess.run([protoc, f"-I{proto_dir}", f"-I{include}", f"--encode={header['message']}",
                                      header["file"]], stdin=f, capture_output=True, check=True).stdout
        raw[key] = ("." + header["message"], payload, header.get("extends"), str(txt.relative_to(content_dir.parent)))

    resolved: dict[str, tuple[str, bytes]] = {}

    def resolve(key: str, stack: tuple = ()) -> tuple[str, bytes]:
        if key in resolved:
            return resolved[key]
        if key in stack:
            raise ValueError(f"content extends cycle: {' -> '.join(stack + (key,))}")
        if key not in raw:
            raise ValueError(f"content/{stack[-1]}.txtpb extends '{key}', which doesn't exist")
        full, payload, base, _ = raw[key]
        if base:
            base_full, base_payload = resolve(base, stack + (key,))
            if base_full != full:
                raise ValueError(f"content/{key}.txtpb ({full[1:]}) extends {base} ({base_full[1:]}): types differ")
            payload = base_payload + payload  # base first: this file's fields override
        resolved[key] = (full, payload)
        return resolved[key]

    for key in raw:
        resolve(key)
    return model, resolved, raw


def run(protoc: str, include: Path, proto_dir: Path, content_dir: Path, header_re,
        shader_root: Path | None = None) -> dict[str, str]:
    model, resolved, raw = load(protoc, include, proto_dir, content_dir, header_re)
    if shader_root is not None:
        check_materials(model, shader_root)
    # Top-level files are game configs (baked into GameContent); subfolders are a library for refs/extends.
    content = [(raw[k][3], *resolved[k]) for k in raw if "/" not in k]
    return generate(model, content, "LuckyNumbers.Model", resolved)

