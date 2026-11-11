// Save slots (Unity: Core/SavePresenter). Play on the title opens this picker: an empty slot starts a new character
// there, a filled one continues it. 1-3 pick a slot, Esc goes back. Delete asks once more before it clears a slot.
import { useEffect, useState } from "react";
import { LN, useLNState } from "./ln";

type Slot = { callsign: string; cls: string; level: number; seed: string; played: number; where: string; credits: number };

function parse(row: string): Slot | null {
  if (!row) return null;
  const [callsign, cls, level, seed, played, where, credits] = row.split("|");
  return { callsign, cls, level: Number(level), seed, played: Number(played), where, credits: Number(credits) };
}

function ago(ms: number): string {
  const s = Math.max(0, (Date.now() - ms) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return new Date(ms).toLocaleDateString();
}

export function SaveSlotsGate() {
  // Tell Unity there's a picker, so Play asks which slot.
  useEffect(() => LN.send("SaveSlotsReady", { on: true }), []);
  const state = useLNState("SaveSlots");
  const open = state?.open ?? false;
  const [confirm, setConfirm] = useState(-1);
  useEffect(() => setConfirm(-1), [open]);
  useEffect(() => {
    if (!open) return;
    if (document.pointerLockElement) document.exitPointerLock();
    const onKey = (e: KeyboardEvent) => {
      if (e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
      const n = e.code.startsWith("Digit") ? Number(e.code.slice(5)) : 0;
      if (n >= 1 && n <= (LN.states.SaveSlots?.slots.length ?? 0)) LN.send("SaveSlotPicked", { index: n - 1 });
      else if (e.code === "Escape") LN.send("SaveSlotPicked", { index: -1 });
      else return;
      e.preventDefault();
      e.stopImmediatePropagation(); // the title doesn't also see it
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [open]);
  if (!open || !state) return null;
  return (
    <div className="saves ui" role="dialog" aria-label="Pick a save slot">
      <div className="hangar-kicker">Personnel files // pick a slot</div>
      <ol className="save-list">
        {state.slots.map((row, i) => {
          const s = parse(row);
          return (
            <li key={i} className={s ? "filled" : "empty"}>
              <button type="button" className="save-pick" onClick={() => LN.send("SaveSlotPicked", { index: i })}>
                <kbd>{i + 1}</kbd>
                {s ? (
                  <span className="save-text">
                    <b>{s.callsign}</b>
                    <span>
                      {s.cls} · Level {s.level} · {s.where} · {s.credits.toLocaleString()} cr
                    </span>
                    <small>
                      Lucky number {s.seed} · {ago(s.played)}
                    </small>
                  </span>
                ) : (
                  <span className="save-text">
                    <b>Empty</b>
                    <span>New character</span>
                  </span>
                )}
                <span className="save-go">{s ? "Continue" : "New"}</span>
              </button>
              {s &&
                (confirm === i ? (
                  <span className="save-confirm">
                    Delete {s.callsign}?
                    <button type="button" className="sheet-btn danger" onClick={() => { LN.send("SaveSlotDeleteRequested", { index: i }); setConfirm(-1); }}>
                      Delete
                    </button>
                    <button type="button" className="sheet-btn" onClick={() => setConfirm(-1)}>
                      Keep
                    </button>
                  </span>
                ) : (
                  <button type="button" className="save-delete" aria-label={`Delete ${s.callsign}`} onClick={() => setConfirm(i)}>
                    Delete
                  </button>
                ))}
            </li>
          );
        })}
      </ol>
      <div className="hangar-foot">
        <span>
          <kbd>1</kbd>–<kbd>{state.slots.length}</kbd> pick · <kbd>Esc</kbd> back
        </span>
        <button type="button" className="sheet-btn" onClick={() => LN.send("SaveSlotPicked", { index: -1 })}>
          Back
        </button>
      </div>
    </div>
  );
}
