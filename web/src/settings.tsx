// Settings (Unity: Core/SettingsPresenter, GameSettings state): the one Settings panel, on the title and in game.
// The title's own Settings button sets GameSettings.open; in game the gear (top right, in the template's toolbar
// beside Fullscreen) or Esc opens it. Every change sends the whole set (SettingsChanged); Unity clamps, applies and
// saves it (debounced), and mirrors it back. Defaults sends SettingsReset; Close / Esc send SettingsToggled false.
//
// Esc rule: while the panel is open, Esc closes it. While closed, Esc opens it only in game (not the title), with
// the mouse free (a locked pointer's Esc is the browser's: it frees the mouse first), nothing typed into, and no
// other panel up (any overlay dialog, the map, the outfitter, the job board, an encounter, the save or ship picker,
// character creation). So the first Esc frees the mouse, the next opens Settings, and Esc in another panel only
// closes that panel.
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from "react";
import { createPortal } from "react-dom";
import { LN, useLNState } from "./ln";
import { usePointerLocked } from "./pointer";
import type { GameSettings, RenderQuality } from "./ln";

const QUALITIES: { q: RenderQuality; label: string; hint: string }[] = [
  { q: "Low", label: "Low", hint: "540p" },
  { q: "Medium", label: "Medium", hint: "720p" },
  { q: "High", label: "High", hint: "1080p" },
];
const LOOK_MIN = 0.25;
const LOOK_MAX = 3;

/** When the pointer was last freed: the Esc that frees it can still reach the page right after (Firefox). */
let unlockedAt = 0;
if (typeof document !== "undefined") {
  document.addEventListener("pointerlockchange", () => {
    if (document.pointerLockElement == null) unlockedAt = performance.now();
  });
}

function otherPanelOpen(): boolean {
  const s = LN.states;
  if (s.MapState?.open || s.Outfitter?.open || s.JobBoard?.open || s.EncounterState?.open) return true;
  if (s.SaveSlots?.open || s.ShipPicker?.open || s.CharacterSheet?.setup) return true;
  return document.querySelector('#overlay [role="dialog"]:not(.settings)') != null;
}

function typing(): boolean {
  const el = document.activeElement;
  if (!(el instanceof HTMLElement)) return false;
  if (el.isContentEditable || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) return true;
  return el instanceof HTMLInputElement && !["button", "checkbox", "radio", "range"].includes(el.type);
}

const toggle = (open: boolean) => LN.send("SettingsToggled", { open });

/** Esc, installed at load (before React mounts the other panels' key handlers, so it sees whether one was open
 *  before any of them closes it on the same key). Capture phase: before Unity too. */
if (typeof window !== "undefined") {
  window.addEventListener(
    "keydown",
    (e: KeyboardEvent) => {
      if (e.code !== "Escape" || e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
      const st = LN.states.GameSettings;
      if (!st) return;   // a Unity build without settings
      if (st.open) toggle(false);
      else {
        const sc = LN.states.FlowState?.scene;
        if (sc == null || sc === "Title") return;
        if (document.pointerLockElement != null || performance.now() - unlockedAt < 250) return;
        if (typing() || otherPanelOpen()) return;
        toggle(true);
      }
      e.preventDefault();
      e.stopImmediatePropagation();
    },
    { capture: true },
  );
}

export function SettingsGate() {
  const gs = useLNState("GameSettings");
  const scene = useLNState("FlowState")?.scene;
  const open = gs?.open ?? false;
  const inGame = scene != null && scene !== "Title";

  // The panel needs the mouse.
  useEffect(() => {
    if (open && document.pointerLockElement) document.exitPointerLock();
  }, [open]);

  useToolbarWidth();

  if (!gs) return null;
  return (
    <>
      {inGame && !open && <Gear />}
      {open && <SettingsPanel gs={gs} />}
    </>
  );
}

/** The template's top-right toolbar (Fullscreen) grows with the gear: publish its width as --toolbar-w on #overlay,
 *  so the HUD laid out beside it (sheet chips, flight readouts) stays clear. */
function useToolbarWidth() {
  useEffect(() => {
    const bar = document.getElementById("toolbar");
    const host = document.getElementById("overlay");
    if (!bar || !host || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(() => host.style.setProperty("--toolbar-w", `${Math.round(bar.offsetWidth)}px`));
    ro.observe(bar);
    return () => {
      ro.disconnect();
      host.style.removeProperty("--toolbar-w");
    };
  }, []);
}

/** In game: a gear in the toolbar, beside Fullscreen. */
function Gear() {
  const locked = usePointerLocked();
  const bar = document.getElementById("toolbar");
  const button = (
    <button
      type="button"
      className={`icon-button settings-gear${locked ? " locked" : ""}`}
      title="Settings (Esc)"
      aria-label="Settings"
      onClick={() => toggle(true)}
    >
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinejoin="round" aria-hidden="true">
        <path d="M10.3 2.5h3.4l.5 2.6 1.8.8 2.2-1.5 2.4 2.4-1.5 2.2.8 1.8 2.6.5v3.4l-2.6.5-.8 1.8 1.5 2.2-2.4 2.4-2.2-1.5-1.8.8-.5 2.6h-3.4l-.5-2.6-1.8-.8-2.2 1.5-2.4-2.4 1.5-2.2-.8-1.8-2.6-.5v-3.4l2.6-.5.8-1.8-1.5-2.2 2.4-2.4 2.2 1.5 1.8-.8z" />
        <circle cx="12" cy="12" r="3.2" />
      </svg>
    </button>
  );
  // Outside the template (dev page without a toolbar): a corner of our own.
  return bar ? createPortal(button, bar) : <div className="settings-gear-host ui">{button}</div>;
}

function SettingsPanel({ gs }: { gs: GameSettings }) {
  // The slider is local while it moves (Unity's echo lags a frame); everything else reads straight from the state.
  const [look, setLook] = useState(gs.lookSensitivity || 1);
  useEffect(() => setLook(gs.lookSensitivity || 1), [gs.lookSensitivity]);
  const quality: RenderQuality = gs.quality === "Unspecified" ? "Medium" : gs.quality;
  const panel = useRef<HTMLDivElement>(null);
  const first = useRef<HTMLInputElement>(null);

  const send = (patch: Partial<Pick<GameSettings, "lookSensitivity" | "invertY" | "quality" | "showFps">>) =>
    LN.send("SettingsChanged", {
      lookSensitivity: look,
      invertY: gs.invertY,
      quality,
      showFps: gs.showFps,
      ...patch,
    });

  // Focus comes in on open (keyboard / gamepad) and goes back where it was on close.
  useLayoutEffect(() => {
    const before = document.activeElement as HTMLElement | null;
    first.current?.focus({ preventScroll: true });
    return () => {
      if (before && before !== document.body && document.contains(before)) before.focus({ preventScroll: true });
      else (document.activeElement as HTMLElement | null)?.blur?.();
    };
  }, []);

  // Tab stays inside the panel.
  const trap = (e: KeyboardEvent) => {
    if (!panel.current) return;
    const items = panel.current.querySelectorAll<HTMLElement>("button:not(:disabled), input:not(:disabled)");
    const focusable = Array.from(items).filter((el) => el.tabIndex >= 0);
    if (focusable.length === 0) return;
    const a = focusable[0];
    const z = focusable[focusable.length - 1];
    const at = document.activeElement;
    if (e.shiftKey && (at === a || at === panel.current)) {
      e.preventDefault();
      z.focus();
    } else if (!e.shiftKey && (at === z || at === panel.current)) {
      e.preventDefault();
      a.focus();
    }
  };

  // Arrows move along the quality buttons (one tab stop, like radios).
  const qualityKeys = (e: KeyboardEvent) => {
    const step = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const i = QUALITIES.findIndex((x) => x.q === quality);
    const next = QUALITIES[Math.min(QUALITIES.length - 1, Math.max(0, i + step))];
    if (next.q !== quality) send({ quality: next.q });
    panel.current?.querySelector<HTMLElement>(`[data-quality="${next.q}"]`)?.focus();
  };
  const onKeyRef = useRef<(e: KeyboardEvent) => void>(() => {});
  onKeyRef.current = (e) => {
    if (e.key === "Tab") trap(e);
    else if (document.activeElement instanceof HTMLElement && document.activeElement.dataset.quality) qualityKeys(e);
  };

  // Unity's Web player preventDefaults every key it sees, which would stop arrows on the slider and Enter/Space on
  // the buttons: while focus is in the panel, keys are handled here, natively, and go no further (Esc is handled at
  // load, before this). Native default actions (slider arrows, Enter/Space on buttons) still happen.
  useEffect(() => {
    const keep = (e: KeyboardEvent) => {
      if (e.code === "Escape" || !panel.current?.contains(document.activeElement)) return;
      if (e.type === "keydown") onKeyRef.current(e);
      e.stopImmediatePropagation();
    };
    const types = ["keydown", "keyup", "keypress"] as const;
    for (const t of types) window.addEventListener(t, keep, { capture: true });
    return () => {
      for (const t of types) window.removeEventListener(t, keep, { capture: true });
    };
  }, []);

  const pct = ((look - LOOK_MIN) / (LOOK_MAX - LOOK_MIN)) * 100;
  return (
    <>
      <div className="settings-scrim ui" aria-hidden="true" onClick={() => toggle(false)} />
      <div ref={panel} className="settings ui" role="dialog" aria-modal="true" aria-labelledby="settings-title" tabIndex={-1}>
        <header className="settings-head">
          <div className="hangar-kicker">System // config</div>
          <h2 id="settings-title">Settings</h2>
        </header>

        <div className="settings-row">
          <label htmlFor="settings-look" className="settings-label">
            Look speed
          </label>
          <output htmlFor="settings-look" className="settings-value">
            {look.toFixed(2)}x
          </output>
          <input
            ref={first}
            id="settings-look"
            className="settings-slider"
            type="range"
            min={LOOK_MIN}
            max={LOOK_MAX}
            step={0.05}
            value={look}
            style={{ "--pct": `${pct}%` } as CSSProperties}
            aria-valuetext={`${look.toFixed(2)} times`}
            onChange={(e) => {
              // React's onChange is the input event: live while dragging.
              const v = Number(e.currentTarget.value);
              setLook(v);
              send({ lookSensitivity: v });
            }}
          />
          <div className="settings-scale" aria-hidden="true">
            <span>0.25x</span>
            <span>1x</span>
            <span>3x</span>
          </div>
        </div>

        <Toggle label="Invert look up/down" hint="Mouse forward looks down" on={gs.invertY} set={(v) => send({ invertY: v })} />

        <div className="settings-row">
          <span id="settings-quality" className="settings-label">
            Render quality
          </span>
          <div className="settings-seg" role="radiogroup" aria-labelledby="settings-quality">
            {QUALITIES.map((x) => (
              <button
                key={x.q}
                type="button"
                role="radio"
                aria-checked={quality === x.q}
                tabIndex={quality === x.q ? 0 : -1}
                data-quality={x.q}
                className={quality === x.q ? "active" : ""}
                onClick={() => send({ quality: x.q })}
              >
                <b>{x.label}</b>
                <small>{x.hint}</small>
              </button>
            ))}
          </div>
          <div className="settings-hint">The 3D view's resolution cap. Lower runs smoother on older phones and laptops; the HUD stays sharp.</div>
        </div>

        <Toggle label="Show frame rate" hint="Readout at the top" on={gs.showFps} set={(v) => send({ showFps: v })} />

        <footer className="settings-foot">
          <span className="settings-keys">
            <kbd>Esc</kbd> close
          </span>
          <button type="button" className="sheet-btn" onClick={() => LN.send("SettingsReset", {})}>
            Defaults
          </button>
          <button type="button" className="sheet-btn primary" onClick={() => toggle(false)}>
            Close
          </button>
        </footer>
      </div>
    </>
  );
}

function Toggle({ label, hint, on, set }: { label: string; hint: string; on: boolean; set: (v: boolean) => void }) {
  return (
    <button type="button" role="switch" aria-checked={on} className={`settings-toggle${on ? " on" : ""}`} onClick={() => set(!on)}>
      <span className="settings-toggle-text">
        <span className="settings-label">{label}</span>
        <small>{hint}</small>
      </span>
      <span className="settings-switch" aria-hidden="true">
        <span />
      </span>
      <span className="settings-state" aria-hidden="true">
        {on ? "On" : "Off"}
      </span>
    </button>
  );
}
