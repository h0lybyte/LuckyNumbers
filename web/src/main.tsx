// Overlay entry: mounts React into the WebGL template's #overlay (over the canvas) with the #dock
// available for portals. Loaded as overlay/overlay.js by the template; CSS ships inside this file.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { installKeyboardGuard } from "./keyboard";
import css from "./overlay.css?inline";

// Before anything else listens: text fields keep their keys (see keyboard.ts).
installKeyboardGuard();

const style = document.createElement("style");
style.textContent = css;
document.head.appendChild(style);

const host = document.getElementById("overlay");
if (host) {
  const root = document.createElement("div");
  root.id = "overlay-root";
  host.appendChild(root);
  createRoot(root).render(
    <StrictMode>
      <App dock={document.getElementById("dock")} />
    </StrictMode>,
  );
} else {
  console.warn("[overlay] no #overlay element; not mounting");
}
