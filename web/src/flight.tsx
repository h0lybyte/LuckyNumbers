// Flying in to land by the wreck: what to do next, not just numbers. The current step lights up (fly to the
// pad, slow down, hover down, touch down), an arrow points at the pad, instruments sit where a cockpit would put
// them, and low down the three touchdown limits show as bars with their safe zone. Unity sends FlightState; when
// this HUD is up it tells Unity (FlightHudReady) so its own text readout steps aside.
import { useEffect } from "react";
import { LN, useLNState } from "./ln";
import type { FlightState } from "./ln";

// Six-axis controls (hover ship): aim, thrust, stop over the pad, let it down.
const HOVER_STEPS = [
  { title: "Fly to the green pad", how: "Aim with the mouse, W to thrust" },
  { title: "Stop over the pad", how: "Let go of W, or tap X to brake" },
  { title: "Descend", how: "Hold C: the ship eases down and levels itself" },
  { title: "Touch down", how: "Keep holding C, hands off the mouse" },
];

// Flying about (boarded after landing): lift off, fly anywhere and land, or climb high and break orbit (E).
const ROAM_STEPS = [
  { title: "Lift off", how: "Hold SPACE to rise off the ground" },
  { title: "Fly anywhere", how: "Land with C wherever it's flat, or climb above orbit height to leave" },
  { title: "Break orbit", how: "Press E to fly back to the station" },
];

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
  const roaming = f.roaming;
  const steps = roaming ? ROAM_STEPS : f.hoverShip ? HOVER_STEPS : STEPS;
  const step = roaming
    ? f.canLeave ? 2 : f.altitude < 20 && f.speed < 3 ? 0 : 1
    : !f.assisted
    ? -1
    : f.hoverShip
      ? f.hovering || f.climb < -0.8   // descending: by the key, or still sinking after it
        ? f.altitude < 5 ? 3 : 2
        : f.padDistance > 18 ? 0 : f.speed > 2.5 ? 1 : 2
      : f.hovering ? (f.altitude < 15 ? 3 : 2) : slow ? 2 : f.padDistance > 45 ? 0 : 1;
  const offPad = !roaming && step >= 2 && f.padDistance > (f.hoverShip ? 18 : 45);
  const low = f.altitude < 40 && (!roaming || f.climb < -0.8);
  const warnings = [...(low ? warn(f) : []), ...fuelWarn(f)];
  const keys = f.hoverShip
    ? [["Mouse", "aim"], ["Click", "lasers"], ["W S", "thrust"], ["A D", "strafe"], ["Q E", "roll"], ["Space C", "up / down"], ["Shift", "boost"], ["X", "brake"]]
    : [["Mouse", "steer"], ["W S", "speed"], ["A D", "roll"], ["Shift", "boost"], ["Space", "hover down"]];
  return (
    <div className="flight-hud">
      {step >= 0 && (
        <div className="flight-steps">
          <ol>
            {steps.map((s, i) => (
              <li key={s.title} className={i === step ? "now" : i < step ? "done" : ""}>
                <span className="n">{i + 1}</span> {s.title}
              </li>
            ))}
          </ol>
          <div className="flight-do">
            <b>{steps[step].title}</b>
            <span>{steps[step].how}</span>
            {offPad && <span className="flight-note">Any flat spot works; the pad is closest to the wreck</span>}
          </div>
          {warnings.map((w) => (
            <div key={w} className="flight-warn">
              {w}
            </div>
          ))}
        </div>
      )}

      <Crosshair shots={f.shots} />
      {f.canLeave && (
        <div className="flight-leave">
          <kbd>E</kbd> Break orbit: back to the station
        </div>
      )}

      {roaming ? (
        <div className={`flight-orbit${f.canLeave ? " ready" : ""}`} title="Above this height, E flies you back to the station">
          <span>ORBIT</span>
          <div className="flight-orbit-bar">
            <div style={{ width: `${Math.min(100, (100 * Math.max(0, f.altitude)) / Math.max(1, f.orbitAltitude))}%` }} />
          </div>
          <b>
            {Math.max(0, f.altitude).toFixed(0)} / {f.orbitAltitude.toFixed(0)} m
          </b>
        </div>
      ) : (
        <PadArrow bearing={f.padBearing} distance={f.padDistance} />
      )}

      <div className="flight-instruments">
        <Instrument label="ALT" value={f.altitude} unit="m" />
        <Instrument label="SPEED" value={f.speed} unit="m/s"
          sub={f.hoverShip ? (f.boost ? "BOOST" : undefined) : f.assisted ? `SET ${f.setSpeed.toFixed(0)}${f.boost ? " BOOST" : ""}` : undefined} />
        <Instrument label="CLIMB" value={f.climb} unit="m/s" signed />
        {f.fuelMax > 0 && <FuelGauge fuel={f.fuel} max={f.fuelMax} low={f.fuelLow} empty={f.fuelEmpty} />}
        {f.hovering && <div className="flight-tag">{f.hoverShip ? "DESCENDING" : "HOVER"}</div>}
      </div>

      {low && (
        <div className="flight-gauges">
          <Gauge label="Descent" value={f.descent} limit={f.maxDescent} unit="m/s" fix="hold SPACE" />
          <Gauge label="Drift" value={f.drift} limit={f.maxDrift} unit="m/s" fix="tap S" />
          <Gauge label="Tilt" value={f.tilt} limit={f.maxTilt} unit="°" fix="hold SPACE to level" />
        </div>
      )}

      <div className="flight-keys">
        {keys.map(([k, what]) => (
          <div key={k}>
            {k.split(" ").map((x) => (
              <kbd key={x}>{x}</kbd>
            ))}{" "}
            {what}
          </div>
        ))}
      </div>
    </div>
  );
}

function warn(f: FlightState): string[] {
  const out: string[] = [];
  if (f.hoverShip) {
    if (f.drift > f.maxDrift) out.push("Still moving: let go of W, or tap X");
    if (f.descent > f.maxDescent) out.push("Coming down too fast: let go and hold C");
    if (f.tilt > f.maxTilt) out.push("Tilted: let go of the mouse and it levels itself");
    return out;
  }
  if (f.drift > f.maxDrift) out.push("Too fast to land: tap S");
  if (f.descent > f.maxDescent) out.push("Coming down too fast: hold SPACE");
  if (f.tilt > f.maxTilt) out.push("Tilted: hold SPACE to level out");
  return out;
}

function fuelWarn(f: FlightState): string[] {
  if (f.fuelEmpty) return ["Out of fuel: engines out, the reserve cells are letting you down. Land, and the solar cells top it up"];
  if (f.fuelLow) return ["Fuel low: land soon (the station fills the tank)"];
  return [];
}

/** The tank: a bar that goes amber when low and red when dry. */
function FuelGauge({ fuel, max, low, empty }: { fuel: number; max: number; low: boolean; empty: boolean }) {
  const share = Math.max(0, Math.min(1, fuel / Math.max(1, max)));
  return (
    <div className={`flight-inst flight-fuel${empty ? " empty" : low ? " low" : ""}`}>
      <span className="label">FUEL</span>
      <span className="value">{empty ? "EMPTY" : `${(share * 100).toFixed(0)}`}{!empty && <small> %</small>}</span>
      <div className="flight-fuel-bar">
        <div style={{ width: `${share * 100}%` }} />
      </div>
    </div>
  );
}

/** Where the guns point (the chase camera looks down the nose); flashes as a shot goes. */
function Crosshair({ shots }: { shots: number }) {
  return (
    <svg key={shots} className={`flight-cross${shots > 0 ? " fired" : ""}`} viewBox="-20 -20 40 40" aria-hidden="true">
      <path d="M -14 0 H -5 M 5 0 H 14 M 0 -14 V -5 M 0 5 V 14" />
      <circle r="1.5" />
    </svg>
  );
}

/** The pad, relative to your nose: up = ahead, right = to your right. */
function PadArrow({ bearing, distance }: { bearing: number; distance: number }) {
  const below = distance < 9;
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

const HOVER_REASONS: Record<string, string> = {
  descent: "You came down too fast. Hold C near the ground and it eases you down.",
  drift: "You were still moving. Let go of W (or tap X) and stop before you descend.",
  tilt: "The ship was tilted. Let go of the mouse near the ground and it levels itself.",
  hit: "You flew into something. Keep clear of the wreck and the rocks.",
};

function CrashCard({ f }: { f: FlightState }) {
  const why = f.crashReason.split(",").filter(Boolean);
  const reasons = f.hoverShip ? HOVER_REASONS : REASONS;
  return (
    <div className="flight-crash">
      <b>Too hard. Trying again…</b>
      <ul>
        {(why.length ? why : ["descent"]).map((r) => (
          <li key={r}>{reasons[r] ?? r}</li>
        ))}
      </ul>
    </div>
  );
}
