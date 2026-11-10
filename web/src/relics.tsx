// Relics at the crash site: the d20 when you use one (tumbles, lands on the roll, + luck and sheet = total, what it did),
// the ready relic (R, or click: an ordinary pack use, Core/InventoryPresenter rolls it), and a revealed relic's
// bearing (RelicSite). Unity rolls; this only shows it.
import { useEffect, useRef, useState } from "react";
import { LN, useLNEvent, useLNState } from "./ln";
import type { RelicRolled } from "./ln";

export function RelicHud() {
  const site = useLNState("RelicSite");
  const pack = useLNState("PlayerInventory");
  return (
    <>
      <D20 />
      {site?.reveal && <div className="relic-reveal">{site.reveal}</div>}
      {pack && pack.ready !== 0 && pack.readyName && <ReadyRelic uid={pack.ready} name={pack.readyName} cooldown={pack.cooldown} />}
    </>
  );
}

/** The relic R uses: its name and cooldown; click to use it too. */
function ReadyRelic({ uid, name, cooldown }: { uid: number; name: string; cooldown: number }) {
  return (
    <div className="relic-strip ui">
      <button
        type="button"
        className="relic-item selected usable"
        title={`${name} - R or click to use`}
        onClick={() => LN.send("InventoryUseRequested", { uid })}
      >
        <span className="relic-icon">d20</span>
        <span className="relic-name">{name}</span>
        {cooldown > 0 ? <span className="relic-cool">{Math.ceil(cooldown)}s</span> : <kbd>R</kbd>}
      </button>
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
