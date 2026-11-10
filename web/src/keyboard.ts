// Typing into HTML fields over the game. Unity's Web player listens for keys on the page and calls
// preventDefault on them (WebGLInput.captureAllKeyboardInput), so a text field in the dock or an overlay
// would never get its characters, and the game would walk around while you type. While an editable element
// has focus, a capture-phase listener on window (first in line, before Unity's) stops the key from going
// further: the field types normally and the game doesn't see it. Esc still blurs the field.

function editable(el: EventTarget | null): el is HTMLElement {
  if (!(el instanceof HTMLElement)) return false;
  if (el.isContentEditable) return true;
  if (el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) return true;
  if (el instanceof HTMLInputElement) {
    return !["button", "checkbox", "radio", "range", "submit", "reset", "color", "file", "image"].includes(el.type);
  }
  return false;
}

export function installKeyboardGuard(): () => void {
  const guard = (e: KeyboardEvent) => {
    const target = document.activeElement;
    if (!editable(target)) return;
    if (e.type === "keydown" && e.key === "Escape") {
      target.blur(); // back to the game
      return;
    }
    e.stopImmediatePropagation(); // Unity never sees it, so it can't preventDefault: the field gets the key
  };
  const types = ["keydown", "keyup", "keypress"] as const;
  for (const t of types) window.addEventListener(t, guard, { capture: true });
  return () => {
    for (const t of types) window.removeEventListener(t, guard, { capture: true });
  };
}
