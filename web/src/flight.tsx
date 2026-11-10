// Flying in to land by the wreck: what to do next, not just numbers. The current step lights up (fly to the
// pad, slow down, hover down, touch down), an arrow points at the pad, instruments sit where a cockpit would put
// them, and low down the three touchdown limits show as bars with their safe zone. Unity sends FlightState; when
// this HUD is up it tells Unity (FlightHudReady) so its own text readout steps aside.
import { useEffect } from "react";
import { LN, useLNState } from "./ln";
import type { FlightState } from "./ln";

const STEPS = [
  { title: "Fly to the green pad", how: "Mouse to steer, W / S to set your speed" },
  { title: "Slow down", how: "Tap S until SET reads 0" },
  { title: "Hover down", how: "Hold SPACE: the ship descends and levels itself" },
  { title: "Touch down", how: "Keep holding SPACE until the gauges are green" },
];

export function FlightHud() {
  const f = useLNState("FlightState");
  // Tell Unity this HUD is up, every time a flight starts (each landing attempt is a fresh scene).
  const flying = f?.flying ?? false;
  useEffect(() => {
    if (flying) LN.send("FlightHudReady", { on: true });
  }, [flying]);
  if (!f || (!f.flying && !f.crashed)) return null;
  if (f.crashed) return <CrashCard f={f} />;

  // Follow what you're doing: hovering low = touch down, hovering = hover down, slow = hover down,
  // fast and near the pad = slow down, else fly to the pad. Landing anywhere counts.
  const slow = f.speed <= f.maxDrift * 0.85;
  const step = !f.assisted ? -1 : f.hovering ? (f.altitude < 15 ? 3 : 2) : slow ? 2 : f.padDistance > 45 ? 0 : 1;
  const offPad = step >= 1 && f.padDistance > 45;
  const low = f.altitude < 40;
  const warnings = low ? warn(f) : [];
  return (
    <div className="flight-hud">
      {step >= 0 && (
        <div className="flight-steps">
          <ol>
            {STEPS.map((s, i) => (
              <li key={s.title} className={i === step ? "now" : i < step ? "done" : ""}>
                <span className="n">{i + 1}</span> {s.title}
              </li>
            ))}
          </ol>
          <div className="flight-do">
            <b>{STEPS[step].title}</b>
            <span>{STEPS[step].how}</span>
            {offPad && <span className="flight-note">Any flat spot works; the pad is closest to the wreck</span>}
          </div>
          {warnings.map((w) => (
            <div key={w} className="flight-warn">
              {w}
            </div>
          ))}
        </div>
      )}

      <PadArrow bearing={f.padBearing} distance={f.padDistance} />

      <div className="flight-instruments">
        <Instrument label="ALT" value={f.altitude} unit="m" />
        <Instrument label="SPEED" value={f.speed} unit="m/s" sub={f.assisted ? `SET ${f.setSpeed.toFixed(0)}${f.boost ? " BOOST" : ""}` : undefined} />
        <Instrument label="CLIMB" value={f.climb} unit="m/s" signed />
        {f.hovering && <div className="flight-tag">HOVER</div>}
      </div>

      {low && (
        <div className="flight-gauges">
          <Gauge label="Descent" value={f.descent} limit={f.maxDescent} unit="m/s" fix="hold SPACE" />
          <Gauge label="Drift" value={f.drift} limit={f.maxDrift} unit="m/s" fix="tap S" />
          <Gauge label="Tilt" value={f.tilt} limit={f.maxTilt} unit="°" fix="hold SPACE to level" />
        </div>
      )}

      <div className="flight-keys">
        <div><kbd>Mouse</kbd> steer</div>
        <div><kbd>W</kbd><kbd>S</kbd> speed</div>
        <div><kbd>A</kbd><kbd>D</kbd> roll</div>
        <div><kbd>Shift</kbd> boost</div>
        <div><kbd>Space</kbd> hover down</div>
      </div>
    </div>
  );
}

function warn(f: FlightState): string[] {
  const out: string[] = [];
  if (f.drift > f.maxDrift) out.push("Too fast to land: tap S");
  if (f.descent > f.maxDescent) out.push("Coming down too fast: hold SPACE");
  if (f.tilt > f.maxTilt) out.push("Tilted: hold SPACE to level out");
  return out;
}

/** The pad, relative to your nose: up = ahead, right = to your right. */
function PadArrow({ bearing, distance }: { bearing: number; distance: number }) {
  const below = distance < 6;
  return (
    <div className="flight-pad">
      <svg viewBox="-30 -30 60 60" aria-hidden="true">
        <circle r="26" className="pad-ring" />
        {below ? <circle r="8" className="pad-below" /> : <path d="M 0 -22 L 9 2 L 0 -4 L -9 2 Z" transform={`rotate(${bearing})`} className="pad-arrow" />}
      </svg>
      <div>
        <b>PAD</b> {below ? "below you" : `${distance.toFixed(0)} m`}
        {!below && Math.abs(bearing) > 10 && <span className="muted"> {bearing > 0 ? "right" : "left"}</span>}
      </div>
    </div>
  );
}

function Instrument({ label, value, unit, sub, signed }: { label: string; value: number; unit: string; sub?: string; signed?: boolean }) {
  const text = signed ? `${value >= 0 ? "+" : ""}${value.toFixed(1)}` : value.toFixed(0);
  return (
    <div className="flight-inst">
      <span className="label">{label}</span>
      <span className="value">{text}<small> {unit}</small></span>
      {sub && <span className="sub">{sub}</span>}
    </div>
  );
}

/** A touchdown limit: the bar fills with the value; green under 70% of the limit, amber to it, red past it. */
function Gauge({ label, value, limit, unit, fix }: { label: string; value: number; limit: number; unit: string; fix: string }) {
  const v = Math.max(0, value);
  const tone = v <= limit * 0.7 ? "ok" : v <= limit ? "close" : "bad";
  const fill = Math.min(1, v / (limit * 1.6));
  return (
    <div className={`gauge ${tone}`}>
      <div className="gauge-head">
        <span>{label}</span>
        <span>
          {v.toFixed(1)} / {limit.toFixed(0)} {unit}
        </span>
      </div>
      <div className="gauge-bar">
        <div className="gauge-safe" style={{ width: `${(1 / 1.6) * 100}%` }} />
        <div className="gauge-fill" style={{ width: `${fill * 100}%` }} />
      </div>
      {tone === "bad" && <div className="gauge-fix">{fix}</div>}
    </div>
  );
}

const REASONS: Record<string, string> = {
  descent: "You came down too fast. Hold SPACE all the way to the ground.",
  drift: "You were still moving too fast. Tap S to slow down before you hover down.",
  tilt: "The ship was tilted. Holding SPACE levels it for you.",
  hit: "You flew into something. Keep clear of the wreck and the rocks.",
};

function CrashCard({ f }: { f: FlightState }) {
  const why = f.crashReason.split(",").filter(Boolean);
  return (
    <div className="flight-crash">
      <b>Too hard. Trying again…</b>
      <ul>
        {(why.length ? why : ["descent"]).map((r) => (
          <li key={r}>{REASONS[r] ?? r}</li>
        ))}
      </ul>
    </div>
  );
}
