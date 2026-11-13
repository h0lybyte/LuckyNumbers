// Dispatch's job board (Unity: Station/Jobs, JobBoard state): a few seeded jobs, each on a planet with its danger
// and pay. Opened at your ship (E): click one or press 1-3 to take it and leave (JobAcceptRequested), Esc closes it.
// On the planet, the job you're on sits under the
// HUD (PlayerJob) and turns green once its goal is met.
import { useEffect } from "react";
import { LN, useLNState } from "./ln";

const KINDS: Record<string, string> = { Salvage: "Salvage", Retrieve: "Retrieve", Clear: "Clear", Uplink: "Uplink", Defend: "Defend" };

export function JobBoardPanel() {
  const board = useLNState("JobBoard");
  const open = board?.open ?? false;
  const offers = (board?.offers ?? []).map((o) => {
    const [kind, title, brief, planet, danger, pay] = o.split("|");
    return { kind, title, brief, planet, danger: Number(danger), pay: Number(pay) };
  });
  useEffect(() => {
    if (!open) return;
    if (document.pointerLockElement) document.exitPointerLock();
    const onKey = (e: KeyboardEvent) => {
      if (e.ctrlKey || e.metaKey || e.altKey || e.repeat) return;
      const m = /^(?:Digit|Numpad)([1-9])$/.exec(e.code);   // the key's place, whatever the layout
      const n = m ? Number(m[1]) : NaN;
      if (!Number.isInteger(n) || n < 1 || n > offers.length) return;
      LN.send("JobAcceptRequested", { index: n - 1 });
      e.preventDefault();
      e.stopImmediatePropagation();
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [open, offers.length]);
  if (!open) return null;
  return (
    <div className="job-board ui" role="dialog" aria-label="Dispatch job board">
      <div className="job-kicker">Dispatch // pick a job to leave on // Esc closes</div>
      <ol>
        {offers.map((o, i) => (
          <li key={i}>
            <button type="button" className="job-offer" onClick={() => LN.send("JobAcceptRequested", { index: i })}>
              <span className="job-key">
                <kbd>{i + 1}</kbd>
              </span>
              <span className="job-main">
                <span className="job-kind">{KINDS[o.kind] ?? o.kind}</span>
                <b>{o.title}</b>
                <small>{o.brief}</small>
              </span>
              <span className="job-meta">
                <span className="job-planet">{o.planet}</span>
                <span className="job-danger" title={`Danger ${o.danger} of 5`}>
                  {"◆".repeat(o.danger)}
                  <span className="dim">{"◆".repeat(Math.max(0, 5 - o.danger))}</span>
                </span>
                <b className="job-pay">{o.pay.toLocaleString()} cr</b>
              </span>
            </button>
          </li>
        ))}
      </ol>
    </div>
  );
}

/** On the planet: the job you're here for, green once its goal is met, red once it can't be (a Defend's beacon lost). */
export function JobLine() {
  const job = useLNState("PlayerJob");
  if (!job?.active) return null;
  const goal = (job.kind === "Clear" || job.kind === "Defend") && job.goal > 0
    ? ` (${job.count}/${job.goal}${job.kind === "Defend" ? " waves" : ""})` : "";
  const text = job.failed ? "Job failed: fly back to the station"
    : job.done ? (job.kind === "Uplink" ? "Dock at the station to get paid" : "Fly back to the station to get paid")
    : job.objective + goal;
  return (
    <div className={`job-line${job.done ? " done" : job.failed ? " failed" : ""}`}>
      <span className="job-kind">{job.failed ? "Failed" : job.done ? "Done" : "Job"}</span> {text}
    </div>
  );
}
