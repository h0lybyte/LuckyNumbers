// The character sheet (C): abilities rolled from the lucky number, with +/- to move points between them until you
// leave the station; class and origin; level, XP, Stamina and HP; skills. Unity owns the rules
// (Core/CharacterPresenter) and sends CharacterSheet; this only shows it and sends requests back.
import { useEffect, useState } from "react";
import { LN, useLNState } from "./ln";

const sign = (n: number) => (n >= 0 ? `+${n}` : `−${-n}`);

/** C toggles the sheet (not while flying: C is descend there). Esc closes it. */
export function CharacterSheetGate() {
  const [open, setOpen] = useState(false);
  const flow = useLNState("FlowState");
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.repeat || e.ctrlKey || e.metaKey || e.altKey) return;
      const t = document.activeElement;
      if (t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement) return;
      if (e.code === "KeyC") {
        if (LN.states.FlightState?.flying) return;
        e.stopImmediatePropagation();
        setOpen((v) => !v);
      } else if (e.code === "Escape") setOpen(false);
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, []);
  // The sheet needs the mouse.
  useEffect(() => {
    if (open && document.pointerLockElement) document.exitPointerLock();
  }, [open]);

  const flying = useLNState("FlightState")?.flying ?? false;
  const scene = flow?.scene;
  if (scene !== "Station" && scene !== "Planet" && scene !== "Title") return null;
  return (
    <>
      {!flying && !open && scene !== "Title" && (
        <button type="button" className="ui chip sheet-chip" onClick={() => setOpen(true)}>
          Sheet <kbd>C</kbd>
        </button>
      )}
      {open && <Sheet onClose={() => setOpen(false)} />}
    </>
  );
}

function Sheet({ onClose }: { onClose: () => void }) {
  const s = useLNState("CharacterSheet");
  const hp = useLNState("PlayerHealth");
  if (!s) return null;
  const abilities = s.abilities.map((row) => {
    const [id, name, abbr, blurb] = row.split("|");
    return { id, name, abbr, blurb };
  });
  const classes = s.classes.map((row) => {
    const [id, name, blurb, key] = row.split("|");
    return { id, name, blurb, key };
  });
  const origins = s.origins.map((row) => {
    const [id, name, blurb, bonuses] = row.split("|");
    return { id, name, blurb, bonuses };
  });
  const cls = classes.find((c) => c.id === s.classId);
  const origin = origins.find((o) => o.id === s.originId);
  const editable = !s.locked;
  const xpPct = s.xpNext > 0 ? (100 * (s.xp - s.xpLevelStart)) / Math.max(1, s.xpNext - s.xpLevelStart) : 100;

  return (
    <div className="sheet ui" role="dialog" aria-label="Character sheet">
      <header className="sheet-head">
        <div>
          <div className="sheet-kicker">Personnel file // seed {s.seed}</div>
          <h2>{s.callsign}</h2>
          <div className="sheet-sub">
            {cls?.name ?? s.classId} &middot; {origin?.name ?? s.originId} &middot; Level {s.level}
          </div>
        </div>
        <div className="sheet-head-right">
          <span className={`sheet-badge${s.locked ? " locked" : ""}`}>{s.locked ? "Locked" : "Editable"}</span>
          <button type="button" className="sheet-close" onClick={onClose} aria-label="Close sheet">
            <kbd>C</kbd>
          </button>
        </div>
      </header>

      <div className="sheet-body">
        <section className="sheet-abilities">
          <h3>Abilities</h3>
          {editable && (
            <div className="sheet-points">
              <span>
                Points <b>{s.points}</b>
              </span>
              <span className="muted">can lower {s.canLower} more</span>
              <button type="button" className="sheet-btn" onClick={() => LN.send("CharacterResetRequested", {})}>
                Reset
              </button>
            </div>
          )}
          <div className="ability-grid">
            {abilities.map((a, i) => {
              const moved = s.moved[i] ?? 0;
              const adjust = s.adjust[i] ?? 0;
              const key = cls?.key === a.abbr;
              return (
                <div key={a.id} className={`ability${key ? " key" : ""}`} title={a.blurb}>
                  <div className="ability-abbr">
                    {a.abbr}
                    {key && <span className="ability-key">key</span>}
                  </div>
                  <div className="ability-score">{s.scores[i]}</div>
                  <div className="ability-mod">{sign(s.mods[i] ?? 0)}</div>
                  <div className="ability-roll">
                    rolled {s.rolled[i]}
                    {moved !== 0 && <span className={moved > 0 ? "up" : "down"}> {sign(moved)}</span>}
                  </div>
                  {editable && (
                    <div className="ability-adjust">
                      <button
                        type="button"
                        aria-label={`Lower ${a.name}`}
                        disabled={(adjust & 2) === 0}
                        onClick={() => LN.send("CharacterAbilityAdjustRequested", { index: i, delta: -1 })}
                      >
                        &minus;
                      </button>
                      <button
                        type="button"
                        aria-label={`Raise ${a.name}`}
                        disabled={(adjust & 1) === 0}
                        onClick={() => LN.send("CharacterAbilityAdjustRequested", { index: i, delta: 1 })}
                      >
                        +
                      </button>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </section>

        <section className="sheet-vitals">
          <h3>Vitals</h3>
          <Meter label="Stamina" value={s.stamina} max={s.staminaMax} tone="stamina" />
          <Meter label="HP" value={hp?.current ?? 0} max={hp?.max ?? 0} tone="hp" />
          <Meter label={s.xpNext > 0 ? `XP (next ${s.xpNext})` : "XP (max level)"} value={s.xp} max={s.xpNext || s.xp} pct={xpPct} tone="xp" />
          <div className="sheet-relic">
            Relic rolls <b>{sign(s.relicBonus)}</b> <span className="muted">({s.relicAbility})</span>
          </div>
        </section>

        <section className="sheet-pick">
          <h3>Class</h3>
          <Picker items={classes} current={s.classId} editable={editable} send={(id) => LN.send("CharacterClassRequested", { id })} />
          {cls && <p className="sheet-blurb">{cls.blurb}</p>}
          <h3>Origin</h3>
          <Picker items={origins} current={s.originId} editable={editable} send={(id) => LN.send("CharacterOriginRequested", { id })} />
          {origin && (
            <p className="sheet-blurb">
              {origin.blurb}{" "}
              <span className="muted">
                {origin.bonuses}
              </span>
            </p>
          )}
        </section>

        <section className="sheet-skills">
          <h3>Skills</h3>
          <ul>
            {s.skills.map((row) => {
              const [id, name, abbr, bonus, trained] = row.split("|");
              return (
                <li key={id} className={trained === "1" ? "trained" : ""}>
                  <span className="skill-dot" aria-hidden="true" />
                  <span className="skill-name">{name}</span>
                  <span className="skill-abbr">{abbr}</span>
                  <span className="skill-bonus">{sign(Number(bonus))}</span>
                </li>
              );
            })}
          </ul>
        </section>
      </div>
      {editable && <footer className="sheet-foot">Rolled from your lucky number. Moves lock when you leave the station.</footer>}
    </div>
  );
}

function Meter({ label, value, max, pct, tone }: { label: string; value: number; max: number; pct?: number; tone: string }) {
  const p = pct ?? (max > 0 ? (100 * value) / max : 0);
  return (
    <div className={`meter ${tone}`}>
      <div className="meter-head">
        <span>{label}</span>
        <b>
          {value}
          {tone !== "xp" && <small> / {max}</small>}
        </b>
      </div>
      <div className="meter-bar">
        <div className="meter-fill" style={{ width: `${Math.max(0, Math.min(100, p))}%` }} />
      </div>
    </div>
  );
}

function Picker({ items, current, editable, send }: { items: { id: string; name: string; blurb: string }[]; current: string; editable: boolean; send: (id: string) => void }) {
  return (
    <div className="sheet-picker">
      {items.map((it) => (
        <button
          key={it.id}
          type="button"
          className={`sheet-btn${it.id === current ? " active" : ""}`}
          disabled={!editable && it.id !== current}
          title={it.blurb}
          onClick={() => editable && send(it.id)}
        >
          {it.name}
        </button>
      ))}
    </div>
  );
}
