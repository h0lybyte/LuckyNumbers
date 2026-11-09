// Screenshot a running Web build after the game reaches a state, via the Chrome DevTools Protocol.
// No dependencies (Node 22+ global WebSocket). Used by: python3 tools/ln.py webshot
//
//   node tools/webshot.mjs <url> <out.png> [--wait "console text"] [--timeout 90] [--chrome path]
//
// Prints the page's console lines, waits for --wait to appear in the console, then captures.
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [url, out, ...rest] = process.argv.slice(2);
const opt = (name, def) => { const i = rest.indexOf(`--${name}`); return i >= 0 ? rest[i + 1] : def; };
const waitFor = opt("wait", null);
const timeoutMs = Number(opt("timeout", "90")) * 1000;
const chrome = opt("chrome", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
if (!url || !out) { console.error("usage: webshot.mjs <url> <out.png> [--wait text] [--timeout s]"); process.exit(2); }

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "webshot-"));
const proc = spawn(chrome, ["--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--no-first-run", "--enable-unsafe-swiftshader", "--enable-unsafe-webgpu", "--window-size=1280,720",
  "--hide-scrollbars", "about:blank"], { stdio: "ignore" });
const cleanup = (code) => { proc.kill(); try { rmSync(profile, { recursive: true, force: true }); } catch {} process.exit(code); };
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
let seen = false;
ws.addEventListener("message", (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === "Runtime.consoleAPICalled") {
    const text = msg.params.args.map((a) => a.value ?? a.description ?? "").join(" ");
    if (!/memorysetup|Content-Encoding/.test(text)) console.log(`[console.${msg.params.type}] ${text}`.slice(0, 400));
    if (waitFor && text.includes(waitFor)) seen = true;
  }
  if (msg.method === "Runtime.exceptionThrown") console.log(`[exception] ${msg.params.exceptionDetails.text}`);
});
const send = (method, params = {}) => new Promise((r) => { const id = nextId++; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });

await send("Runtime.enable");
await send("Page.enable");
await send("Page.navigate", { url });

const start = Date.now();
while (waitFor && !seen && Date.now() - start < timeoutMs) await sleep(250);
if (waitFor && !seen) { console.error(`timed out waiting for console text: ${waitFor}`); cleanup(1); }
await sleep(1500); // let a few frames render after the marker

const shot = await send("Page.captureScreenshot", { format: "png" });
writeFileSync(out, Buffer.from(shot.result.data, "base64"));
console.log(`screenshot: ${out} (${((Date.now() - start) / 1000).toFixed(1)}s)`);
cleanup(0);
