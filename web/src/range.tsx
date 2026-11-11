// The shooting range: every shot at a board or crate is an attack roll (d20 + ranged mod vs the target's AC).
// Unity rolls (RangePresenter) and decides the hit; this only shows it, quick enough to keep up with the trigger.
import { D20Panel, useDiceRoll } from "./relics";
import type { AttackRolled } from "./ln";

export function RangeHud() {
  const { event: e, face, settled } = useDiceRoll<AttackRolled>("AttackRolled", 380, 2600);
  if (!e) return null;
  const tone = e.roll === 1 ? "fumble" : e.roll === 20 ? "crit" : e.hit ? "good" : "miss";
  return (
    <D20Panel face={face} settled={settled} tone={tone} heading={e.target}>
      <div className="d20-sum">
        d20 <b>{e.roll}</b>
        {e.bonus !== 0 && <> {e.bonus > 0 ? "+" : "\u2212"} DEX <b>{Math.abs(e.bonus)}</b></>} = <b>{e.total}</b> vs AC <b>{e.ac}</b>
      </div>
      <div className="d20-title">{e.title}</div>
      <div className="d20-line">{e.text}</div>
    </D20Panel>
  );
}
