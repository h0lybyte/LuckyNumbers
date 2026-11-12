// Touch controls for phones and tablets: two floating sticks (left moves, right looks/steers) and the buttons
// for what you're doing (on foot, or flying). They feed Unity as a virtual gamepad: this writes the sticks and a
// button bitmask into window.LNTouch, and TouchPad.jslib/TouchPad.cs read it every input update (no messages, no
// JSON). Pointer events go straight into that buffer through refs, so moving a thumb never re-renders React.
//
// Shown on a coarse pointer (a touch screen), or anywhere with ?ln-touch (try it with a mouse).
import { useEffect, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { useLNState } from "./ln";

/** Button bits: must match TouchPad.Buttons (C#). */
export const Btn = {
  Jump: 1 << 0,
  Descend: 1 << 1,
  Interact: 1 << 2,
  Fire: 1 << 3,
  Brake: 1 << 4,
  Sprint: 1 << 5,
  Map: 1 << 6,
  Cancel: 1 << 7,
  RollLeft: 1 << 8,
  RollRight: 1 << 9,
  Relic: 1 << 10,
  View: 1 << 11,
} as const;

interface TouchBuffer {
  on: boolean;
  axes: Float32Array; // lx, ly, rx, ry (-1..1, y up)
  buttons: number;
}

declare global {
  interface Window {
    LNTouch?: TouchBuffer;
  }
}

/** A touch screen, or ?ln-touch. Fixed for the visit (a phone doesn't grow a mouse). */
export const touchDevice: boolean =
  new URLSearchParams(location.search).has("ln-touch") ||
  (typeof matchMedia !== "undefined" && matchMedia("(pointer: coarse)").matches);

const buffer: TouchBuffer = { on: false, axes: new Float32Array(4), buttons: 0 };
if (touchDevice) {
  buffer.on = true; // Unity: no pointer lock, the sticks steer (PointerLock.Touch)
  window.LNTouch = buffer;
}

/** Let go of everything (controls hidden: a menu, the map, a card). */
function release() {
  buffer.axes.fill(0);
  buffer.buttons = 0;
}

/** Locks during which the sticks step aside (the game isn't taking movement then). */
const PAUSING = new Set(["Map", "Encounter", "Downed", "Leaving", "Spawn"]);

export function TouchControls() {
  const flow = useLNState("FlowState");
  const control = useLNState("PlayerControl");
  const map = useLNState("MapState");
  const scene = flow?.scene;
  const locks = control?.locks ?? [];
  const inGame = scene === "Planet" || scene === "Station" || scene === "FlightSchool" || scene === "Orbit";
  const paused = locks.some((l) => PAUSING.has(l)) || (map?.open ?? false);
  const show = touchDevice && inGame && !paused;
  const flying = scene === "FlightSchool" || scene === "Orbit" || locks.includes("Vehicle");

  useEffect(() => {
    if (!show) release();
  }, [show]);
  useEffect(() => release, []);
  if (!show) return null;

  return (
    <div className="touch">
      <Stick side="left" axis={0} />
      <Stick side="right" axis={2} />
      <div className="touch-top">
        <Hold bit={Btn.Cancel} label="Menu" />
        {scene === "Planet" && <Hold bit={Btn.Map} label="Map" />}
        {scene === "FlightSchool" && <Hold bit={Btn.View} label="View" />}
        {scene === "FlightSchool" && <Hold bit={Btn.Relic} label="Skin" />}
      </div>
      {flying ? (
        <div className="touch-pad">
          {scene !== "FlightSchool" && scene !== "Orbit" && <Hold bit={Btn.Fire} label="Fire" big />}
          <Hold bit={Btn.Jump} label="Lift" />
          <Hold bit={Btn.Descend} label="Down" />
          <Hold bit={Btn.Brake} label="Brake" />
          <Hold bit={Btn.Sprint} label="Boost" />
          <Hold bit={Btn.RollLeft} label="Roll ◀" />
          <Hold bit={Btn.RollRight} label="Roll ▶" />
          {scene !== "FlightSchool" && <Hold bit={Btn.Interact} label="Use" />}
        </div>
      ) : (
        <div className="touch-pad">
          <Hold bit={Btn.Fire} label="Fire" big />
          <Hold bit={Btn.Jump} label="Jump" />
          <Hold bit={Btn.Interact} label="Use" />
          <Hold bit={Btn.Sprint} label="Run" />
          {scene === "Planet" && <Hold bit={Btn.Relic} label="Relic" />}
        </div>
      )}
    </div>
  );
}

/** Stick travel (CSS px) for full deflection. */
const RADIUS = 56;

/**
 * A floating stick: touch anywhere in its half of the screen and it centres there; drag to push it. `axis` is
 * where its x goes in the buffer (y follows).
 */
function Stick({ side, axis }: { side: "left" | "right"; axis: number }) {
  const zone = useRef<HTMLDivElement>(null);
  const base = useRef<HTMLDivElement>(null);
  const knob = useRef<HTMLDivElement>(null);
  const drag = useRef<{ id: number; x: number; y: number } | null>(null);

  const place = (x: number, y: number, dx: number, dy: number) => {
    const b = base.current;
    const k = knob.current;
    if (!b || !k) return;
    b.style.transform = `translate(${x}px, ${y}px)`;
    k.style.transform = `translate(${dx}px, ${dy}px)`;
  };

  const down = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (drag.current) return;
    const r = zone.current!.getBoundingClientRect();
    drag.current = { id: e.pointerId, x: e.clientX - r.left, y: e.clientY - r.top };
    zone.current!.setPointerCapture(e.pointerId);
    zone.current!.classList.add("held");
    place(drag.current.x, drag.current.y, 0, 0);
    e.preventDefault();
  };

  const move = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || d.id !== e.pointerId) return;
    const r = zone.current!.getBoundingClientRect();
    let dx = e.clientX - r.left - d.x;
    let dy = e.clientY - r.top - d.y;
    const len = Math.hypot(dx, dy);
    if (len > RADIUS) {
      dx *= RADIUS / len;
      dy *= RADIUS / len;
    }
    buffer.axes[axis] = dx / RADIUS;
    buffer.axes[axis + 1] = -dy / RADIUS; // screen down is stick down
    place(d.x, d.y, dx, dy);
  };

  const up = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (drag.current?.id !== e.pointerId) return;
    drag.current = null;
    buffer.axes[axis] = 0;
    buffer.axes[axis + 1] = 0;
    zone.current!.classList.remove("held");
  };

  return (
    <div
      ref={zone}
      className={`ui touch-zone ${side}`}
      onPointerDown={down}
      onPointerMove={move}
      onPointerUp={up}
      onPointerCancel={up}
    >
      <div ref={base} className="touch-base">
        <div ref={knob} className="touch-knob" />
      </div>
    </div>
  );
}

/** A button held while touched (bit set from press to release, even if the finger slides off). */
function Hold({ bit, label, big }: { bit: number; label: ReactNode; big?: boolean }) {
  const el = useRef<HTMLButtonElement>(null);
  const on = (e: ReactPointerEvent<HTMLButtonElement>) => {
    buffer.buttons |= bit;
    el.current!.setPointerCapture(e.pointerId);
    el.current!.classList.add("held");
    e.preventDefault();
  };
  const off = () => {
    buffer.buttons &= ~bit;
    el.current?.classList.remove("held");
  };
  useEffect(() => off, []); // unmounted while held (mode change): let go
  return (
    <button
      ref={el}
      type="button"
      className={`ui touch-btn${big ? " big" : ""}`}
      onPointerDown={on}
      onPointerUp={off}
      onPointerCancel={off}
      onContextMenu={(e) => e.preventDefault()}
    >
      {label}
    </button>
  );
}

// ---- turn sideways ---------------------------------------------------------------------------------------------

const portraitQuery = typeof matchMedia !== "undefined" ? matchMedia("(orientation: portrait)") : null;

/** The page's stage (canvas + overlay): what the template fullscreens. */
const stage = () => document.getElementById("stage") ?? document.documentElement;

/**
 * Held upright on a touch screen: the sticks and the flight HUD need the width, so ask to turn it sideways. Where
 * the browser allows (Android Chrome), one tap goes fullscreen and locks landscape; iPhone Safari has neither API,
 * so there it's just the ask. "Play anyway" dismisses it for the visit (a tablet held upright is fine).
 */
export function RotatePrompt() {
  const [portrait, setPortrait] = useState(portraitQuery?.matches ?? false);
  const [dismissed, setDismissed] = useState(false);
  useEffect(() => {
    if (!portraitQuery) return;
    const on = () => setPortrait(portraitQuery.matches);
    portraitQuery.addEventListener("change", on);
    return () => portraitQuery.removeEventListener("change", on);
  }, []);
  if (!touchDevice || !portrait || dismissed) return null;

  const canFullscreen = !!stage().requestFullscreen;
  const goLandscape = async () => {
    try {
      if (!document.fullscreenElement) await stage().requestFullscreen({ navigationUI: "hide" });
      // Not in every browser's typings, nor every browser (iOS): best effort.
      await (screen.orientation as ScreenOrientation & { lock?: (o: string) => Promise<void> }).lock?.("landscape");
    } catch {
      // refused or unsupported: the ask stays up until the phone turns
    }
  };
  return (
    <div className="rotate ui" role="dialog" aria-label="Turn your phone sideways">
      <div className="rotate-phone" aria-hidden="true" />
      <b>Turn your phone sideways</b>
      <span>The sticks and the flight instruments need the width.</span>
      {canFullscreen && (
        <button type="button" className="touch-btn rotate-go" onClick={goLandscape}>
          Play fullscreen
        </button>
      )}
      <button type="button" className="rotate-skip" onClick={() => setDismissed(true)}>
        Play anyway
      </button>
    </div>
  );
}
