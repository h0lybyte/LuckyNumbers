// Fights on the planet (Unity: Planet/CombatPresenter). Every attack is a d20 roll; the feed shows the last few,
// newest at the bottom: who at whom, d20 + bonus = total against the armour, hit (and the damage dice) or miss.
// The monster you hit last shows its health; going down shows the rescue. Your own rolls also land on the d20.
// On a Defend job, the beacon's panel: its hp, this wave's charge, the wave and the horde (DefendState).
import { useEffect, useRef, useState } from "react";
import { useLNEvent, useLNState } from "./ln";
import type { CombatRoll } from "./ln";
import { AttackD20, grade } from "./relics";

type Line = CombatRoll & { id: number; at: number };

export function CombatHud() {
  const combat = useLNState("CombatState");
  const [name, hp, max] = (combat?.target ?? "").split("|");
  return (
    <>
      <AttackD20 />
      <DefendHud />
      {combat?.target && (
        <div className="combat-target" aria-label={`${name}: ${hp} of ${max}`}>
          <span>{name}</span>
          <div className="combat-target-bar">
            <div style={{ width: `${(100 * Number(hp)) / Math.max(1, Number(max))}%` }} />
          </div>
        </div>
      )}
      <CombatFeed />
      {combat?.downed && <div className="combat-down">Down. Rescue drone inbound…</div>}
    </>
  );
}

/** A Defend job's beacon (DefendState): hp, charge, wave and horde; the rest between waves; won or lost. */
function DefendHud() {
  const d = useLNState("DefendState");
  if (!d?.phase || d.phase === "ready") return null;
  const pct = (v: number, max: number) => `${Math.max(0, Math.min(100, (100 * v) / Math.max(1, max)))}%`;
  const head =
    d.phase === "won" ? "BEACON CHARGED"
    : d.phase === "lost" ? "BEACON LOST"
    : d.phase === "rest" ? `WAVE ${d.wave + 1} IN ${d.rest} S`
    : `WAVE ${d.wave}/${d.waves} · CHARGING`;
  // The whole charge, across the waves: those done plus this one's.
  const charged = d.phase === "won" ? d.waves : Math.max(0, d.wave - 1) + (d.phase === "rest" ? 1 : d.charge);
  return (
    <div className={`defend ${d.phase}${d.hp < d.hpMax * 0.3 ? " low" : ""}`} role="status"
         aria-label={`Beacon ${d.hp} of ${d.hpMax} hp, wave ${d.wave} of ${d.waves}, ${Math.round(d.charge * 100)}% charged`}>
      <div className="defend-head">
        <span>{head}</span>
        <em>{d.phase === "charging" ? `${Math.round(d.charge * 100)}%` : d.phase === "rest" ? `${d.wave}/${d.waves}` : ""}</em>
      </div>
      <div className="defend-row hp">
        <span>BEACON</span>
        <div className="dogfight-bar"><div style={{ width: pct(d.hp, d.hpMax) }} /></div>
        <em>{d.hp}</em>
      </div>
      <div className="defend-row charge">
        <span>CHARGE</span>
        <div className="dogfight-bar"><div style={{ width: pct(charged, d.waves) }} /></div>
        <em>{Math.round((100 * charged) / Math.max(1, d.waves))}%</em>
      </div>
      {(d.phase === "charging" || d.phase === "rest") && (
        <small>
          {d.horde} HOSTILE{d.horde === 1 ? "" : "S"}{d.waiting > 0 && ` · ${d.waiting} MORE COMING`}
        </small>
      )}
    </div>
  );
}

/** The last few rolls (CombatRoll), newest at the bottom; each fades after a few seconds. Space combat shares it. */
export function CombatFeed() {
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
  if (lines.length === 0) return null;
  return (
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
          <b>
            {l.crit ? `CRIT ${l.damage}` : l.fumble && !l.hit ? "FUMBLE" : l.strong ? `STRONG ${l.damage}`
              : l.graze ? `GRAZE ${l.damage}` : l.hit ? `HIT ${l.damage}` : "MISS"}
          </b>
          {l.hit && <small>{grade(l).dice}</small>}
        </li>
      ))}
    </ol>
  );
}
