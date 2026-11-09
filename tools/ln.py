#!/usr/bin/env python3
"""LuckyNumbers dev tasks. Standard library only, works on macOS / Windows / Linux.

    python3 tools/ln.py check         headless compile, report C# errors
    python3 tools/ln.py build-web     headless Web build -> LuckyNumbers/Builds/WebGL
    python3 tools/ln.py serve         serve the Web build with COOP/COEP headers
    python3 tools/ln.py deploy        push the Web build to itch.io with butler

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


def run_unity(extra: list[str], log_name: str, force: bool) -> tuple[int, Path]:
    if editor_is_open() and not force:
        raise RuntimeError("Unity has this project open. Close the editor first "
                           "(or pass --force if the lockfile is stale after a crash).")
    unity = find_unity()
    LOGS.mkdir(parents=True, exist_ok=True)
    log = LOGS / log_name
    cmd = [str(unity), "-batchmode", "-quit", "-nographics",
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
    for line in re.findall(r"^\[WebBuild\].*$", text, re.M):
        print(f"  {line}")
    for marker in ("Scripts have compiler errors.", "Aborting batchmode due to failure:"):
        if marker in text:
            print(f"  {marker}")
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


GFX_CHOICES = ("both", "webgl2", "webgpu")


def web_build_dir(gfx: str) -> Path:
    """Mirror of WebBuild.OutputDirFor: Both (default) -> Builds/WebGL, others -> Builds/WebGL-<Variant>."""
    suffix = {"both": "", "webgl2": "-WebGL2", "webgpu": "-WebGPU"}[gfx]
    return WEB_BUILD.with_name("WebGL" + suffix)


def cmd_build_web(args) -> int:
    code, log = run_unity(["-buildTarget", "WebGL",
                           "-executeMethod", "LuckyNumbers.Editor.WebBuild.Build",
                           "-lnGfx", args.gfx],
                          f"ln-build-web-{args.gfx}.log", args.force)
    errors = summarize_log(log)
    out = web_build_dir(args.gfx)
    if code != 0 or errors or not (out / "index.html").is_file():
        return fail("Web build failed, see log above.")
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
    cmd = [butler, "push", str(WEB_BUILD), f"{target}:{args.channel}"]
    if args.version:
        cmd += ["--userversion", args.version]
    print(" ".join(cmd))
    if args.dry_run:
        print("(dry run, nothing pushed)")
        return 0
    return subprocess.call(cmd)


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
    s.set_defaults(func=cmd_build_web)

    s = sub.add_parser("serve", aliases=["server"], help="serve the Web build with COOP/COEP headers")
    s.add_argument("--port", type=int, default=8080)
    s.add_argument("--dir", help=f"build folder (default {WEB_BUILD.relative_to(ROOT)})")
    s.add_argument("--gfx", choices=GFX_CHOICES, default="both", help="which variant's build folder to serve")
    s.add_argument("--open", action="store_true", help="open the browser")
    s.set_defaults(func=cmd_serve)

    s = sub.add_parser("deploy", help="butler push the Web build to itch.io")
    s.add_argument("--target", help="itch user/game (or ITCH_TARGET env)")
    s.add_argument("--channel", default="html5")
    s.add_argument("--version", help="user version, e.g. 0.1.0")
    s.add_argument("--dry-run", action="store_true")
    s.set_defaults(func=cmd_deploy)

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
