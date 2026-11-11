// A dice-only standoff on the planet (Unity: Planet/CombatPresenter.Encounters): a monster that hasn't seen you
// (finds open themselves, no card: their roll shows as a toast). Time stands still while the card is up. Pick a choice (click, or 1-5):
// each is d20 + one of your skills against a DC, shown with your bonus. Then the roll and what happened; Continue
// (Enter) lets time run again.
import { useEffect } from "react";
import { LN, useLNState } from "./ln";

export function EncounterCard() {
  const e = useLNState("EncounterState");
  const open = e?.open ?? false;
  const rolled = e?.rolled ?? false;
  const options = (e?.options ?? []).map((o) => {
    const [id, label, skill, bonus, dc] = o.split("|");
    return { id, label, skill, bonus: Number(bonus), dc: Number(dc) };
  });
  const send = (option: string) => LN.send("EncounterChoice", { option });
  useEffect(() => {
    if (!open) return;
    if (document.pointerLockElement) document.exitPointerLock();
    const onKey = (k: KeyboardEvent) => {
      if (k.ctrlKey || k.metaKey || k.altKey || k.repeat) return;
      if (rolled) {
        if (k.code !== "Enter" && k.code !== "Space" && k.code !== "KeyE") return;
        send("continue");
      } else {
        const m = /^(?:Digit|Numpad)([1-9])$/.exec(k.code);
        const n = m ? Number(m[1]) : 0;
        if (n < 1 || n > options.length) return;
        send(options[n - 1].id);
      }
      k.preventDefault();
      k.stopImmediatePropagation();
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [open, rolled, options.length]);
  if (!open || !e) return null;
  // Your chance on each: a d20 needs (dc - bonus) or more; natural 20s always count, 1s never do.
  const chance = (bonus: number, dc: number) => Math.round(100 * Math.min(19, Math.max(1, 21 - (dc - bonus))) / 20);
  return (
    <div className={`encounter ui ${e.kind}`} role="dialog" aria-label={e.title}>
      <div className="encounter-kicker">Encounter</div>
      <h2>{e.title}</h2>
      <p>{e.text}</p>
      {!rolled ? (
        <ol className="encounter-options">
          {options.map((o, i) => (
            <li key={o.id}>
              <button type="button" onClick={() => send(o.id)}>
                <kbd>{i + 1}</kbd>
                <b>{o.label}</b>
                {o.skill ? (
                  <span className="encounter-check">
                    {o.skill} {o.bonus >= 0 ? "+" : ""}
                    {o.bonus} vs DC {o.dc} <small>{chance(o.bonus, o.dc)}%</small>
                  </span>
                ) : (
                  <span className="encounter-check muted">no roll</span>
                )}
              </button>
            </li>
          ))}
        </ol>
      ) : (
        <div className={`encounter-result ${e.success ? "good" : "bad"}`}>
          {e.skill && (
            <div className="encounter-roll">
              <span className="die">d20 {e.d20}</span> {e.bonus >= 0 ? "+" : ""}
              {e.bonus} {e.skill} = <b>{e.total}</b> vs DC {e.dc}{" "}
              <strong>{e.d20 === 20 ? "NATURAL 20" : e.d20 === 1 ? "NATURAL 1" : e.success ? "SUCCESS" : "FAILURE"}</strong>
            </div>
          )}
          <p>{e.result}</p>
          <button type="button" className="sheet-ready" onClick={() => send("continue")}>
            Continue <kbd>Enter</kbd>
          </button>
        </div>
      )}
    </div>
  );
}
