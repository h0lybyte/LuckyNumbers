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

/** The roll: random faces for ~0.9 s, then the real one; the result stays a few seconds. */
function D20() {
  const [roll, setRoll] = useState<RelicRolled | null>(null);
  const [face, setFace] = useState(20);
  const [settled, setSettled] = useState(false);
  const timers = useRef<number[]>([]);

  useLNEvent("RelicRolled", (e) => {
    timers.current.forEach(clearTimeout);
    timers.current = [];
    setRoll(e);
    setSettled(false);
    const spin = window.setInterval(() => setFace(1 + Math.floor(Math.random() * 20)), 70);
    timers.current.push(
      window.setTimeout(() => {
        clearInterval(spin);
        setFace(e.roll);
        setSettled(true);
      }, 900) as unknown as number,
      window.setTimeout(() => setRoll(null), 5200) as unknown as number,
      spin as unknown as number,
    );
  });
  useEffect(() => () => timers.current.forEach(clearTimeout), []);

  if (!roll) return null;
  const tone = roll.roll === 1 ? "fumble" : roll.total >= 20 ? "crit" : roll.total >= 15 ? "good" : "";
  return (
    <div className={`d20-panel ${settled ? "settled" : "rolling"} ${tone}`}>
      <svg className="d20" viewBox="-50 -50 100 100" aria-hidden="true">
        <polygon points="0,-46 40,-23 40,23 0,46 -40,23 -40,-23" className="d20-hull" />
        <polygon points="0,-30 26,15 -26,15" className="d20-face" />
        <polyline points="0,-46 0,-30 M 40,-23 26,15 M -40,-23 -26,15" className="d20-edge" />
        <path d="M 0 -46 L 0 -30 M 40 -23 L 26 15 M -40 -23 L -26 15 M 40 23 L 26 15 M -40 23 L -26 15 M 0 46 L 26 15 M 0 46 L -26 15" className="d20-edge" />
        <text y="4" className="d20-number">{face}</text>
      </svg>
      <div className="d20-text">
        <div className="d20-relic">{roll.relic}</div>
        {settled && (
          <>
            <div className="d20-sum">
              d20 <b>{roll.roll}</b>
              {roll.bonus !== 0 && <> {roll.bonus > 0 ? "+" : "\u2212"} mods <b>{Math.abs(roll.bonus)}</b></>} = <b>{roll.total}</b>
            </div>
            <div className="d20-title">{roll.title}</div>
            <div className="d20-line">{roll.text}</div>
          </>
        )}
      </div>
    </div>
  );
}
