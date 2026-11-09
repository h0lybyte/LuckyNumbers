"""(ln.web) messages -> the Unity <-> page bridge (called from protogen.generate).

Emits:
  Di/WebMirror.g.cs        C#: subscribes to every (ln.web) state/event, writes JSON without reflection
                           (IL2CPP-safe), and publishes page-sent events back into MessagePipe.
  @web/ln.g.ts             TypeScript types for the same messages (web/src/generated/ln.g.ts).

Wire format (one string per frame, Unity -> page, only when something changed):
  {"s":{"FlowState":{"scene":"Planet"}},"e":[["SceneEntered",{"scene":"Planet"}]]}
Page -> Unity: a JSON array of [name, payload] pairs ((ln.web_in) events only), drained once per frame.

JSON keys are lowerCamelCase proto field names; enums are their C# member names ("Planet");
(ln.value) messages are nested objects; repeated state fields are arrays.
"""
from __future__ import annotations

from protogen import (OPT_EVENT, OPT_STATE, OPT_VALUE, OPT_WEB, OPT_WEB_IN, SCALARS, T_ENUM, T_MESSAGE, HEADER,
                      Emitter, Field, Message, Model, enum_member, pascal)


def camel(snake: str) -> str:
    p = pascal(snake)
    return p[:1].lower() + p[1:]


def _check(model: Model, msg: Message) -> None:
    if not msg.options & {OPT_STATE, OPT_EVENT}:
        raise ValueError(f"{msg.full}: (ln.web) goes on an (ln.state) or (ln.event)")
    if OPT_WEB_IN in msg.options and (OPT_EVENT not in msg.options
                                      or not all(f.type in SCALARS or f.type == T_ENUM for f in msg.fields)):
        raise ValueError(f"{msg.full}: (ln.web_in) goes on an (ln.event) whose fields are all scalars/enums")
    for f in msg.fields:
        if f.type in SCALARS or f.type == T_ENUM:
            continue
        if f.type == T_MESSAGE and OPT_VALUE in model.messages[f.type_name].options:
            inner = model.messages[f.type_name]
            if all(x.type in SCALARS or x.type == T_ENUM for x in inner.fields):
                continue
        raise ValueError(f"{msg.full}.{f.name}: (ln.web) fields must be scalars, enums or flat (ln.value) messages")


def _inbound(msg: Message) -> bool:
    """Events the page may publish: (ln.web_in)."""
    return OPT_WEB_IN in msg.options


# ---------------------------------------------------------------------------------------------- C#

class _CSharp:
    def __init__(self, model: Model, em: Emitter):
        self.m, self.em = model, em

    def write_value(self, f: Field, expr: str) -> str:
        """Statement appending `expr` (of field f's element type) as JSON to `b`."""
        if f.type == T_ENUM:
            return f"Json.Str(b, {self.m.enums[f.type_name].name}Name({expr}));"
        if f.type == T_MESSAGE:
            return f"Write(b, {expr});"
        if f.type == 9:
            return f"Json.Str(b, {expr});"
        if f.type == 8:
            return f"b.Append({expr} ? \"true\" : \"false\");"
        return f"Json.Num(b, {expr});"

    def writer(self, msg: Message, state: bool) -> list[str]:
        arg = "s" if state else "e"
        out = [f"        static void Write(StringBuilder b, {msg.name} {arg})", "        {", "            b.Append('{');"]
        for i, f in enumerate(msg.fields):
            key = ("," if i else "") + f'\\"{camel(f.name)}\\":'
            out.append(f'            b.Append("{key}");')
            member = f"{arg}.{pascal(f.name)}" + (".Value" if state and not f.repeated else "")
            if f.repeated:
                out += ["            b.Append('[');", "            {", "                var first = true;",
                        f"                foreach (var x in {arg}.{pascal(f.name)})", "                {",
                        "                    if (!first) b.Append(',');", "                    first = false;",
                        f"                    {self.write_value(f, 'x')}", "                }", "            }",
                        "            b.Append(']');"]
            else:
                out.append(f"            {self.write_value(f, member)}")
        return out + ["            b.Append('}');", "        }"]

    def reader_value(self, f: Field, key: str) -> str:
        t = self.em.ctype(f)
        if f.type == T_ENUM:
            return f"{t}Parse(Json.Get(o, \"{key}\"))"
        if f.type == 9:
            return f"Json.Get(o, \"{key}\") as string ?? \"\""
        if f.type == 8:
            return f"Json.Get(o, \"{key}\") is bool {key}V && {key}V"
        return f"({t})Json.Number(Json.Get(o, \"{key}\"))"

    def file(self, namespace: str, states: list[Message], events: list[Message], enums: list) -> str:
        values = sorted({f.type_name for m in states + events for f in m.fields if f.type == T_MESSAGE})
        L = [HEADER.format(src="proto/**/*.proto ((ln.web) messages)"),
             "using System;", "using System.Collections.Generic;", "using System.Globalization;", "using System.Text;",
             "using MessagePipe;", "using ObservableCollections;", "using R3;", "using VContainer;", "",
             f"namespace {namespace}", "{",
             "    /// <summary>Where WebMirror sends things; implemented by the web bridge.</summary>",
             "    public interface IWebSink",
             "    {",
             "        /// <summary>State <paramref name=\"index\"/> (into <see cref=\"WebMirror.States\"/>) changed;"
             " coalesce and call WebMirror.WriteState once per flush.</summary>",
             "        void MarkState(int index);",
             "        /// <summary>Start an event entry; the mirror writes its payload JSON into the returned"
             " buffer, then calls EndEvent.</summary>",
             "        StringBuilder BeginEvent(string name);",
             "        void EndEvent();",
             "    }", "",
             "    /// <summary>Every (ln.web) state and event, mirrored to the page as JSON (no reflection).</summary>",
             "    public static class WebMirror",
             "    {",
             "        public static readonly string[] States =",
             "        {"]
        L += [f'            "{m.name}",' for m in states]
        L += ["        };", "",
              "        /// <summary>Subscribe to every (ln.web) state and event. Marks every state once so the page"
              " gets a full snapshot.</summary>",
              "        public static IDisposable Bind(IObjectResolver c, IWebSink sink)",
              "        {",
              "            var d = new CompositeDisposable();"]
        for i, m in enumerate(states):
            L += ["            {", f"                var s = c.Resolve<{m.name}>();"]
            for f in m.fields:
                p = pascal(f.name)
                if f.repeated:
                    t = self.em.ctype(f)
                    L += [f"                NotifyCollectionChangedEventHandler<{t}> h{p} ="
                          f" (in NotifyCollectionChangedEventArgs<{t}> _) => sink.MarkState({i});",
                          f"                s.{p}.CollectionChanged += h{p};",
                          f"                d.Add(Disposable.Create(() => s.{p}.CollectionChanged -= h{p}));"]
                else:
                    L.append(f"                d.Add(s.{p}.Subscribe(_ => sink.MarkState({i})));")
            L += [f"                sink.MarkState({i});", "            }"]
        for m in events:
            L += [f"            d.Add(c.Resolve<ISubscriber<{m.name}>>().Subscribe(e =>",
                  "            {", f'                Write(sink.BeginEvent("{m.name}"), e);',
                  "                sink.EndEvent();", "            }));"]
        L += ["            return d;", "        }", "",
              "        /// <summary>Append state <paramref name=\"index\"/>'s JSON object.</summary>",
              "        public static void WriteState(IObjectResolver c, int index, StringBuilder b)",
              "        {", "            switch (index)", "            {"]
        L += [f"                case {i}: Write(b, c.Resolve<{m.name}>()); break;" for i, m in enumerate(states)]
        L += ["            }", "        }", "",
              "        /// <summary>Publish a page-sent event. False if it isn't an inbound (ln.web) event.</summary>",
              "        public static bool Publish(IObjectResolver c, string name, Dictionary<string, object> o)",
              "        {", "            switch (name)", "            {"]
        for m in events:
            if not _inbound(m):
                continue
            args = ", ".join(self.reader_value(f, camel(f.name)) for f in m.fields)
            L += [f'                case "{m.name}":',
                  f"                    c.Resolve<IPublisher<{m.name}>>().Publish(new {m.name}({args}));",
                  "                    return true;"]
        L += ["                default:", "                    return false;", "            }", "        }", ""]
        for m in states:
            L += self.writer(m, state=True) + [""]
        for m in events:
            L += self.writer(m, state=False) + [""]
        for full in values:
            L += self.writer(self.m.messages[full], state=False) + [""]
        for e in enums:
            L += [f"        static string {e.name}Name({e.name} v)", "        {", "            switch (v)", "            {"]
            L += [f'                case {e.name}.{enum_member(e, n)}: return "{enum_member(e, n)}";' for n, _, _ in e.values]
            L += ["                default: return ((int)v).ToString(CultureInfo.InvariantCulture);", "            }",
                  "        }", "",
                  f"        static {e.name} {e.name}Parse(object v)", "        {",
                  f"            if (v is double n) return ({e.name})(int)n;", "            switch (v as string)", "            {"]
            L += [f'                case "{enum_member(e, n)}": return {e.name}.{enum_member(e, n)};' for n, _, _ in e.values]
            L += [f"                default: return default;", "            }", "        }", ""]
        L += [JSON_HELPERS, "    }", "}", ""]
        return "\n".join(L)


JSON_HELPERS = r'''        /// <summary>Minimal JSON: write numbers/strings, read a flat object (string, double, bool, null).</summary>
        public static class Json
        {
            public static void Num(StringBuilder b, double v)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) b.Append("null");
                else b.Append(v.ToString("R", CultureInfo.InvariantCulture));
            }

            public static void Num(StringBuilder b, float v)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) b.Append("null");
                else b.Append(v.ToString("R", CultureInfo.InvariantCulture));
            }

            public static void Num(StringBuilder b, int v) => b.Append(v.ToString(CultureInfo.InvariantCulture));
            public static void Num(StringBuilder b, uint v) => b.Append(v.ToString(CultureInfo.InvariantCulture));
            public static void Num(StringBuilder b, long v) => b.Append(v.ToString(CultureInfo.InvariantCulture));

            public static void Str(StringBuilder b, string s)
            {
                b.Append('"');
                if (s != null)
                {
                    foreach (var ch in s)
                    {
                        switch (ch)
                        {
                            case '"': b.Append("\\\""); break;
                            case '\\': b.Append("\\\\"); break;
                            case '\n': b.Append("\\n"); break;
                            case '\r': b.Append("\\r"); break;
                            case '\t': b.Append("\\t"); break;
                            default:
                                if (ch < ' ') b.Append("\\u").Append(((int)ch).ToString("x4"));
                                else b.Append(ch);
                                break;
                        }
                    }
                }
                b.Append('"');
            }

            public static object Get(Dictionary<string, object> o, string key) =>
                o != null && o.TryGetValue(key, out var v) ? v : null;

            public static double Number(object v) => v is double d ? d : v is bool t && t ? 1 : 0;

            /// <summary>Parse <c>[["Name",{...}],...]</c>: page-sent events.</summary>
            public static List<KeyValuePair<string, Dictionary<string, object>>> ParseBatch(string json)
            {
                var list = new List<KeyValuePair<string, Dictionary<string, object>>>();
                var i = 0;
                Ws(json, ref i);
                if (!Eat(json, ref i, '[')) return list;
                while (true)
                {
                    Ws(json, ref i);
                    if (Eat(json, ref i, ']') || i >= json.Length) return list;
                    if (!Eat(json, ref i, '[')) return list;
                    Ws(json, ref i);
                    var name = ReadString(json, ref i);
                    Ws(json, ref i);
                    Eat(json, ref i, ',');
                    Ws(json, ref i);
                    var payload = json[i] == '{' ? ReadObject(json, ref i) : new Dictionary<string, object>();
                    Ws(json, ref i);
                    Eat(json, ref i, ']');
                    Ws(json, ref i);
                    Eat(json, ref i, ',');
                    if (name != null) list.Add(new KeyValuePair<string, Dictionary<string, object>>(name, payload));
                }
            }

            static Dictionary<string, object> ReadObject(string s, ref int i)
            {
                var o = new Dictionary<string, object>();
                Eat(s, ref i, '{');
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || Eat(s, ref i, '}')) return o;
                    var key = ReadString(s, ref i);
                    Ws(s, ref i);
                    Eat(s, ref i, ':');
                    Ws(s, ref i);
                    o[key ?? ""] = ReadValue(s, ref i);
                    Ws(s, ref i);
                    Eat(s, ref i, ',');
                }
            }

            static object ReadValue(string s, ref int i)
            {
                if (i >= s.Length) return null;
                var c = s[i];
                if (c == '"') return ReadString(s, ref i);
                if (c == '{') return ReadObject(s, ref i);
                if (c == '[') { Skip(s, ref i); return null; }
                if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
                if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
                if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
                var start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                return double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture,
                                       out var n) ? n : 0d;
            }

            static void Skip(string s, ref int i)
            {
                var depth = 0;
                var inString = false;
                for (; i < s.Length; i++)
                {
                    var c = s[i];
                    if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                    if (c == '"') inString = true;
                    else if (c == '[' || c == '{') depth++;
                    else if ((c == ']' || c == '}') && --depth == 0) { i++; return; }
                }
            }

            static string ReadString(string s, ref int i)
            {
                if (!Eat(s, ref i, '"')) return null;
                var b = new StringBuilder();
                while (i < s.Length)
                {
                    var c = s[i++];
                    if (c == '"') return b.ToString();
                    if (c != '\\' || i >= s.Length) { b.Append(c); continue; }
                    var e = s[i++];
                    switch (e)
                    {
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out var u)) b.Append((char)u);
                            i += 4;
                            break;
                        default: b.Append(e); break;
                    }
                }
                return b.ToString();
            }

            static void Ws(string s, ref int i)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            static bool Eat(string s, ref int i, char c)
            {
                if (i < s.Length && s[i] == c) { i++; return true; }
                return false;
            }
        }'''


# ---------------------------------------------------------------------------------------------- TS

TS_SCALARS = {1: "number", 2: "number", 3: "number", 5: "number", 8: "boolean", 9: "string", 13: "number"}


def _ts_doc(text: str, indent: str) -> list[str]:
    if not text:
        return []
    lines = [l.strip().replace("*/", "* /") for l in text.splitlines()]
    if len(lines) == 1:
        return [f"{indent}/** {lines[0]} */"]
    return [f"{indent}/**"] + [f"{indent} * {l}".rstrip() for l in lines] + [f"{indent} */"]


def _ts_type(model: Model, f: Field) -> str:
    if f.type in TS_SCALARS:
        t = TS_SCALARS[f.type]
    elif f.type == T_ENUM:
        t = model.enums[f.type_name].name
    else:
        t = model.messages[f.type_name].name
    return f"{t}[]" if f.repeated else t


def _ts(model: Model, states: list[Message], events: list[Message], enums: list) -> str:
    values = sorted({f.type_name for m in states + events for f in m.fields if f.type == T_MESSAGE})
    L = ["// Generated by tools/ln.py gen from proto/**/*.proto ((ln.web) messages). Do not edit; change the",
         "// source and regenerate.", "/* eslint-disable */", ""]
    for e in enums:
        L += _ts_doc(e.doc, "")
        L.append(f"export type {e.name} =")
        L += [f'  | "{enum_member(e, n)}"' for n, _, _ in e.values]
        L[-1] += ";"
        L.append("")
    for msg in [model.messages[v] for v in values] + states + events:
        L += _ts_doc(msg.doc, "")
        L.append(f"export interface {msg.name} {{")
        for f in msg.fields:
            L += _ts_doc(f.doc, "  ")
            L.append(f"  {camel(f.name)}: {_ts_type(model, f)};")
        L += ["}", ""]
    L.append("/** Every (ln.web) state, by name: the page holds the latest snapshot of each. */")
    L.append("export interface WebStates {")
    L += [f"  {m.name}: {m.name};" for m in states]
    L += ["}", "", "/** Every (ln.web) event, by name: Unity -> page as they happen. */", "export interface WebEvents {"]
    L += [f"  {m.name}: {m.name};" for m in events]
    L += ["}", "", "/** Events the page may publish into the game. */"]
    inbound = [m.name for m in events if _inbound(m)]
    L.append("export type InboundEvent = " + (" | ".join(f'"{n}"' for n in inbound) or "never") + ";")
    L += ["", "export const STATE_NAMES = [" + ", ".join(f'"{m.name}"' for m in states) + "] as const;", ""]
    return "\n".join(L)


# ---------------------------------------------------------------------------------------------- entry

def web_files(model: Model, em: Emitter, namespace: str) -> dict[str, str]:
    msgs = [m for _, full in model.order if (m := model.messages.get(full)) and m.options & {OPT_WEB, OPT_WEB_IN}]
    for m in msgs:
        _check(model, m)
    states = [m for m in msgs if OPT_STATE in m.options]
    events = [m for m in msgs if OPT_EVENT in m.options]
    used = {f.type_name for m in msgs for f in m.fields if f.type == T_ENUM}
    used |= {x.type_name for m in msgs for f in m.fields if f.type == T_MESSAGE
             for x in model.messages[f.type_name].fields if x.type == T_ENUM}
    enums = [model.enums[full] for _, full in model.order if full in used]
    return {
        "Di/WebMirror.g.cs": _CSharp(model, em).file(namespace, states, events, enums),
        "@web/ln.g.ts": _ts(model, states, events, enums),
    }
