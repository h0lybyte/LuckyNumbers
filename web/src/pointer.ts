// Pointer lock, seen from the page. Unity locks the pointer on a click into the first-person view
// (FirstPersonPresenter); while locked every mouse event goes to the canvas, so HTML overlays can't be clicked.
// Esc (browser) or Tab (here) frees it; a click on the view locks it again. HTML clicks never reach the canvas,
// so clicking an overlay button doesn't re-lock.
import { useSyncExternalStore } from "react";

const subscribe = (notify: () => void) => {
  document.addEventListener("pointerlockchange", notify);
  return () => document.removeEventListener("pointerlockchange", notify);
};

/** True while the game holds the pointer (mouse look). */
export function usePointerLocked(): boolean {
  return useSyncExternalStore(subscribe, () => document.pointerLockElement != null);
}

/** Tab frees the mouse while it's locked (Esc does too, natively). Capture phase: before Unity sees the key. */
export function installFreeMouseKey(): () => void {
  const onKey = (e: KeyboardEvent) => {
    if (e.code !== "Tab" || document.pointerLockElement == null) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    document.exitPointerLock();
  };
  window.addEventListener("keydown", onKey, { capture: true });
  return () => window.removeEventListener("keydown", onKey, { capture: true });
}
