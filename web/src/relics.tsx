// Relics at the crash site: the d20 when you use one (tumbles, lands on the roll, + luck and sheet = total, what it did),
// the inventory strip (click to use), and a revealed relic's bearing. Unity rolls; this only shows it.
import { useEffect, useRef, useState } from "react";
import { LN, useLNEvent, useLNState } from "./ln";
import type { RelicRolled } from "./ln";

export function RelicHud() {
  const inv = useLNState("RelicInventory");
  return (
    <>
      <D20 />
      {inv?.reveal && <div className="relic-reveal">{inv.reveal}</div>}
      {inv && inv.items.length > 0 && <Inventory items={inv.items} selected={inv.selected} cooldown={inv.cooldown} />}
    </>
  );
}

function Inventory({ items, selected, cooldown }: { items: string[]; selected: number; cooldown: number }) {
  return (
    <div className="relic-strip ui">
      {items.map((item, i) => {
        const [id, name, figure] = item.split("|");
        const usable = id !== "relic";
        return (
          <button
            key={i}
            type="button"
            className={`relic-item${i === selected ? " selected" : ""}${usable ? " usable" : ""}`}
            title={`${name}${figure ? ` (${figure})` : ""}${usable ? " - R or click to use" : ""}`}
            onClick={() => LN.send("RelicUseRequested", { index: i })}
          >
            <span className="relic-icon">{usable ? "d20" : name.slice(0, 1).toUpperCase()}</span>
            <span className="relic-name">{name}</span>
            {usable && i === selected && cooldown > 0 && <span className="relic-cool">{Math.ceil(cooldown)}s</span>}
          </button>
        );
      })}
    </div>
  );
}

/** The relic roll: random faces for ~0.9 s, then the real one; the result stays a few seconds. */
function D20() {
  const roll = useDiceRoll<RelicRolled>("RelicRolled", 900, 5200);
  if (!roll.event) return null;
  const e = roll.event;
  const tone = e.roll === 1 ? "fumble" : e.total >= 20 ? "crit" : e.total >= 15 ? "good" : "";
  return (
    <D20Panel face={roll.face} settled={roll.settled} tone={tone} heading={e.relic}>
      <div className="d20-sum">
        d20 <b>{e.roll}</b>
        {e.bonus !== 0 && <> {e.bonus > 0 ? "+" : "\u2212"} mods <b>{Math.abs(e.bonus)}</b></>} = <b>{e.total}</b>
      </div>
      <div className="d20-title">{e.title}</div>
      <div className="d20-line">{e.text}</div>
    </D20Panel>
  );
}

/**
 * The latest `name` event as a roll: its face tumbles for `spinMs`, then lands on `roll`; gone after `holdMs`.
 * A new event restarts it (rapid fire just keeps it rolling).
 */
export function useDiceRoll<E extends { roll: number }>(name: "RelicRolled" | "AttackRolled", spinMs: number, holdMs: number) {
  const [event, setEvent] = useState<E | null>(null);
  const [face, setFace] = useState(20);
  const [settled, setSettled] = useState(false);
  const timers = useRef<{ spin?: number; land?: number; hide?: number }>({});
  const clear = () => {
    const t = timers.current;
    clearInterval(t.spin);
    clearTimeout(t.land);
    clearTimeout(t.hide);
  };

  useLNEvent(name, (e) => {
    clear();
    setEvent(e as unknown as E);
    setSettled(false);
    timers.current = {
      spin: window.setInterval(() => setFace(1 + Math.floor(Math.random() * 20)), 70),
      land: window.setTimeout(() => {
        clearInterval(timers.current.spin);
        setFace(e.roll);
        setSettled(true);
      }, spinMs),
      hide: window.setTimeout(() => setEvent(null), holdMs),
    };
  });
  useEffect(() => clear, []);
  return { event, face, settled };
}

/** The d20 and what's beside it (shown once it lands). */
export function D20Panel({ face, settled, tone, heading, children }: {
  face: number;
  settled: boolean;
  tone: string;
  heading: string;
  children: React.ReactNode;
}) {
  return (
    <div className={`d20-panel ${settled ? "settled" : "rolling"} ${tone}`}>
      <svg className="d20" viewBox="-50 -50 100 100" aria-hidden="true">
        <polygon points="0,-46 40,-23 40,23 0,46 -40,23 -40,-23" className="d20-hull" />
        <polygon points="0,-30 26,15 -26,15" className="d20-face" />
        <path d="M 0 -46 L 0 -30 M 40 -23 L 26 15 M -40 -23 L -26 15 M 40 23 L 26 15 M -40 23 L -26 15 M 0 46 L 26 15 M 0 46 L -26 15" className="d20-edge" />
        <text y="4" className="d20-number">{face}</text>
      </svg>
      <div className="d20-text">
        <div className="d20-relic">{heading}</div>
        {settled && children}
      </div>
    </div>
  );
}
