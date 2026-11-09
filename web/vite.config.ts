import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// One ES module, overlay/overlay.js, next to the Unity build's index.html (CSS is inlined into it).
// LN_OVERLAY_OUT overrides the folder (tools/ln.py and CI point it at the build being packaged).
const out = process.env.LN_OVERLAY_OUT ?? fileURLToPath(new URL("../LuckyNumbers/Builds/WebGL/overlay", import.meta.url));

export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    outDir: out,
    emptyOutDir: true,
    target: "es2022",
    modulePreload: false,
    rolldownOptions: {
      input: "src/main.tsx",
      output: { entryFileNames: "overlay.js", chunkFileNames: "[name].js", assetFileNames: "[name][extname]" },
    },
  },
});
