// ?ln-fps: a small frame-rate readout (top centre) for checking a device by eye, e.g. on a phone, where there's no
// devtools to hand. Unity renders once per animation frame, so the page's frame rate is the game's. Updates twice
// a second: average fps, the median and 95th-percentile frame time, and the texture format the page chose.
import { useEffect, useState } from "react";

const on = typeof location !== "undefined" && new URLSearchParams(location.search).has("ln-fps");

export function FpsMeter() {
  const [text, setText] = useState("");
  useEffect(() => {
    if (!on) return;
    let raf = 0;
    let last = performance.now();
    let since = last;
    const frames: number[] = [];
    const tick = (now: number) => {
      frames.push(now - last);
      last = now;
      if (now - since >= 500 && frames.length > 1) {
        const sorted = [...frames].sort((a, b) => a - b);
        const fps = (frames.length * 1000) / (now - since);
        const p95 = sorted[Math.floor(sorted.length * 0.95)];
        const tex = (window as { lnTextures?: string }).lnTextures ?? "?";
        setText(`${fps.toFixed(0)} fps · ${sorted[sorted.length >> 1].toFixed(1)} ms · p95 ${p95.toFixed(1)} ms · ${tex}`);
        frames.length = 0;
        since = now;
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, []);
  if (!on || !text) return null;
  return <div className="fps-meter">{text}</div>;
}
