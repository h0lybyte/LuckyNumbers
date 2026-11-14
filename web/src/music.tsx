// Music on the Web build (MusicState, set by Unity's Core/MusicPresenter). Tracks are plain files under
// StreamingAssets: an <audio> element streams and decodes them natively, so they are never in the .data download
// or the wasm heap. Volume goes through a gain node (iOS ignores <audio>.volume). Browsers hold audio until the
// page's first click / key / touch: playback starts then.
import { useEffect, useRef } from "react";
import { useLNState } from "./ln";

interface Track {
  name: string;
  el: HTMLAudioElement;
  gain: GainNode;
  stopAt: number; // pause timer after a fade to silence
}

let ctx: AudioContext | null = null;
let waiting = false;

function context(): AudioContext {
  ctx ??= new AudioContext();
  return ctx;
}

/** Play (or resume) now, or on the first user gesture if the browser blocks it. */
function start(t: Track) {
  const go = () => {
    void context().resume();
    t.el.play().catch(() => wait());
  };
  const wait = () => {
    if (waiting) return;
    waiting = true;
    const resume = () => {
      waiting = false;
      for (const ev of ["pointerdown", "keydown", "touchend"]) window.removeEventListener(ev, resume, true);
      go();
    };
    for (const ev of ["pointerdown", "keydown", "touchend"]) window.addEventListener(ev, resume, true);
  };
  if (context().state !== "running") wait();
  go();
}

function open(name: string): Track {
  const el = new Audio(`StreamingAssets/${name}`);
  el.loop = true;
  el.preload = "auto";
  const c = context();
  const gain = c.createGain();
  gain.gain.value = 0;
  c.createMediaElementSource(el).connect(gain).connect(c.destination);
  return { name, el, gain, stopAt: 0 };
}

function fade(t: Track, to: number, seconds: number) {
  const c = context();
  const now = c.currentTime;
  const g = t.gain.gain;
  g.cancelScheduledValues(now);
  g.setValueAtTime(g.value, now);
  g.linearRampToValueAtTime(to, now + Math.max(0.01, seconds));
  window.clearTimeout(t.stopAt);
  if (to <= 0) t.stopAt = window.setTimeout(() => t.el.pause(), seconds * 1000 + 50);
  else start(t);
}

export function Music() {
  const state = useLNState("MusicState");
  const current = useRef<Track | null>(null);
  const track = state?.track ?? "";
  const volume = Math.max(0, Math.min(1, state?.volume ?? 0));
  const seconds = state?.fadeSeconds ?? 0;

  useEffect(() => {
    let t = current.current;
    if (t && t.name !== track) {
      // Another track: the old one fades out and is let go.
      const old = t;
      fade(old, 0, seconds);
      window.setTimeout(() => old.el.removeAttribute("src"), seconds * 1000 + 100);
      t = current.current = null;
    }
    if (!track) return;
    if (!t) t = current.current = open(track);
    fade(t, volume, seconds);
  }, [track, volume, seconds]);

  return null;
}
