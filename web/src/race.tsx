// Circuit racing round the station (Unity: Station/Race, RaceState). Lap, place, time and the next gate up top; the
// standings and the cube feed down the right; your shield charges and running effects; a big banner for the
// countdown, laps and hits; a card for each cube you roll (the d20, the bonus, what it did); the finish card.
import { useEffect, useState } from "react";
import { useLNState } from "./ln";

const time = (s: number) => {
  const m = Math.floor(s / 60);
  const r = s - m * 60;
  return `${m}:${r < 10 ? "0" : ""}${r.toFixed(1)}`;
};

const ordinal = (n: number) => {
  const t = n % 100;
  if (t >= 11 && t <= 13) return `${n}th`;
  return `${n}${["th", "st", "nd", "rd"][n % 10] ?? "th"}`;
};

export function RaceHud() {
  const r = useLNState("RaceState");
  if (!r?.active) return null;
  return (
    <>
      <div className="race-top" aria-label={`Lap ${r.lap} of ${r.laps}, ${ordinal(r.place)} of ${r.racers}`}>
        <div className="race-cell">
          <span>LAP</span>
          <b>
            {r.lap}<small>/{r.laps}</small>
          </b>
        </div>
        <div className="race-cell place">
          <span>POS</span>
          <b>
            {ordinal(r.place)}<small>/{r.racers}</small>
          </b>
        </div>
        <div className="race-cell">
          <span>TIME</span>
          <b>{time(r.time)}</b>
          {r.bestLap > 0 && <small>best {time(r.bestLap)}</small>}
        </div>
        <div className="race-cell">
          <span>GATE</span>
          <b>{Math.round(r.nextGate)}<small> m</small></b>
        </div>
        <div className="race-cell">
          <span>SHIELD</span>
          <b className="race-pips" aria-label={`${r.shields} shield charges`}>
            {r.shields > 0 ? "◆".repeat(r.shields) : <small>none</small>}
          </b>
        </div>
        {r.practice && <div className="race-practice">PRACTICE · no pay</div>}
        {r.effects && <div className="race-effects">{r.effects}</div>}
      </div>
      {r.countdown <= 0 && !r.finished && r.gateOff > 25 && (
        <div
          className={`race-arrow${r.gateOff > 100 ? " behind" : ""}`}
          style={{ transform: `rotate(${r.gateAngle}deg)` }}
          aria-label={`Next gate ${Math.round(r.gateOff)} degrees off your nose`}
        />
      )}
      <Standings standings={r.standings} />
      <Feed feed={r.feed} />
      {r.banner && (
        <div key={r.banner} className={`race-banner${r.countdown > 0 ? " count" : ""}`} role="status">
          {r.banner}
        </div>
      )}
      <RollCard roll={r.roll} seq={r.rollSeq} />
      {r.finished && (
        <div className="race-result" role="status">
          <b>{r.result}</b>
          <small>E: back to the station</small>
        </div>
      )}
    </>
  );
}

function Standings({ standings }: { standings: string[] }) {
  return (
    <ol className="race-standings" aria-label="Standings">
      {standings.map((s) => {
        const [place, name, lap, you, done] = s.split("|");
        return (
          <li key={name} className={`${you === "1" ? "you" : ""}${done === "1" ? " done" : done === "2" ? " dnf" : ""}`}>
            <em>{place}</em>
            <span>{name}</span>
            <small>{done === "1" ? "FIN" : done === "2" ? "DNF" : `L${lap}`}</small>
          </li>
        );
      })}
    </ol>
  );
}

function Feed({ feed }: { feed: string[] }) {
  if (feed.length === 0) return null;
  return (
    <ul className="race-feed" aria-label="Cube rolls">
      {feed.map((f, i) => {
        const [who, cube, hex, d20, bonus, total, effect, you] = f.split("|");
        return (
          <li key={`${i}-${f}`} className={you === "1" ? "you" : ""}>
            <i style={{ background: `#${hex}` }} aria-hidden="true" />
            <span>{who}</span>
            <small>
              {cube} {d20 === "20" ? "NAT 20" : d20 === "1" ? "NAT 1" : `${d20}+${bonus}=${total}`}
            </small>
            <b>{effect}</b>
          </li>
        );
      })}
    </ul>
  );
}

/** Your latest cube roll: shown for a few seconds each time you take one (rollSeq). */
function RollCard({ roll, seq }: { roll: string; seq: number }) {
  const [shown, setShown] = useState(0);
  useEffect(() => {
    if (seq <= 0) return;
    setShown(seq);
    const t = window.setTimeout(() => setShown(0), 3200);
    return () => window.clearTimeout(t);
  }, [seq]);
  if (!roll || shown === 0) return null;
  const [cube, hex, d20, bonus, total, effect, said] = roll.split("|");
  const nat = d20 === "20" ? " nat20" : d20 === "1" ? " nat1" : "";
  return (
    <div key={shown} className={`race-roll${nat}`} style={{ ["--cube" as string]: `#${hex}` }} role="status">
      <div className="race-roll-die" aria-hidden="true">{d20}</div>
      <div className="race-roll-body">
        <small>{cube} cube · d20 {d20} + {bonus} = {total}</small>
        <b>{effect}</b>
        <span>{said}</span>
      </div>
    </div>
  );
}
