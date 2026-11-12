// Credits that count instead of jump: useCountUp eases a shown number towards its target (fast first, settling
// last), CreditCounter is the gold readout with a "+N" / "-N" that floats off it, and PayoutBanner is the big
// "credits received" card when the museum (or a job) pays you at the station.
import { useEffect, useRef, useState } from "react";
import { useLNState } from "./ln";

const reduceMotion = () => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;

/** The number to show while `target` changes: counts from where it was to the new value (bigger jumps run longer). */
export function useCountUp(target: number, ms = 900, from?: number): number {
  const [shown, setShown] = useState(from ?? target);
  const shownRef = useRef(from ?? target);
  useEffect(() => {
    const a = shownRef.current;
    const d = target - a;
    if (d === 0) return;
    if (reduceMotion()) {
      shownRef.current = target;
      setShown(target);
      return;
    }
    const duration = Math.min(2600, ms + 320 * Math.log10(Math.abs(d) + 1));
    const start = performance.now();
    let raf = 0;
    const step = (t: number) => {
      const k = Math.min(1, (t - start) / duration);
      const v = Math.round(a + d * (1 - Math.pow(1 - k, 4)));   // ease-out quart: races, then settles
      shownRef.current = v;
      setShown(v);
      if (k < 1) raf = requestAnimationFrame(step);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [target, ms]);
  return shown;
}

type Delta = { id: number; amount: number };
let nextDelta = 1;
// What each remembered counter last settled on: one that comes back (a new scene, the HUD shown again) counts on
// from there, so a payout made while it was away still runs.
const remembered = new Map<string, number>();

/**
 * Gold credits readout: counts to the new balance, pulses while it does, and floats the change off the top.
 * `memo` names it across remounts (see remembered).
 */
export function CreditCounter({ value, className = "", memo }: { value: number; className?: string; memo?: string }) {
  const start = memo !== undefined ? (remembered.get(memo) ?? value) : value;
  const shown = useCountUp(value, 900, start);
  const last = useRef(start);
  const [deltas, setDeltas] = useState<Delta[]>([]);
  useEffect(() => {
    if (memo !== undefined) remembered.set(memo, value);
  }, [memo, value]);
  useEffect(() => {
    const amount = value - last.current;
    last.current = value;
    if (amount === 0) return;
    const id = nextDelta++;
    setDeltas((d) => [...d.slice(-2), { id, amount }]);
    const t = setTimeout(() => setDeltas((d) => d.filter((x) => x.id !== id)), 1600);
    return () => clearTimeout(t);
  }, [value]);
  const counting = shown !== value;
  const dir = counting ? (value > shown ? " up" : " down") : "";
  return (
    <span className={`credit-counter${dir} ${className}`} aria-label={`${value.toLocaleString()} credits`}>
      <span className="credit-value" aria-hidden="true">
        {shown.toLocaleString()}
      </span>
      <span className="credit-unit" aria-hidden="true">
        cr
      </span>
      {deltas.map((d) => (
        <span key={d.id} className={`credit-delta ${d.amount > 0 ? "gain" : "spend"}`} aria-hidden="true">
          {d.amount > 0 ? "+" : "-"}
          {Math.abs(d.amount).toLocaleString()}
        </span>
      ))}
    </span>
  );
}

/** At the station: every time you're paid, a card that counts the payout up from zero and rolls the balance on. */
export function PayoutBanner() {
  const credits = useLNState("PlayerVoyage")?.credits ?? 0;
  const scene = useLNState("FlowState")?.scene;
  const last = useRef<number | null>(null);
  const sceneRef = useRef(scene);
  sceneRef.current = scene;
  const timers = useRef<number[]>([]);
  const [pay, setPay] = useState<{ id: number; amount: number; before: number; after: number } | null>(null);
  useEffect(() => () => timers.current.forEach(clearTimeout), []);
  useEffect(() => {
    const before = last.current;
    last.current = credits;
    // Loading a save (on the title) or the first value we see isn't a payout; spending isn't either.
    const s = sceneRef.current;
    if (before === null || s === "Title" || s === undefined || credits <= before) return;
    const id = nextDelta++;
    // A beat after the station fades in, so it lands with the call. Not cancelled by later renders: a payout
    // that lands as the scene state changes still shows.
    timers.current.push(
      window.setTimeout(() => setPay({ id, amount: credits - before, before, after: credits }), 700),
      window.setTimeout(() => setPay((p) => (p?.id === id ? null : p)), 5200),
    );
  }, [credits]);
  if (!pay) return null;
  return <PayoutCard key={pay.id} amount={pay.amount} before={pay.before} after={pay.after} />;
}

function PayoutCard({ amount, before, after }: { amount: number; before: number; after: number }) {
  const [go, setGo] = useState(false);
  useEffect(() => {
    const t = setTimeout(() => setGo(true), 250);   // the card lands, then the numbers run
    return () => clearTimeout(t);
  }, []);
  const gained = useCountUp(go ? amount : 0, 1300, 0);
  const balance = useCountUp(go ? after : before, 1500, before);
  const done = gained === amount;
  return (
    <div className={`payout-card ui${done ? " done" : ""}`} role="status" aria-label={`Credits received: ${amount.toLocaleString()}`}>
      <div className="payout-kicker">Credits received</div>
      <div className="payout-amount" aria-hidden="true">
        +{gained.toLocaleString()}
        <small>cr</small>
      </div>
      <div className="payout-balance" aria-hidden="true">
        Balance <b>{balance.toLocaleString()} cr</b>
      </div>
    </div>
  );
}
