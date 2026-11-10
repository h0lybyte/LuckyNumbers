#!/usr/bin/env python3
"""LuckyNumbers dev tasks. Standard library only, works on macOS / Windows / Linux.

    python3 tools/ln.py check         headless compile, report C# errors
    python3 tools/ln.py build-web     headless Web build -> LuckyNumbers/Builds/WebGL
    python3 tools/ln.py serve         serve the Web build with COOP/COEP headers
    python3 tools/ln.py bake-grasps   bake weapons' finger grips into weapons.json (needs the build served)
    python3 tools/ln.py deploy        push the Web build to itch.io with butler
    python3 tools/ln.py bump          start the next CHANGELOG.md entry (patch; minor/major) from merged PRs

Unity is located from ProjectVersion.txt + the Unity Hub install folder.
Override with UNITY_PATH=/path/to/Unity(.exe).
"""
from __future__ import annotations

import argparse
import http.server
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import time
import webbrowser
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "LuckyNumbers"
LOGS = PROJECT / "Logs"
WEB_BUILD = PROJECT / "Builds" / "WebGL"
LOCKFILE = PROJECT / "Temp" / "UnityLockfile"
SYSTEM = platform.system()  # "Darwin" | "Windows" | "Linux"


def fail(msg: str, code: int = 1) -> int:
    print(f"error: {msg}", file=sys.stderr)
    return code


# --------------------------------------------------------------------------- Unity

def unity_version() -> str:
    text = (PROJECT / "ProjectSettings" / "ProjectVersion.txt").read_text(encoding="utf-8")
    m = re.search(r"m_EditorVersion:\s*(\S+)", text)
    if not m:
        raise RuntimeError("m_EditorVersion not found in ProjectVersion.txt")
    return m.group(1)


def hub_editor_dirs() -> list[Path]:
    """Default Unity Hub editor folders plus Hub's custom install location, if set."""
    home = Path.home()
    if SYSTEM == "Darwin":
        dirs = [Path("/Applications/Unity/Hub/Editor")]
        hub_cfg = home / "Library" / "Application Support" / "UnityHub"
    elif SYSTEM == "Windows":
        dirs = [Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Unity" / "Hub" / "Editor"]
        hub_cfg = Path(os.environ.get("APPDATA", home / "AppData" / "Roaming")) / "UnityHub"
    else:
        dirs = [home / "Unity" / "Hub" / "Editor"]
        hub_cfg = home / ".config" / "UnityHub"
    try:
        custom = json.loads((hub_cfg / "secondaryInstallPath.json").read_text(encoding="utf-8"))
        if custom:
            dirs.insert(0, Path(custom))
    except (OSError, ValueError):
        pass
    return dirs


def editor_binary(version_dir: Path) -> Path:
    if SYSTEM == "Darwin":
        return version_dir / "Unity.app" / "Contents" / "MacOS" / "Unity"
    if SYSTEM == "Windows":
        return version_dir / "Editor" / "Unity.exe"
    return version_dir / "Editor" / "Unity"


def find_unity() -> Path:
    env = os.environ.get("UNITY_PATH")
    if env:
        p = Path(env)
        if not p.is_file():
            raise RuntimeError(f"UNITY_PATH does not point to a file: {p}")
        return p
    version = unity_version()
    tried = []
    for d in hub_editor_dirs():
        candidate = editor_binary(d / version)
        tried.append(str(candidate))
        if candidate.is_file():
            return candidate
    raise RuntimeError(f"Unity {version} not found. Install it via Unity Hub or set UNITY_PATH.\n  tried: "
                       + "\n         ".join(tried))


def editor_is_open() -> bool:
    """True only if a Unity process actually holds Temp/UnityLockfile.

    A lockfile can be left behind after a crash or a failed batch run, so its mere
    existence is not enough: try to take the lock ourselves.
    """
    if not LOCKFILE.exists():
        return False
    try:
        with open(LOCKFILE, "a") as f:  # Windows: fails while Unity holds it open
            if SYSTEM == "Windows":
                return False
            import fcntl
            try:
                fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError:
                return True
            fcntl.flock(f, fcntl.LOCK_UN)
            return False
    except PermissionError:
        return True


def run_unity(extra: list[str], log_name: str, force: bool, graphics: bool = False) -> tuple[int, Path]:
    if editor_is_open() and not force:
        raise RuntimeError("Unity has this project open. Close the editor first "
                           "(or pass --force if the lockfile is stale after a crash).")
    unity = find_unity()
    LOGS.mkdir(parents=True, exist_ok=True)
    log = LOGS / log_name
    cmd = [str(unity), "-batchmode", "-quit", *([] if graphics else ["-nographics"]),
           "-projectPath", str(PROJECT), "-logFile", str(log), *extra]
    print(f"Unity {unity_version()}: {' '.join(extra) or 'compile'}")
    print(f"  log: {log}")
    start = time.monotonic()
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        while proc.poll() is None:
            time.sleep(5)
            print(f"  ... {int(time.monotonic() - start)}s", flush=True)
    except KeyboardInterrupt:
        proc.terminate()
        raise
    print(f"  finished in {int(time.monotonic() - start)}s (exit {proc.returncode})")
    return proc.returncode, log


def summarize_log(log: Path) -> int:
    """Print the useful lines from a Unity log. Returns number of compile errors."""
    try:
        text = log.read_text(encoding="utf-8", errors="replace")
    except OSError:
        print("  (no log written)")
        return 0
    errors = sorted(set(re.findall(r"^.*error CS\d+.*$", text, re.M)))
    warnings = sorted(set(re.findall(r"^.*warning CS\d+.*$", text, re.M)))
    for line in errors[:50]:
        print(f"  {line}")
    if len(errors) > 50:
        print(f"  ... and {len(errors) - 50} more")
    shader_errors = sorted(set(re.findall(r"^Shader error in '.*$", text, re.M)))
    for line in shader_errors[:20]:
        print(f"  {line}")
    errors += shader_errors
    for line in re.findall(r"^\[(?:WebBuild|ShaderPreview|SceneBuilder|FontSetup)\].*$", text, re.M):
        print(f"  {line}")
    for marker in ("Scripts have compiler errors.", "Aborting batchmode due to failure:"):
        if marker in text:
            print(f"  {marker}")
    # Package Manager failures (bad manifest entries) aren't C# errors; show them too.
    pkg = re.search(r"^An error occurred while resolving packages:\n((?:[ \t]+.*\n?)+)", text, re.M)
    if pkg:
        print("  Package resolution failed:")
        for line in pkg.group(1).rstrip().splitlines():
            print(f"  {line}")
    exc = re.search(r"^\w+(?:\.\w+)*Exception: .*$", text, re.M)
    if exc:
        print(f"  {exc.group(0)}")
    print(f"  {len(errors)} error(s), {len(warnings)} warning(s)")
    return len(errors)


# --------------------------------------------------------------------------- commands

def cmd_check(args) -> int:
    code, log = run_unity(["-accept-apiupdate"], "ln-check.log", args.force)
    errors = summarize_log(log)
    return 0 if code == 0 and errors == 0 else 1


CHANGELOG = ROOT / "CHANGELOG.md"
VERSION_HEADING = re.compile(r"^##\s+v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\b.*$", re.M)


def changelog_version() -> tuple[str, str]:
    """(version, notes) from the top '## x.y.z' entry of CHANGELOG.md."""
    text = CHANGELOG.read_text(encoding="utf-8")
    m = VERSION_HEADING.search(text)
    if not m:
        raise RuntimeError(f"no '## x.y.z' heading in {CHANGELOG.name}")
    nxt = VERSION_HEADING.search(text, m.end())
    notes = text[m.end():nxt.start() if nxt else len(text)].strip()
    return m.group(1), notes


def cmd_version(args) -> int:
    version, notes = changelog_version()
    print(notes if args.notes else version)
    return 0


def next_version(version: str, part: str) -> str:
    """0.3.0 + patch -> 0.3.1, + minor -> 0.4.0, + major -> 1.0.0 (a pre-release suffix is dropped)."""
    major, minor, patch = (int(n) for n in version.split("-")[0].split("."))
    if part == "major":
        return f"{major + 1}.0.0"
    if part == "minor":
        return f"{major}.{minor + 1}.0"
    return f"{major}.{minor}.{patch + 1}"


def merged_since(tag: str) -> list[str]:
    """PR titles merged since `tag` (merge commits' first body line), oldest first; else plain commit subjects."""
    def log(*extra: str) -> str:
        rng = f"{tag}..HEAD" if tag else "HEAD"
        return subprocess.run(["git", "log", rng, "--reverse", *extra], cwd=ROOT, capture_output=True,
                              text=True, check=True).stdout
    titles = [t.strip() for t in log("--merges", "--grep=^Merge pull request", "--format=%b%x00").split("\0")]
    titles = [t.splitlines()[0] for t in titles if t]
    if not titles:
        titles = [t for t in log("--no-merges", "--format=%s").splitlines() if t]
    return titles


def cmd_bump(args) -> int:
    version, _ = changelog_version()
    new = next_version(version, args.part)
    tag = f"v{version}"
    if subprocess.run(["git", "rev-parse", "-q", "--verify", f"refs/tags/{tag}"], cwd=ROOT,
                      capture_output=True).returncode != 0:
        print(f"{tag} isn't tagged yet: release it before starting the next version", file=sys.stderr)
        return 1
    if args.notes_file:
        notes = Path(args.notes_file).read_text(encoding="utf-8").strip()
    else:
        titles = merged_since(tag)
        if not titles:
            print(f"nothing merged since {tag}", file=sys.stderr)
            return 1
        notes = "\n".join(f"- {t}" for t in titles)
    text = CHANGELOG.read_text(encoding="utf-8")
    m = VERSION_HEADING.search(text)
    entry = f"## {new} ({time.strftime('%Y-%m-%d')})\n\n{notes}\n\n"
    CHANGELOG.write_text(text[:m.start()] + entry + text[m.start():], encoding="utf-8")
    print(new)
    return 0


GFX_CHOICES = ("both", "webgl2", "webgpu")


def web_build_dir(gfx: str) -> Path:
    """Mirror of WebBuild.OutputDirFor: Both (default) -> Builds/WebGL, others -> Builds/WebGL-<Variant>."""
    suffix = {"both": "", "webgl2": "-WebGL2", "webgpu": "-WebGPU"}[gfx]
    return WEB_BUILD.with_name("WebGL" + suffix)


def build_overlay(out: Path, watch: bool = False) -> int:
    """web/ (React overlays) -> <build>/overlay/overlay.js. Installs node_modules on first use."""
    npm = shutil.which("npm")
    if npm is None:
        print("  npm not found: skipping the overlay (the game runs without it)")
        return 0
    if not (WEB_DIR / "node_modules").is_dir():
        code = subprocess.run([npm, "ci"], cwd=WEB_DIR).returncode
        if code != 0:
            return code
    env = {**os.environ, "LN_OVERLAY_OUT": str(out)}
    script = ["run", "watch"] if watch else ["run", "build"]
    print(f"  overlay -> {out}" + (" (watching; Ctrl+C to stop)" if watch else ""))
    try:
        return subprocess.run([npm, *script], cwd=WEB_DIR, env=env).returncode
    except KeyboardInterrupt:
        return 0


def cmd_overlay(args) -> int:
    out = web_build_dir(args.gfx)
    if not (out / "index.html").is_file():
        return fail(f"no Web build in {out}; run `ln.py build-web` first")
    return build_overlay(out / "overlay", watch=args.watch)


def cmd_preview(args) -> int:
    method, png = {"ship": ("RenderShip", "preview-ship.png"), "arms": ("RenderArms", "preview-arms.png"),
                   "station": ("RenderStation", "preview-station.png")}.get(
        args.what, ("RenderSpace", "preview-space.png"))
    code, log = run_unity(["-executeMethod", f"LuckyNumbers.Editor.ShaderPreview.{method}"],
                          "ln-preview.log", args.force, graphics=True)
    errors = summarize_log(log)
    out = PROJECT / "Logs" / png
    if code != 0 or errors or not out.is_file():
        return fail("preview failed, see log above")
    print(f"Preview: {out}")
    return 0


def cmd_scenes(args) -> int:
    code, log = run_unity(["-executeMethod", "LuckyNumbers.Editor.SceneBuilder.BuildAll"],
                          "ln-scenes.log", args.force)
    errors = summarize_log(log)
    return 0 if code == 0 and not errors else fail("scene build failed, see log above")


def cmd_webshot(args) -> int:
    """Screenshot the served Web build once the console shows --wait (needs Node 22+ and Chrome)."""
    node = shutil.which("node")
    if not node:
        return fail("node not found (Node 22+ needed for tools/webshot.mjs)")
    out = Path(args.out).resolve()
    cmd = [node, str(ROOT / "tools" / "webshot.mjs"), args.url, str(out), "--timeout", str(args.timeout)]
    if args.wait:
        cmd += ["--wait", args.wait]
    if args.steps:
        cmd += ["--steps", args.steps]
    cmd += ["--size", args.size, "--dpr", str(args.dpr)]
    return subprocess.call(cmd)


WEAPONS_INDEX = PROJECT / "Assets" / "_Project" / "Art" / "Models" / "Weapons" / "weapons.json"


def compact_number_arrays(text: str) -> str:
    """Indented JSON, but arrays of plain numbers on one line (a grip's 60 rotation floats, points)."""
    return re.sub(r"\[\s*(-?[\d.e+-]+(?:,\s*-?[\d.e+-]+)*)\s*\]",
                  lambda m: "[" + ", ".join(x.strip() for x in m.group(1).split(",")) + "]", text)


def cmd_bake_grasps(args) -> int:
    """Bake every weapon's finger grips into weapons.json: the served Web build's range (?ln-range&ln-bake) picks
    each one up, solves both hands' grips live with the game's own code, and logs them; this writes them in.
    Build and serve first (build-web, serve); afterwards rebuild the scenes so the weapons carry them."""
    node = shutil.which("node")
    if not node:
        return fail("node not found (Node 22+ needed for tools/webshot.mjs)")
    shot = PROJECT / "Logs" / "bake-grasps.png"
    cmd = [node, str(ROOT / "tools" / "webshot.mjs"), args.url, str(shot), "--timeout", str(args.timeout),
           "--wait", "[Range] ready", "--full", "--steps", json.dumps([{"wait": "[Bake] done"}, {"shot": str(shot)}])]
    print("baking grips in the range (headless)...")
    out = subprocess.run(cmd, capture_output=True, text=True)
    grips, problems = {}, []
    for line in out.stdout.splitlines():
        if "[Bake] {" in line:
            entry = json.loads(line[line.index("[Bake] ") + len("[Bake] "):])
            # Unity writes floats as doubles (0.01 -> 0.009999999776...): 4 decimals is under a tenth of a degree.
            r4 = lambda v: [r4(x) for x in v] if isinstance(v, list) else round(v, 4) if isinstance(v, float) else v
            grips[entry["item"]] = {side: {k: r4(v) for k, v in entry[side].items()} for side in ("left", "right")
                                    if entry.get(side) and entry[side].get("rotations")}
        elif "[Bake]" in line and "done" not in line:
            problems.append(line.strip())
        elif "[Bake] done" in line:
            print("  " + line.split("[Bake] ", 1)[1])
    if not grips:
        print(out.stdout[-2000:])
        return fail("no grips came back: is the latest build served (build-web, serve) at " + args.url + "?")
    index = json.loads(WEAPONS_INDEX.read_text())
    for item in index["items"]:
        if item["name"] in grips:
            item["grasp"] = grips[item["name"]]
            g = item["grasp"]
            print(f"  {item['name']}: " + ", ".join(f"{side} {v['touching']}/{v['fingers']} fingers on, palm off {v['push'] * 100:.1f} cm"
                                                   for side, v in g.items()))
    WEAPONS_INDEX.write_text(compact_number_arrays(json.dumps(index, indent=2)) + "\n")
    for p in problems:
        print("  " + p)
    missing = [i["name"] for i in index["items"] if i["kind"] != "mod" and i["name"] not in grips]
    if missing:
        print("  not baked: " + ", ".join(missing))
    print(f"wrote {WEAPONS_INDEX.relative_to(ROOT)}; next: python3 tools/ln.py scenes (the weapons carry the grips)")
    return 0 if not missing else 1


def cmd_build_web(args) -> int:
    version = args.version or changelog_version()[0]
    code, log = run_unity(["-buildTarget", "WebGL",
                           "-executeMethod", "LuckyNumbers.Editor.WebBuild.Build",
                           "-lnGfx", args.gfx, "-lnVersion", version],
                          f"ln-build-web-{args.gfx}.log", args.force)
    errors = summarize_log(log)
    out = web_build_dir(args.gfx)
    if code != 0 or errors or not (out / "index.html").is_file():
        return fail("Web build failed, see log above.")
    if build_overlay(out / "overlay") != 0:
        return fail("overlay build failed (web/), see above.")
    size = sum(f.stat().st_size for f in out.rglob("*") if f.is_file()) / (1024 * 1024)
    print(f"Web build OK ({args.gfx}): {out} ({size:.1f} MB)")
    print(f"Next: python3 tools/ln.py serve --gfx {args.gfx}")
    return 0


class WebHandler(http.server.SimpleHTTPRequestHandler):
    """Adds the headers Unity Web multithreading needs and serves pre-compressed files."""

    CONTENT_TYPES = {".wasm": "application/wasm", ".js": "application/javascript",
                     ".data": "application/octet-stream", ".json": "application/json"}
    ENCODINGS = {".br": "br", ".gz": "gzip"}

    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def guess_type(self, path):
        base, ext = os.path.splitext(path)
        if ext in self.ENCODINGS:
            path, ext = base, os.path.splitext(base)[1]
        if ext == ".unityweb":  # decompression-fallback builds: loader decompresses in JS
            return "application/octet-stream"
        return self.CONTENT_TYPES.get(ext) or super().guess_type(path)

    def send_head(self):
        path = self.translate_path(self.path)
        ext = os.path.splitext(path)[1]
        if ext in self.ENCODINGS and os.path.isfile(path):
            f = open(path, "rb")
            self.send_response(200)
            self.send_header("Content-Type", self.guess_type(path))
            self.send_header("Content-Encoding", self.ENCODINGS[ext])
            self.send_header("Content-Length", str(os.fstat(f.fileno()).st_size))
            self.end_headers()
            return f
        return super().send_head()

    def log_message(self, fmt, *a):  # quieter: only errors
        if not (len(a) > 1 and str(a[1]).startswith(("2", "3"))):
            super().log_message(fmt, *a)


def cmd_serve(args) -> int:
    build = Path(args.dir).resolve() if args.dir else web_build_dir(args.gfx)
    if not (build / "index.html").is_file():
        return fail(f"no index.html in {build}. Run: python3 tools/ln.py build-web")
    handler = lambda *a, **kw: WebHandler(*a, directory=str(build), **kw)  # noqa: E731
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    url = f"http://localhost:{args.port}"
    print(f"Serving {build}\n  {url}  (cross-origin isolated, Ctrl+C to stop)")
    if args.open:
        webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")
    return 0


def cmd_deploy(args) -> int:
    target = args.target or os.environ.get("ITCH_TARGET")
    if not target or "/" not in target:
        return fail("set the itch target: --target user/game  (or ITCH_TARGET=user/game)")
    if not (WEB_BUILD / "index.html").is_file():
        return fail("no Web build. Run: python3 tools/ln.py build-web")
    butler = shutil.which("butler")
    if not butler:
        return fail("butler not found on PATH. Install: https://itch.io/docs/butler/")
    version = args.version or changelog_version()[0]
    cmd = [butler, "push", str(WEB_BUILD), f"{target}:{args.channel}", "--userversion", version]
    print(" ".join(cmd))
    if args.dry_run:
        print("(dry run, nothing pushed)")
        return 0
    return subprocess.call(cmd)


PROTO_DIR = ROOT / "proto"
CONTENT_DIR = ROOT / "content"
CONTENT_HEADER = re.compile(r"^#\s*proto-(file|message|extends):\s*(\S+)\s*$", re.M)


# Pinned official protoc release; downloaded once into .cache/ and verified by SHA-256
# (digests from the GitHub release assets), so every machine and CI uses the same binary.
PROTOC_VERSION = "36.2"
PROTOC_ASSETS = {
    ("Darwin", "arm64"): ("osx-aarch_64", "9cd98a532c5c5e0c4161314de0225de27e4c8a323917b6ea7b1b714d3ae23466"),
    ("Darwin", "x86_64"): ("osx-x86_64", "228cc7add4616cc14ca5e80dee83209d44449a7aee95a914ae748fa374efb078"),
    ("Linux", "x86_64"): ("linux-x86_64", "121f6c7afe1d4d0e3ea6aab9432038599250134cbf4474cb1167d2c7decd4278"),
    ("Linux", "aarch64"): ("linux-aarch_64", "8b8f18bd2b30346efbc698dd5a73dd7c805f3ef8380f6dfc95c768f3f1852f6a"),
    ("Windows", "AMD64"): ("win64", "f0c128dc0d8492eceece83bb459a4c0e316764b929ffbf1aa416357fd644edd3"),
}
CACHE_DIR = ROOT / ".cache"


def ensure_protoc() -> tuple[Path, Path]:
    """Return (protoc binary, its include dir), downloading the pinned release if needed."""
    import hashlib, io, urllib.request, zipfile
    key = (SYSTEM, platform.machine())
    if key not in PROTOC_ASSETS:
        raise RuntimeError(f"no pinned protoc for {key}")
    suffix, sha = PROTOC_ASSETS[key]
    home = CACHE_DIR / "protoc" / f"{PROTOC_VERSION}-{suffix}"
    exe = home / "bin" / ("protoc.exe" if SYSTEM == "Windows" else "protoc")
    if not exe.is_file():
        url = (f"https://github.com/protocolbuffers/protobuf/releases/download/"
               f"v{PROTOC_VERSION}/protoc-{PROTOC_VERSION}-{suffix}.zip")
        print(f"  downloading protoc {PROTOC_VERSION} ({suffix}) ...")
        data = urllib.request.urlopen(url, timeout=60).read()
        got = hashlib.sha256(data).hexdigest()
        if got != sha:
            raise RuntimeError(f"protoc checksum mismatch: expected {sha}, got {got}")
        zipfile.ZipFile(io.BytesIO(data)).extractall(home)
        exe.chmod(0o755)
    return exe, home / "include"


def cmd_proto(args) -> int:
    """Compile all .proto schemas, then encode every content/*.txtpb against its schema."""
    protoc_exe, protoc_include = ensure_protoc()
    protoc = str(protoc_exe)
    includes = [f"-I{PROTO_DIR}", f"-I{protoc_include}"]
    protos = sorted(str(p.relative_to(PROTO_DIR)) for p in PROTO_DIR.rglob("*.proto"))
    r = subprocess.run([protoc, *includes, f"--descriptor_set_out={os.devnull}", *protos],
                       capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stderr.strip())
        return fail("schema compile failed")
    print(f"  schemas OK ({len(protos)} files)")

    bad = 0
    for txt in sorted(CONTENT_DIR.rglob("*.txtpb")):
        header = dict(CONTENT_HEADER.findall(txt.read_text(encoding="utf-8")))
        rel = txt.relative_to(ROOT)
        if "file" not in header or "message" not in header:
            print(f"  FAIL {rel}: needs '# proto-file:' and '# proto-message:' header lines")
            bad += 1
            continue
        with open(txt, "rb") as f:
            r = subprocess.run([protoc, *includes, f"--encode={header['message']}", header["file"]],
                               stdin=f, capture_output=True)
        if r.returncode != 0:
            print(f"  FAIL {rel} ({header['message']}): {r.stderr.decode(errors='replace').strip()}")
            bad += 1
        else:
            print(f"  ok   {rel} -> {header['message']} ({len(r.stdout)} bytes)")
    return 1 if bad else 0


ROSLYN_PROJECT = ROOT / "tools" / "roslyn" / "LuckyNumbers.Generators" / "LuckyNumbers.Generators.csproj"
ROSLYN_TARGET = PROJECT / "Assets" / "_Project" / "Generators" / "LuckyNumbers.Generators.dll"
# Unity treats a DLL as a source generator via this label with every platform disabled.
ROSLYN_META = """fileFormatVersion: 2
guid: 6c1f0b3a9d2e4f7a8b5c3d1e2f4a6b8c
labels:
- RoslynAnalyzer
PluginImporter:
  externalObjects: {}
  serializedVersion: 3
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
    Any:
      enabled: 0
      settings: {}
    Editor:
      enabled: 0
      settings:
        CPU: AnyCPU
        DefaultValueInitialized: true
        OS: AnyOS
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def dotnet() -> Path:
    """The .NET SDK bundled with the project's Unity editor (no separate install needed)."""
    env = shutil.which("dotnet")
    unity = find_unity()
    bundled = {"Darwin": unity.parents[1] / "Resources" / "Scripting" / "DotNetSdk" / "dotnet",
               "Windows": unity.parent / "Data" / "Resources" / "Scripting" / "DotNetSdk" / "dotnet.exe",
               "Linux": unity.parent / "Data" / "Resources" / "Scripting" / "DotNetSdk" / "dotnet"}[SYSTEM]
    if bundled.is_file():
        return bundled
    if env:
        return Path(env)
    raise RuntimeError(f"no .NET SDK found (looked for {bundled}); install one or set PATH")


def cmd_roslyn(args) -> int:
    """Build the Roslyn source generator and copy it into the Unity project."""
    out = CACHE_DIR / "roslyn"
    r = subprocess.run([str(dotnet()), "build", str(ROSLYN_PROJECT), "-c", "Release", "-nologo",
                        "-v", "quiet", "-o", str(out)], capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-4000:])
        return fail("generator build failed")
    built = out / ROSLYN_TARGET.name
    data = built.read_bytes()
    if ROSLYN_TARGET.is_file() and ROSLYN_TARGET.read_bytes() == data:
        print(f"  unchanged {ROSLYN_TARGET.relative_to(ROOT)}")
    else:
        ROSLYN_TARGET.write_bytes(data)
        print(f"  wrote {ROSLYN_TARGET.relative_to(ROOT)} ({len(data) // 1024} KB)")
    meta = Path(str(ROSLYN_TARGET) + ".meta")
    if not meta.is_file() or meta.read_text(encoding="utf-8") != ROSLYN_META:
        meta.write_text(ROSLYN_META, encoding="utf-8", newline="\n")
        print(f"  wrote {meta.relative_to(ROOT)} (RoslynAnalyzer label)")
    return 0


GENERATED_DIR = PROJECT / "Assets" / "_Project" / "Generated"
WEB_DIR = ROOT / "web"
WEB_GENERATED_DIR = WEB_DIR / "src" / "generated"


def generated_path(rel: str) -> Path:
    """protogen output path: "@web/x" -> web/src/generated/x, everything else under Assets/_Project/Generated."""
    return WEB_GENERATED_DIR / rel[len("@web/"):] if rel.startswith("@web/") else GENERATED_DIR / rel


def cmd_gen(args) -> int:
    """proto/ + content/ -> C# under Assets/_Project/Generated (see tools/protogen.py)."""
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import protogen
    protoc, include = ensure_protoc()
    try:
        files = protogen.run(str(protoc), include, PROTO_DIR, CONTENT_DIR, CONTENT_HEADER,
                             shader_root=PROJECT / "Assets" / "_Project")
    except subprocess.CalledProcessError as e:
        print((e.stderr or b"").decode(errors="replace").strip())
        return fail("protoc failed; run `ln.py proto` for details")
    except ValueError as e:
        return fail(str(e))

    wanted = {generated_path(rel): text for rel, text in files.items()}
    # Docs from content: CREDITS.md and README.md's credits block (tools/credits.py).
    import credits
    model, resolved, _ = protogen.load(str(protoc), include, PROTO_DIR, CONTENT_DIR, CONTENT_HEADER)
    if "credits" in resolved:
        cfg = protogen.decode(model, *resolved["credits"])
        wanted[ROOT / "CREDITS.md"] = credits.credits_md(cfg)
        readme = ROOT / "README.md"
        wanted[readme] = credits.readme(readme.read_text(encoding="utf-8") if readme.is_file() else "", cfg)
    existing = {p for d in (GENERATED_DIR, WEB_GENERATED_DIR) if d.exists()
                for p in d.rglob("*") if p.is_file() and not p.name.endswith(".meta")}
    changed = [p for p, t in wanted.items() if not p.is_file() or p.read_text(encoding="utf-8") != t]
    stale = sorted(existing - set(wanted))

    if args.check:
        for p in changed + stale:
            print(f"  out of date: {p.relative_to(ROOT)}")
        if changed or stale:
            return fail("generated code is stale; run `python3 tools/ln.py gen` and commit")
        print(f"  up to date ({len(wanted)} files)")
        return 0

    for p in changed:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(wanted[p], encoding="utf-8", newline="\n")
        print(f"  wrote   {p.relative_to(ROOT)}")
    for p in stale:
        p.unlink()
        Path(str(p) + ".meta").unlink(missing_ok=True)
        print(f"  removed {p.relative_to(ROOT)}")
    print(f"  {len(changed)} written, {len(stale)} removed, {len(wanted) - len(changed)} unchanged")
    return 0


ENV_FILE = ROOT / ".env"
SECRET_KEYS = ("UNITY_EMAIL", "UNITY_PASSWORD", "UNITY_LICENSE", "BUTLER_API_KEY")
VARIABLE_KEYS = ("ITCH_TARGET",)


def read_env(path: Path) -> dict[str, str]:
    """Minimal .env parser: KEY=VALUE, # comments, optional surrounding quotes."""
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, val = line.partition("=")
        val = val.strip()
        if len(val) >= 2 and val[0] == val[-1] and val[0] in "\"'":
            val = val[1:-1]
        values[key.strip()] = val
    return values


def cmd_secrets(args) -> int:
    """Push .env values to GitHub Actions secrets/variables via gh. Never prints values."""
    if not ENV_FILE.is_file():
        return fail(f"no {ENV_FILE.name}. Copy .env.example to .env and fill it in.")
    gh = shutil.which("gh")
    if not gh:
        return fail("GitHub CLI (gh) not found on PATH.")
    env = read_env(ENV_FILE)

    # The license is uploaded from its file, so its contents never sit in .env.
    lic = env.get("UNITY_LICENSE_FILE", "")
    if lic:
        lic_path = Path(lic).expanduser()
        if not lic_path.is_file():
            return fail(f"UNITY_LICENSE_FILE not found: {lic_path}")
        env["UNITY_LICENSE"] = lic_path.read_text(encoding="utf-8")

    plan = [(k, "secret") for k in SECRET_KEYS] + [(k, "variable") for k in VARIABLE_KEYS]
    missing = [k for k, _ in plan if not env.get(k)]
    for key, kind in plan:
        state = "set" if env.get(key) else "EMPTY, skipped"
        print(f"  {kind:8} {key:16} {state}")
    if args.dry_run:
        print("(dry run, nothing sent)")
        return 0 if not missing else 1

    for key, kind in plan:
        value = env.get(key)
        if not value:
            continue
        cmd = [gh, kind, "set", key] + (["--body", value] if kind == "variable" else [])
        # Secrets go over stdin so they never appear in the process list.
        r = subprocess.run(cmd, input=None if kind == "variable" else value,
                           text=True, capture_output=True)
        if r.returncode != 0:
            return fail(f"gh {kind} set {key} failed: {r.stderr.strip()}")
        print(f"  pushed {kind} {key}")
    if missing:
        print(f"not pushed (empty in .env): {', '.join(missing)}")
    return 0


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="ln.py", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)

    s = sub.add_parser("check", help="headless compile, report C# errors")
    s.add_argument("--force", action="store_true", help="ignore a stale Unity lockfile")
    s.set_defaults(func=cmd_check)

    s = sub.add_parser("build-web", help="headless Web build")
    s.add_argument("--force", action="store_true", help="ignore a stale Unity lockfile")
    s.add_argument("--gfx", choices=GFX_CHOICES, default="both",
                   help="graphics backend(s): both = WebGPU + WebGL 2 fallback (default); "
                        "other variants build to Builds/WebGL-<Variant>")
    s.add_argument("--version", help="game version (default: top entry of CHANGELOG.md)")
    s.set_defaults(func=cmd_build_web)

    s = sub.add_parser("overlay", help="build the web overlays (web/) into the Web build; --watch rebuilds on save")
    s.add_argument("--watch", action="store_true", help="rebuild on every change (refresh the page to see it)")
    s.add_argument("--gfx", choices=GFX_CHOICES, default="both", help="which variant's build folder to write into")
    s.set_defaults(func=cmd_overlay)

    s = sub.add_parser("scenes", help="rebuild game scenes from code (Editor/SceneBuilder.cs)")
    s.add_argument("--force", action="store_true", help="ignore a stale Unity lockfile")
    s.set_defaults(func=cmd_scenes)

    s = sub.add_parser("webshot", help="screenshot the served Web build after a console marker (Node + Chrome)")
    s.add_argument("--url", default="http://localhost:8080/")
    s.add_argument("--out", default=str(PROJECT / "Logs" / "webshot.png"))
    s.add_argument("--wait", default="[Title] ready", help="console text to wait for ('' = don't wait)")
    s.add_argument("--timeout", type=int, default=90)
    s.add_argument("--steps", help='JSON steps, e.g. [{"drag":[.3,.4,.7,.8]},{"shot":"a.png"},{"click":[.4,.83]}]')
    s.add_argument("--size", default="1400x900", help="browser window, WxH (e.g. 1440x800 for a MacBook Air tab)")
    s.add_argument("--dpr", type=float, default=1.0, help="device pixel ratio (2 = retina)")
    s.set_defaults(func=cmd_webshot)

    s = sub.add_parser("bake-grasps", help="bake every weapon's finger grips into weapons.json (served build's range)")
    s.add_argument("--url", default="http://localhost:8080/?ln-range&ln-bake")
    s.add_argument("--timeout", type=int, default=300)
    s.set_defaults(func=cmd_bake_grasps)

    s = sub.add_parser("preview", help="render shaders to LuckyNumbers/Logs/preview-<what>.png")
    s.add_argument("what", nargs="?", choices=["space", "ship", "arms", "station"], default="space")
    s.add_argument("--force", action="store_true", help="ignore a stale Unity lockfile")
    s.set_defaults(func=cmd_preview)

    s = sub.add_parser("serve", aliases=["server"], help="serve the Web build with COOP/COEP headers")
    s.add_argument("--port", type=int, default=8080)
    s.add_argument("--dir", help=f"build folder (default {WEB_BUILD.relative_to(ROOT)})")
    s.add_argument("--gfx", choices=GFX_CHOICES, default="both", help="which variant's build folder to serve")
    s.add_argument("--open", action="store_true", help="open the browser")
    s.set_defaults(func=cmd_serve)

    s = sub.add_parser("deploy", help="butler push the Web build to itch.io")
    s.add_argument("--target", help="itch user/game (or ITCH_TARGET env)")
    s.add_argument("--channel", default="html5")
    s.add_argument("--version", help="itch user version (default: top entry of CHANGELOG.md)")
    s.add_argument("--dry-run", action="store_true")
    s.set_defaults(func=cmd_deploy)

    s = sub.add_parser("proto", help="validate proto schemas and content/*.txtpb (pinned protoc)")
    s.set_defaults(func=cmd_proto)

    s = sub.add_parser("gen", help="generate C# from proto/ + content/ into Assets/_Project/Generated")
    s.add_argument("--check", action="store_true", help="only verify the committed output is up to date")
    s.set_defaults(func=cmd_gen)

    s = sub.add_parser("roslyn", help="build the Roslyn source generator into Assets/_Project/Generators")
    s.set_defaults(func=cmd_roslyn)

    s = sub.add_parser("version", help="print the game version from CHANGELOG.md")
    s.add_argument("--notes", action="store_true", help="print that entry's release notes instead")
    s.set_defaults(func=cmd_version)

    s = sub.add_parser("bump", help="add the next version's entry to CHANGELOG.md (notes: PRs merged since the tag)")
    s.add_argument("part", nargs="?", default="patch", choices=("patch", "minor", "major"))
    s.add_argument("--notes-file", help="use this file's text as the notes instead")
    s.set_defaults(func=cmd_bump)

    s = sub.add_parser("secrets", help="push .env values to GitHub Actions secrets (never printed)")
    s.add_argument("--dry-run", action="store_true", help="only show which keys are set / empty")
    s.set_defaults(func=cmd_secrets)

    s = sub.add_parser("doctor", help="show what the tools detected")
    s.set_defaults(func=cmd_doctor)

    args = p.parse_args(argv)
    try:
        return args.func(args)
    except RuntimeError as e:
        return fail(str(e))


def cmd_doctor(args) -> int:
    print(f"repo:     {ROOT}")
    print(f"project:  {PROJECT}")
    print(f"os:       {SYSTEM}  python {platform.python_version()}")
    try:
        print(f"unity:    {unity_version()} -> {find_unity()}")
    except RuntimeError as e:
        print(f"unity:    NOT FOUND ({e})")
    print(f"editor:   {'OPEN (lockfile present)' if editor_is_open() else 'closed'}")
    print(f"web build:{' present' if (WEB_BUILD / 'index.html').is_file() else ' none'} ({WEB_BUILD})")
    print(f"butler:   {shutil.which('butler') or 'not installed'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
