// Station overlays: the room you walk into, the incoming call, what to do next; and the Station tools in the
// dock (lights, PS1 look, a live layout editor that rebuilds the station from a painted plan).
import { useEffect, useMemo, useRef, useState } from "react";
import { LN, useLNState } from "./ln";
import type { StationLightMode } from "./ln";

// ---- HUD (over the game) ------------------------------------------------------------------------

export function StationHud() {
  const state = useLNState("StationState");
  if (!state) return null;
  return (
    <>
      <RoomBanner room={state.room} />
      {(state.call === "Ringing" || state.call === "Active") && <CallPanel phase={state.call} caller={state.caller} />}
      {state.objective && state.call === "Ended" && <div className="objective">{state.objective}</div>}
    </>
  );
}

/** The room's name, fading in as you enter and out a few seconds later. */
function RoomBanner({ room }: { room: string }) {
  const [shown, setShown] = useState("");
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    if (!room) {
      setVisible(false);
      return;
    }
    setShown(room);
    setVisible(true);
    const t = setTimeout(() => setVisible(false), 2800);
    return () => clearTimeout(t);
  }, [room]);
  return <div className={`room-banner${visible ? " visible" : ""}`}>{shown}</div>;
}

function CallPanel({ phase, caller }: { phase: "Ringing" | "Active"; caller: string }) {
  return (
    <div className={`call-panel ui ${phase.toLowerCase()}`}>
      <div className="call-icon" aria-hidden="true">
        <span />
      </div>
      <div className="call-text">
        <div className="call-label">{phase === "Ringing" ? "Incoming call" : "On call"}</div>
        <div className="call-caller">{caller}</div>
      </div>
      {phase === "Ringing" && (
        <button type="button" className="call-answer" onClick={() => LN.send("StationCallAnswered", {})}>
          Answer <kbd>E</kbd>
        </button>
      )}
    </div>
  );
}

// ---- Station tools (in the dock) -----------------------------------------------------------------

const MODES: StationLightMode[] = ["On", "Off", "Emergency"];

export function StationTools() {
  const state = useLNState("StationState");
  if (!state) return <p className="muted">Walk into the station to use these.</p>;
  return (
    <>
      <h3>Lights</h3>
      <div className="row">
        {MODES.map((mode) => (
          <button
            key={mode}
            type="button"
            className={state.lights === mode ? "active" : ""}
            onClick={() => LN.send("StationLightsRequested", { mode })}
          >
            {mode}
          </button>
        ))}
      </div>
      <h3>Display</h3>
      <div className="row">
        <button type="button" className={state.psx ? "active" : ""} onClick={() => LN.send("PsxModeRequested", { on: !state.psx })}>
          PS1 look {state.psx ? "on" : "off"}
        </button>
      </div>
      <h3>Layout</h3>
      <PlanEditor plan={state.plan} />
    </>
  );
}

const PALETTE: { ch: string; label: string }[] = [
  { ch: ".", label: "Room" },
  { ch: "c", label: "Corridor" },
  { ch: "D", label: "Door" },
  { ch: "W", label: "Window" },
  { ch: "#", label: "Wall" },
  { ch: "P", label: "Start" },
];

/** Paint the plan (one cell per character), then Apply: Unity rebuilds the station from it on the next frame. */
function PlanEditor({ plan }: { plan: string }) {
  const [rows, setRows] = useState<string[]>(() => plan.split("\n"));
  const [brush, setBrush] = useState(".");
  const [dirty, setDirty] = useState(false);
  const painting = useRef(false);

  // Follow the game's plan unless there are unapplied edits.
  useEffect(() => {
    if (!dirty) setRows(plan.split("\n"));
  }, [plan, dirty]);

  const width = useMemo(() => Math.max(0, ...rows.map((r) => r.length)), [rows]);
  const paint = (x: number, y: number) =>
    setRows((rs) => {
      const row = rs[y].padEnd(width, "#");
      if (row[x] === brush) return rs;
      const next = [...rs];
      next[y] = row.slice(0, x) + brush + row.slice(x + 1);
      setDirty(true);
      return next;
    });

  const apply = () => {
    LN.send("StationPlanRequested", { plan: rows.join("\n") });
    setDirty(false);
  };
  const copy = () => navigator.clipboard?.writeText(rows.map((r) => `plan: "${r}"`).join("\n"));

  return (
    <div className="plan-editor">
      <div className="row">
        {PALETTE.map((p) => (
          <button key={p.ch} type="button" className={brush === p.ch ? "active" : ""} onClick={() => setBrush(p.ch)}>
            <span className={`swatch s-${swatch(p.ch)}`} /> {p.label}
          </button>
        ))}
      </div>
      <div
        className="plan-grid ui"
        style={{ gridTemplateColumns: `repeat(${width}, 1fr)` }}
        onPointerLeave={() => (painting.current = false)}
        onPointerUp={() => (painting.current = false)}
      >
        {rows.map((row, y) =>
          Array.from({ length: width }, (_, x) => {
            const ch = row[x] ?? "#";
            return (
              <div
                key={`${x},${y}`}
                className={`cell s-${swatch(ch)}`}
                title={`${x}, ${y}: ${ch}`}
                onPointerDown={(e) => {
                  e.preventDefault();
                  painting.current = true;
                  paint(x, y);
                }}
                onPointerEnter={() => painting.current && paint(x, y)}
              />
            );
          }),
        )}
      </div>
      <div className="row">
        <button type="button" onClick={apply} disabled={!dirty}>
          Apply
        </button>
        <button type="button" onClick={() => { setRows(plan.split("\n")); setDirty(false); }} disabled={!dirty}>
          Revert
        </button>
        <button type="button" onClick={copy}>
          Copy as txtpb
        </button>
      </div>
    </div>
  );
}

function swatch(ch: string): string {
  switch (ch) {
    case ".":
    case "E":
      return "room";
    case "c":
      return "corridor";
    case "D":
      return "door";
    case "W":
      return "window";
    case "P":
      return "start";
    default:
      return "wall";
  }
}
