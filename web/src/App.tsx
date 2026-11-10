import { useEffect, useState, type FormEvent } from "react";
import { createPortal } from "react-dom";
import { LN, useLNEvent, useLNReady, useLNState } from "./ln";
import { installFreeMouseKey, usePointerLocked } from "./pointer";
import { StationHud, StationTools } from "./station";
import { RelicHud } from "./relics";
import { FlightHud } from "./flight";
import { CharacterSheetGate } from "./sheet";
import { HangarGate } from "./hangar";
import type { GameScene, InboundEvent, WebEvents } from "./ln";

export function App({ dock }: { dock: HTMLElement | null }) {
  const [inspector, setInspector] = useState(() => new URLSearchParams(location.search).has("ln-debug"));

  // ` toggles the bridge inspector (capture phase: runs before Unity's keyboard handling).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.code === "Backquote") setInspector((v) => !v);
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, []);

  // The dock is a grid column: showing it narrows the stage and Unity resizes to fit.
  useEffect(() => {
    if (dock) dock.hidden = !inspector;
  }, [dock, inspector]);

  return (
    <>
      <Hud />
      <StationHudGate />
      <CharacterSheetGate />
      <HangarGate />
      <Toasts />
      {inspector && dock && createPortal(<Inspector />, dock)}
    </>
  );
}

function StationHudGate() {
  const flow = useLNState("FlowState");
  if (flow?.scene === "Station") return <StationHud />;
  if (flow?.scene === "Planet")
    return (
      <>
        <FlightHud />
        <RelicHud />
      </>
    );
  return null;
}

// ---- HUD ------------------------------------------------------------------------------------------

function Hud() {
  const flow = useLNState("FlowState");
  const health = useLNState("PlayerHealth");
  const sheet = useLNState("CharacterSheet");
  const mapOpen = useLNState("MapState")?.open ?? false;
  const locked = usePointerLocked();
  useEffect(installFreeMouseKey, []);
  if (flow?.scene === "Station") return <PointerHint locked={locked} />;
  if (flow?.scene !== "Planet") return null;

  return (
    <>
      {!mapOpen && <PointerHint locked={locked} />}
      {/* While the pointer is locked, HTML can't be clicked: dim the buttons so they read as unavailable. */}
      <div className={`hud${locked ? " locked" : ""}`}>
        {health && health.max > 0 && (
          <div className="vitals">
            {sheet && sheet.staminaMax > 0 && (
              <div className="hud-stamina" title={`Stamina ${sheet.stamina} / ${sheet.staminaMax}`}>
                <div className="hud-stamina-fill" style={{ width: `${(100 * sheet.stamina) / sheet.staminaMax}%` }} />
              </div>
            )}
            <div className="health" title={`HP ${health.current} / ${health.max}`}>
              <div className="health-fill" style={{ width: `${(100 * health.current) / health.max}%` }} />
            </div>
          </div>
        )}
        <button
          type="button"
          className="ui chip"
          onClick={() => LN.send("InputActionPerformed", { action: "Map" })}
          title={mapOpen ? "Close the map (M)" : "Open the map (M)"}
        >
          {mapOpen ? "Close map" : "Map"} <kbd>M</kbd>
        </button>
      </div>
    </>
  );
}

/** Corner badge: which keys free the mouse (locked), or that a click takes it (free). Always shown on Planet. */
function PointerHint({ locked }: { locked: boolean }) {
  return (
    <div className="pointer-hint" aria-live="polite">
      {locked ? (
        <>
          <kbd>Tab</kbd>
          <span className="sep">/</span>
          <kbd>Esc</kbd> free mouse
        </>
      ) : (
        <>Click to look</>
      )}
    </div>
  );
}

// ---- toasts ---------------------------------------------------------------------------------------

type Toast = { id: number; text: string; tone: "info" | "bad" | "good" };
let nextToast = 1;

const SCENE_TITLES: Partial<Record<GameScene, string>> = { Planet: "Crash site" };

function Toasts() {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const push = (text: string, tone: Toast["tone"] = "info") => {
    const id = nextToast++;
    setToasts((t) => [...t.slice(-3), { id, text, tone }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), 3500);
  };

  useLNEvent("SceneEntered", (e) => SCENE_TITLES[e.scene] && push(SCENE_TITLES[e.scene]!));
  useLNEvent("PlayerDamaged", (e) => push(`-${e.amount}${e.source ? ` (${e.source})` : ""}`, "bad"));
  useLNEvent("PlayerHealed", (e) => push(`+${e.amount}`, "good"));
  useLNEvent("PlayerDied", (e) => push(e.cause ? `You died: ${e.cause}` : "You died", "bad"));

  return (
    <div className="toasts" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className={`toast ${t.tone}`}>
          {t.text}
        </div>
      ))}
    </div>
  );
}

// ---- inspector (dev) ------------------------------------------------------------------------------

/** Any inbound event with a JSON payload, e.g. InputActionPerformed {"action":"Jump"}. */
function SendForm() {
  const [name, setName] = useState<InboundEvent>("InputActionPerformed");
  const [payload, setPayload] = useState('{"action":"Jump"}');
  const [error, setError] = useState("");
  const send = (e: FormEvent) => {
    e.preventDefault();
    try {
      LN.send(name, JSON.parse(payload));
      setError("");
    } catch (err) {
      setError(String(err));
    }
  };
  return (
    <form className="send-form" onSubmit={send}>
      <select value={name} onChange={(e) => setName(e.target.value as InboundEvent)}>
        {INBOUND.map((n) => (
          <option key={n}>{n}</option>
        ))}
      </select>
      <input value={payload} onChange={(e) => setPayload(e.target.value)} spellCheck={false} aria-label="Payload JSON" />
      <button type="submit">Send</button>
      {error && <div className="error">{error}</div>}
    </form>
  );
}

const INBOUND: InboundEvent[] = ["SceneChangeRequested", "InputActionPerformed", "MenuItemSelected"];

type LoggedEvent = { at: number; name: keyof WebEvents; payload: unknown };

function Inspector() {
  const ready = useLNReady();
  const [states, setStates] = useState(() => ({ ...LN.states }));
  const [events, setEvents] = useState<LoggedEvent[]>([]);

  useEffect(() => LN.subscribe("*", () => setStates({ ...LN.states })), []);
  useLNEvent("*", (payload, name) =>
    setEvents((list) => [{ at: performance.now(), name, payload }, ...list].slice(0, 40)),
  );

  return (
    <div className="inspector">
      <header>
        <strong>Bridge</strong>
        <span className={ready ? "ok" : "wait"}>{ready ? "connected" : "waiting for Unity"}</span>
        <span className="hint">` to close</span>
      </header>

      <section>
        <h3>Send</h3>
        <div className="row">
          {(["Title", "Intro", "Planet"] as const).map((scene) => (
            <button key={scene} type="button" onClick={() => LN.send("SceneChangeRequested", { target: scene })}>
              {scene}
            </button>
          ))}
          <button type="button" onClick={() => LN.send("InputActionPerformed", { action: "Map" })}>
            Map
          </button>
        </div>
        <SendForm />
      </section>

      {LN.states.FlowState?.scene === "Station" && (
        <section className="station-tools">
          <h3>Station</h3>
          <StationTools />
        </section>
      )}

      <section>
        <h3>States</h3>
        {Object.entries(states).map(([name, value]) => (
          <details key={name} open>
            <summary>{name}</summary>
            <pre>{JSON.stringify(value, null, 1)}</pre>
          </details>
        ))}
      </section>

      <section>
        <h3>Events</h3>
        <ol className="events">
          {events.map((e, i) => (
            <li key={events.length - i}>
              <code>{e.name}</code> <span className="payload">{JSON.stringify(e.payload)}</span>
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}
