// Space combat outside the station (Unity: Station/Dogfight, DogfightState). Your three layers (shield, armour,
// hull), the rogue you're on, the satellite upload on a job, the roll feed, and the memory-restore card when the
// ship goes. Debt (DebtState) shows wherever you are; game over is a full card that reloads to the title.
import { useEffect, useState } from "react";
import { useLNEvent, useLNState } from "./ln";
import { CombatFeed } from "./combat";

export function DogfightHud() {
  const d = useLNState("DogfightState");
  if (!d?.active) return null;
  if (d.destroyed)
    return (
      <div className="dogfight-down" role="alert">
        <b>{d.destroyedText || "Hull breach."}</b>
        <div className="dogfight-upload" aria-hidden="true"><div /></div>
      </div>
    );
  const [name, hull, hullMax, shield, shieldMax, metres, mode] = (d.target ?? "").split("|");
  const pct = (v: number, max: number) => `${Math.max(0, Math.min(100, (100 * v) / Math.max(1, max)))}%`;
  return (
    <>
      <div className="dogfight-self" aria-label={`Shield ${Math.round(d.shield)} of ${Math.round(d.shieldMax)}, hull ${Math.round(d.hull)} of ${Math.round(d.hullMax)}`}>
        <div className={`dogfight-row shield${d.shieldHit ? " hit" : ""}${d.shield <= 0 ? " down" : ""}`}>
          <span>{d.shield <= 0 ? "SHIELD DOWN" : "SHIELD"}</span>
          <div className="dogfight-bar"><div style={{ width: pct(d.shield, d.shieldMax) }} /></div>
          <em>{Math.round(d.shield)}</em>
        </div>
        <div className={`dogfight-row hull${d.hullHit ? " hit" : ""}${d.hull < d.hullMax * 0.3 ? " low" : ""}`}>
          <span>HULL</span>
          <div className="dogfight-bar"><div style={{ width: pct(d.hull, d.hullMax) }} /></div>
          <em>{Math.round(d.hull)}</em>
        </div>
        <div className="dogfight-stats">
          ARMOUR {d.armor} · EVADE {d.evasion} · GUNNERY +{d.gunnery}
          {d.hostiles > 0 && <b> · {d.hostiles} HOSTILE{d.hostiles === 1 ? "" : "S"}</b>}
        </div>
      </div>
      {d.target && (
        <div className="combat-target dogfight-target" aria-label={`${name}: hull ${hull} of ${hullMax}`}>
          <span>{name} · {metres} m{mode && <b className="dogfight-mode"> · {mode.replace(/([a-z])([A-Z])/g, "$1 $2").toUpperCase()}</b>}</span>
          {Number(shieldMax) > 0 && (
            <div className="combat-target-bar shield"><div style={{ width: pct(Number(shield), Number(shieldMax)) }} /></div>
          )}
          <div className="combat-target-bar"><div style={{ width: pct(Number(hull), Number(hullMax)) }} /></div>
        </div>
      )}
      {d.uplinkJob && (
        <div className={`dogfight-uplink${d.uplinkDone ? " done" : d.inRange ? " live" : ""}`}>
          <div className="dogfight-uplink-head">
            <span>{d.objective}</span>
            <em>{d.uplinkDone ? "DONE" : `${Math.round(d.uplink * 100)}%`}</em>
          </div>
          <div className="dogfight-bar"><div style={{ width: pct(d.uplink, 1) }} /></div>
          <small>
            {d.tier && <b className="dogfight-tier">{d.tier.toUpperCase()} · </b>}
            Satellite {Math.round(d.satelliteDistance)} m{d.waves > 0 && ` · wave ${d.wave}/${d.waves}`}
          </small>
        </div>
      )}
      {d.alert && (
        <div className="dogfight-alert" role="alert">
          {d.alert}
        </div>
      )}
      <CombatFeed />
    </>
  );
}

/** In debt: a chip with your balance (and the collections warning); hidden at 0 or above. */
export function DebtChip() {
  const debt = useLNState("DebtState");
  const credits = useLNState("PlayerVoyage")?.credits ?? 0;
  const scene = useLNState("FlowState")?.scene;
  if (!debt || debt.level < 1 || debt.gameOverShown || scene === "Title") return null;
  return (
    <div className={`debt-chip${debt.level >= 2 ? " collections" : ""}`} role="status" title={`Game over below ${debt.gameOver.toLocaleString()} cr`}>
      <b>{credits.toLocaleString()} cr</b> {debt.text}
    </div>
  );
}

/** Game over: the creditors took the chip. The save slot is gone; the button reloads to the title. */
export function GameOverCard() {
  const debt = useLNState("DebtState");
  const [ended, setEnded] = useState(false);
  useLNEvent("RunEnded", () => setEnded(true));
  const shown = ended || (debt?.gameOverShown ?? false);
  useEffect(() => {
    if (shown && document.pointerLockElement) document.exitPointerLock();
  }, [shown]);
  if (!shown) return null;
  return (
    <div className="game-over ui" role="alertdialog" aria-labelledby="game-over-title">
      <div className="game-over-card">
        <div className="job-kicker">Game over</div>
        <h2 id="game-over-title">{debt?.gameOverTitle || "Repossessed"}</h2>
        <p>{debt?.gameOverText}</p>
        <button type="button" className="sheet-btn" onClick={() => location.reload()} autoFocus>
          Back to the title
        </button>
      </div>
    </div>
  );
}
