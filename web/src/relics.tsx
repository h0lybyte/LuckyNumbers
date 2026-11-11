// Relics at the crash site: the d20 when you use one (tumbles, lands on the roll, + luck and sheet = total, what it did),
// the ready relic (R, or click: an ordinary pack use, Core/InventoryPresenter rolls it), and a revealed relic's
// bearing (RelicSite). Unity rolls; this only shows it.
import { useEffect, useRef, useState } from "react";
import { LN, useLNEvent, useLNState } from "./ln";
import type { CombatRoll, RelicRolled } from "./ln";

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

/** The relic roll: random faces for ~0.9 s, then the real one; the result stays a few seconds. */
function D20() {
  const { event: e, face, settled } = useDiceRoll<RelicRolled>("RelicRolled", (r) => r.roll, 900, 5200);
  if (!e) return null;
  const tone = e.roll === 1 ? "fumble" : e.total >= 20 ? "crit" : e.total >= 15 ? "good" : "";
  return (
    <D20Panel face={face} settled={settled} tone={tone} heading={e.relic}>
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
 * The latest `name` event (that `keep` lets through) as a roll: the face tumbles for `spinMs`, then lands on
 * `face(e)`; gone after `holdMs`. A new event restarts it, so rapid fire keeps it rolling.
 */
export function useDiceRoll<E>(
  name: "RelicRolled" | "CombatRoll",
  face: (e: E) => number,
  spinMs: number,
  holdMs: number,
  keep: (e: E) => boolean = () => true,
) {
  const [event, setEvent] = useState<E | null>(null);
  const [shown, setShown] = useState(20);
  const [settled, setSettled] = useState(false);
  const timers = useRef<{ spin?: number; land?: number; hide?: number }>({});
  const clear = () => {
    const t = timers.current;
    clearInterval(t.spin);
    clearTimeout(t.land);
    clearTimeout(t.hide);
  };

  useLNEvent(name, (raw) => {
    const e = raw as unknown as E;
    if (!keep(e)) return;
    clear();
    setEvent(e);
    setSettled(false);
    timers.current = {
      spin: window.setInterval(() => setShown(1 + Math.floor(Math.random() * 20)), 70),
      land: window.setTimeout(() => {
        clearInterval(timers.current.spin);
        setShown(face(e));
        setSettled(true);
      }, spinMs),
      hide: window.setTimeout(() => setEvent(null), holdMs),
    };
  });
  useEffect(() => clear, []);
  return { event, face: shown, settled };
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

/**
 * Your attack rolls (CombatRoll you rolled) on the d20: quick, to keep up with the trigger. d20 + bonus = total
 * against the armour, then hit (and the damage dice), miss, crit or fumble.
 */
export function AttackD20() {
  const { event: e, face, settled } = useDiceRoll<CombatRoll>("CombatRoll", (r) => r.d20, 380, 2600, (r) => r.player);
  if (!e) return null;
  const tone = e.fumble ? "fumble" : e.crit ? "crit" : e.hit ? "good" : "miss";
  const title = e.fumble ? "Fumble" : e.crit ? "Critical hit" : e.hit ? "Hit" : "Miss";
  const line = e.hit
    ? `${e.damage} damage (${e.dice}${e.crit ? " x2" : ""})`
    : e.fumble ? "Nowhere near it." : e.total >= e.armor - 2 ? "Grazed past." : "Wide.";
  return (
    <D20Panel face={face} settled={settled} tone={tone} heading={`${e.weapon} \u2192 ${e.target}`}>
      <div className="d20-sum">
        d20 <b>{e.d20}</b>
        {e.bonus !== 0 && <> {e.bonus > 0 ? "+" : "\u2212"} <b>{Math.abs(e.bonus)}</b></>} = <b>{e.total}</b> vs AC <b>{e.armor}</b>
      </div>
      <div className="d20-title">{title}</div>
      <div className="d20-line">{line}</div>
    </D20Panel>
  );
}
