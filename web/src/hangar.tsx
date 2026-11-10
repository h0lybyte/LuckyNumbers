// The hangar, after character creation on the title: your fighter turns in front of the camera (Unity,
// Title/ShipPickerPresenter) while this picks its skin. A / D or the arrows step through them, Enter launches.
import { useEffect } from "react";
import { LN, useLNState } from "./ln";

export function HangarGate() {
  // Tell Unity there's a picker, so Play opens the hangar after character creation.
  useEffect(() => LN.send("ShipPickerReady", { on: true }), []);
  const picker = useLNState("ShipPicker");
  const open = picker?.open ?? false;
  useEffect(() => {
    if (!open) return;
    if (document.pointerLockElement) document.exitPointerLock();
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const step = e.code === "KeyA" || e.code === "ArrowLeft" ? -1 : e.code === "KeyD" || e.code === "ArrowRight" ? 1 : 0;
      if (step && !e.repeat) LN.send("ShipSkinStepRequested", { step });
      else if ((e.code === "Enter" || e.code === "Space") && !e.repeat) LN.send("ShipPickDone", {});
      else return;
      e.preventDefault();
      e.stopImmediatePropagation(); // the title doesn't also see Enter as Play
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [open]);
  if (!open || !picker) return null;
  return (
    <div className="hangar ui" role="dialog" aria-label="Pick your ship">
      <div className="hangar-kicker">Hangar // pick your ship</div>
      <div className="hangar-row">
        <button type="button" className="hangar-step" aria-label="Previous skin" onClick={() => LN.send("ShipSkinStepRequested", { step: -1 })}>
          ‹
        </button>
        <div className="hangar-name">
          <b>{picker.skin}</b>
          <small>
            Skin {picker.index + 1} / {picker.count}
          </small>
        </div>
        <button type="button" className="hangar-step" aria-label="Next skin" onClick={() => LN.send("ShipSkinStepRequested", { step: 1 })}>
          ›
        </button>
      </div>
      <div className="hangar-foot">
        <span>
          <kbd>A</kbd> <kbd>D</kbd> change · <kbd>Enter</kbd> launch
        </span>
        <button type="button" className="sheet-ready" onClick={() => LN.send("ShipPickDone", {})}>
          Launch
        </button>
      </div>
    </div>
  );
}
