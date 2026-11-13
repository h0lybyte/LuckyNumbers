// Drive a running Web build via the Chrome DevTools Protocol: wait for console markers, drag, click,
// screenshot. No dependencies (Node 22+ global WebSocket). Used by: python3 tools/ln.py webshot
//
//   node tools/webshot.mjs <url> <out.png> [--wait "console text"] [--timeout 90] [--steps JSON]
//                          [--size 1400x900] [--dpr 1]   (window size, device pixel ratio: 2 = a retina screen)
//
// --steps: JSON list run after --wait, coordinates are fractions of the game canvas (0..1, top-left):
//   [{"drag":[x1,y1,x2,y2]}, {"scroll":[x,y,notches]}, {"sleep":1}, {"shot":"a.png"}, {"click":[x,y]}, {"wait":"text"},
//    {"fps":5}, {"key":["KeyW",2]}, {"key":["ShiftLeft+KeyW",2]}, {"down":"KeyW"}, {"up":"KeyW"}, {"hold":[x,y,2]},
//    {"press":[x,y]}, {"release":[x,y]}, {"js":"LN.send(...)"}, {"throttle":4}, {"mem":1}]
//   (fps: measure 5 s; key: hold for 2 s; down / up: press / release and go on; hold: the left button down for 2 s;
//    focus: the page acts focused (headless pointer lock grants); press / release: the left button down / up and go on, so shots run while firing; sleep is in seconds;
//    js: run in the page, prints the result; throttle: Chrome's CPU slowdown factor (1 = off), for low-end runs;
//    mem: the wasm memory (all of Unity's heap) and the page's JS heap)
// Prints the page's console lines (cut at 400 characters; --full keeps them whole). Without --steps it takes one
// screenshot to <out.png>.
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [url, out, ...rest] = process.argv.slice(2);
const opt = (name, def) => { const i = rest.indexOf(`--${name}`); return i >= 0 ? rest[i + 1] : def; };
const waitFor = opt("wait", null);
const full = rest.includes("--full");   // console lines whole (default: cut at 400 characters)
const timeoutMs = Number(opt("timeout", "90")) * 1000;
const [winW, winH] = opt("size", "1400x900").split("x").map(Number);
const dpr = Number(opt("dpr", "1"));
const chrome = opt("chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
const steps = JSON.parse(opt("steps", "null") ?? "null") ?? [{ sleep: 1.5 }, { shot: out }];
if (!url || !out) { console.error("usage: webshot.mjs <url> <out.png> [--wait text] [--steps json]"); process.exit(2); }

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "webshot-"));
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--enable-unsafe-swiftshader", "--enable-unsafe-webgpu", `--window-size=${winW},${winH}`, `--force-device-scale-factor=${dpr}`,
  "--hide-scrollbars", "--enable-precise-memory-info",
  // LN_UNCAPPED=1: no vsync / frame limit, so {"fps"} shows real headroom instead of the display's 60.
  ...(process.env.LN_UNCAPPED ? ["--disable-gpu-vsync", "--disable-frame-rate-limit"] : []),
  "about:blank"], { stdio: "ignore", detached: true }); // its own process group, so its helpers go with it
// Chrome must never outlive this script: kill its whole process group on any way out (done, error, Ctrl-C, ln.py's
// timeout), then drop the temp profile. Only a SIGKILL of node itself can still leave it running.
let cleaned = false;
const killChrome = () => {
  if (cleaned) return;
  cleaned = true;
  try { process.kill(-proc.pid, "SIGKILL"); } catch { try { proc.kill("SIGKILL"); } catch {} }
  try { rmSync(profile, { recursive: true, force: true }); } catch {}
};
const cleanup = (code) => { killChrome(); process.exit(code); };
process.on("exit", killChrome);
for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) process.on(sig, () => cleanup(130));
process.on("uncaughtException", (e) => { console.error(e); cleanup(1); });
process.on("unhandledRejection", (e) => { console.error(e); cleanup(1); });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let target;
for (let i = 0; i < 50 && !target; i++) {
  try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === "page"); }
  catch { await sleep(200); }
}
if (!target) { console.error("chrome did not start"); cleanup(1); }

const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let nextId = 1;
const pending = new Map();
const consoleLines = [];
ws.addEventListener("message", (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === "Runtime.consoleAPICalled") {
    const text = msg.params.args.map((a) => a.value ?? a.description ?? "").join(" ");
    consoleLines.push(text);
    if (!/memorysetup|Content-Encoding/.test(text)) console.log(full ? `[console.${msg.params.type}] ${text}` : `[console.${msg.params.type}] ${text}`.slice(0, 400));
  }
  if (msg.method === "Runtime.exceptionThrown") console.log(`[exception] ${msg.params.exceptionDetails.text}`);
});
const send = (method, params = {}) => new Promise((r) => { const id = nextId++; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
const waitConsole = async (text) => {
  const start = Date.now();
  while (!consoleLines.some((l) => l.includes(text))) {
    if (Date.now() - start > timeoutMs) { console.error(`timed out waiting for console text: ${text}`); cleanup(1); }
    await sleep(200);
  }
};

await send("Runtime.enable");
await send("Page.enable");
await send("Page.navigate", { url });
const t0 = Date.now();
if (waitFor) await waitConsole(waitFor);

// Canvas rect, to turn fractions into page pixels.
const rect = JSON.parse((await send("Runtime.evaluate", {
  expression: "JSON.stringify((document.querySelector('canvas') || document.body).getBoundingClientRect())",
  returnByValue: true,
})).result.result.value);
const px = ([fx, fy]) => ({ x: rect.left + fx * rect.width, y: rect.top + fy * rect.height });
const mouse = (type, p, buttons = 0) => send("Input.dispatchMouseEvent",
  { type, x: p.x, y: p.y, button: "left", buttons, clickCount: type === "mouseMoved" ? 0 : 1 });

for (const step of steps) {
  if (step.sleep) await sleep(step.sleep * 1000);
  if (step.js) { // run page JS, e.g. {"js":"LN.send('DevWarpRequested',{to:'wreck'})"}; prints the result
    const r = await send("Runtime.evaluate", { expression: step.js, awaitPromise: true, returnByValue: true });
    console.log(`js: ${JSON.stringify(r.result?.result?.value ?? r.result?.exceptionDetails?.text ?? null)}`);
  }
  if (step.wait) await waitConsole(step.wait);
  if (step.profile) { // [seconds, "out.cpuprofile"]: the page's CPU profile (open in Chrome DevTools > Performance)
    await send("Profiler.enable");
    await send("Profiler.setSamplingInterval", { interval: 200 });
    await send("Profiler.start");
    await sleep(step.profile[0] * 1000);
    const r = await send("Profiler.stop");
    writeFileSync(step.profile[1], JSON.stringify(r.result.profile));
    console.log(`profile: ${step.profile[1]}`);
  }
  if (step.throttle) { await send("Emulation.setCPUThrottlingRate", { rate: step.throttle }); console.log(`cpu throttle: ${step.throttle}x`); }
  if (step.mem) {
    const r = await send("Runtime.evaluate", { returnByValue: true, expression: `(() => {
      const u = window.unityInstance, mb = (b) => (b / 1048576).toFixed(0);
      const m = u && u.GetMetricsInfo ? u.GetMetricsInfo() : null, js = performance.memory;
      return "wasm heap " + (m ? mb(m.usedWASMHeapSize) + "/" + mb(m.totalWASMHeapSize) + " MB" : "?") +
        (js ? " | js heap " + mb(js.usedJSHeapSize) + "/" + mb(js.totalJSHeapSize) + " MB" : ""); })()` });
    console.log(`mem: ${r.result?.result?.value}`);
  }
  if (step.click) {
    const p = px(step.click);
    await mouse("mouseMoved", p); await sleep(100);
    await mouse("mousePressed", p, 1); await sleep(120);
    await mouse("mouseReleased", p); await sleep(200);
  }
  if (step.hold) { // [x, y, seconds]: the left button held down (e.g. firing)
    const [x, y, seconds] = step.hold, p = px([x, y]);
    await mouse("mouseMoved", p); await sleep(100);
    await mouse("mousePressed", p, 1); await sleep((seconds ?? 1) * 1000);
    await mouse("mouseReleased", p); await sleep(200);
  }
  if (step.focus) await send("Emulation.setFocusEmulationEnabled", { enabled: true }); // page acts focused (pointer lock)
  if (step.press || step.release) { // [x, y]: the left button down / up and go on (shots while firing)
    const p = px(step.press ?? step.release);
    if (step.press) { await mouse("mouseMoved", p); await sleep(50); await mouse("mousePressed", p, 1); }
    else await mouse("mouseReleased", p);
  }
  if (step.scroll) { // [x, y, notches]: positive = scroll down (zoom out)
    const [x, y, notches] = step.scroll, p = px([x, y]);
    for (let i = 0; i < Math.abs(notches); i++) {
      await send("Input.dispatchMouseEvent", { type: "mouseWheel", x: p.x, y: p.y, deltaX: 0, deltaY: Math.sign(notches) * 120 });
      await sleep(60);
    }
  }
  if (step.drag) {
    const [x1, y1, x2, y2] = step.drag, n = 30;
    await mouse("mouseMoved", px([x1, y1])); await sleep(100);
    await mouse("mousePressed", px([x1, y1]), 1);
    for (let i = 1; i <= n; i++) { await mouse("mouseMoved", px([x1 + (x2 - x1) * i / n, y1 + (y2 - y1) * i / n]), 1); await sleep(25); }
    await mouse("mouseReleased", px([x2, y2])); await sleep(200);
  }
  if (step.key || step.down || step.up) { // key: ["KeyW", seconds] or ["ShiftLeft+KeyW", seconds] holds key(s) then
    // lets go (code + key name for the Input System); down / up: "ShiftLeft+KeyW" pressed or released, so other
    // steps (shots) run while it's held.
    const combo = step.key ? step.key[0] : step.down ?? step.up, codes = combo.split("+");
    const vk = { KeyW: 87, KeyA: 65, KeyS: 83, KeyD: 68, KeyE: 69, KeyM: 77, KeyQ: 81, KeyC: 67, KeyX: 88, Space: 32, ShiftLeft: 16 };
    const ev = (type, code) => send("Input.dispatchKeyEvent",
      { type, code, key: code.startsWith("Key") ? code.slice(3).toLowerCase() : code, windowsVirtualKeyCode: vk[code] ?? 0 });
    if (!step.up) for (const c of codes) await ev("keyDown", c);
    if (step.key) await sleep((step.key[1] ?? 0.1) * 1000);
    if (!step.down) for (const c of [...codes].reverse()) await ev("keyUp", c);
  }
  if (step.fps) { // seconds: browser frame rate (Unity renders once per animation frame) + min/max frame time
    const r = await send("Runtime.evaluate", { awaitPromise: true, returnByValue: true, expression: `new Promise((done) => {
      const ts = []; const end = performance.now() + ${step.fps * 1000};
      const tick = (t) => { ts.push(t); if (t < end) requestAnimationFrame(tick); else done(ts); };
      requestAnimationFrame(tick); })` });
    const ts = r.result.result.value, dts = ts.slice(1).map((t, i) => t - ts[i]).sort((a, b) => a - b);
    const p95 = dts[Math.floor(dts.length * 0.95)] ?? 0;
    console.log(`fps: ${((ts.length - 1) / ((ts.at(-1) - ts[0]) / 1000)).toFixed(1)} avg, frame ms median ${dts[dts.length >> 1]?.toFixed(1)} p95 ${p95.toFixed(1)}`);
  }
  if (step.shot) {
    const shot = await send("Page.captureScreenshot", {
      format: "png", clip: { x: rect.left, y: rect.top, width: rect.width, height: rect.height, scale: 1 } });
    writeFileSync(step.shot, Buffer.from(shot.result.data, "base64"));
    console.log(`screenshot: ${step.shot} (${((Date.now() - t0) / 1000).toFixed(1)}s)`);
  }
}
cleanup(0);
