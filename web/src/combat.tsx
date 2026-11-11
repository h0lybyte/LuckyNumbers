// Fights on the planet (Unity: Planet/CombatPresenter). Every attack is a d20 roll; the feed shows the last few,
// newest at the bottom: who at whom, d20 + bonus = total against the armour, hit (and the damage dice) or miss.
// The monster you hit last shows its health; going down shows the rescue.
import { useEffect, useRef, useState } from "react";
import { useLNEvent, useLNState } from "./ln";
import type { CombatRoll } from "./ln";

type Line = CombatRoll & { id: number; at: number };

export function CombatHud() {
  const combat = useLNState("CombatState");
  const [lines, setLines] = useState<Line[]>([]);
  const next = useRef(0);
  useLNEvent("CombatRoll", (e) => {
    const line = { ...e, id: next.current++, at: Date.now() };
    setLines((old) => [...old.slice(-4), line]);
  });
  // Lines fade out after a few seconds.
  useEffect(() => {
    if (lines.length === 0) return;
    const t = setTimeout(() => setLines((old) => old.filter((l) => Date.now() - l.at < 5000)), 1000);
    return () => clearTimeout(t);
  }, [lines]);
  const [name, hp, max] = (combat?.target ?? "").split("|");
  return (
    <>
      {combat?.target && (
        <div className="combat-target" aria-label={`${name}: ${hp} of ${max}`}>
          <span>{name}</span>
          <div className="combat-target-bar">
            <div style={{ width: `${(100 * Number(hp)) / Math.max(1, Number(max))}%` }} />
          </div>
        </div>
      )}
      {lines.length > 0 && (
        <ol className="combat-feed" aria-live="polite">
          {lines.map((l) => (
            <li key={l.id} className={`${l.player ? "mine" : "theirs"} ${l.hit ? "hit" : "miss"}${l.crit ? " crit" : ""}`}>
              <span className="who">
                {l.attacker} → {l.target}
              </span>
              <span className="die">d20 {l.d20}</span>
              <span className="sum">
                {l.bonus >= 0 ? "+" : ""}
                {l.bonus} = {l.total} vs {l.armor}
              </span>
              <b>{l.crit ? `CRIT ${l.damage}` : l.fumble ? "FUMBLE" : l.hit ? `HIT ${l.damage}` : "MISS"}</b>
              {l.hit && <small>{l.dice}{l.crit ? " x2" : ""}</small>}
            </li>
          ))}
        </ol>
      )}
      {combat?.downed && <div className="combat-down">Down. Rescue drone inbound…</div>}
    </>
  );
}
