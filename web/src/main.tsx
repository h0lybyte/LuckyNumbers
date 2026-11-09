// Overlay entry: mounts React into the WebGL template's #overlay (over the canvas) with the #dock
// available for portals. Loaded as overlay/overlay.js by the template; CSS ships inside this file.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import css from "./overlay.css?inline";

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
