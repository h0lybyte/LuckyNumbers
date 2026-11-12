// The outfitter (Unity: Station/OutfitterPresenter, Outfitter state; content/upgrades.txtpb). Opened at the Docking
// Bay's outfitter console (E): your ship's stats up top, then two counters: the station's standard parts (several
// levels each) and the black market's exotics (one-offs that bend a rule). Click to buy the next level
// (UpgradeBuyRequested); E again, Esc, walking away or Close shuts it.
import { useEffect, useState } from "react";
import { LN, useLNEvent, useLNState } from "./ln";
import { CreditCounter } from "./counter";

type Item = {
  id: string;
  tier: "Common" | "Exotic";
  name: string;
  tag: string;
  description: string;
  effect: string;
  level: number;
  levels: number;
  cost: number;
};

function parse(row: string): Item {
  const [id, tier, name, tag, description, effect, level, levels, cost] = row.split("|");
  return { id, tier: tier === "Exotic" ? "Exotic" : "Common", name, tag, description, effect, level: Number(level), levels: Number(levels), cost: Number(cost) };
}

const STATS = [
  ["speed", "Speed"],
  ["agility", "Agility"],
  ["fuel", "Tank"],
  ["efficiency", "Efficiency"],
  ["toughness", "Toughness"],
  ["firepower", "Firepower"],
  ["shield", "Shield"],
  ["armor", "Armour"],
] as const;

export function OutfitterPanel() {
  const shop = useLNState("Outfitter");
  const credits = useLNState("PlayerVoyage")?.credits ?? 0;
  const debt = useLNState("DebtState");
  const open = shop?.open ?? false;
  const [flash, setFlash] = useState<string | null>(null);
  useLNEvent("UpgradeBought", (e) => {
    setFlash(e.id);
    setTimeout(() => setFlash((f) => (f === e.id ? null : f)), 900);
  });
  useEffect(() => {
    if (open && document.pointerLockElement) document.exitPointerLock();
  }, [open]);
  if (!open || !shop) return null;
  const items = shop.items.map(parse);
  const common = items.filter((i) => i.tier === "Common");
  const exotic = items.filter((i) => i.tier === "Exotic");
  return (
    <div className="outfitter ui" role="dialog" aria-label="Outfitter: ship upgrades">
      <header className="outfitter-head">
        <div>
          <div className="job-kicker">Outfitter // {shop.ship} // Esc closes</div>
          <h2>Ship upgrades</h2>
        </div>
        <CreditCounter value={credits} className="outfitter-credits" />
        <button type="button" className="sheet-btn" onClick={() => LN.send("OutfitterCloseRequested", {})}>
          Close
        </button>
      </header>
      {(debt?.level ?? 0) >= 2 && <p className="debt-note" role="status">{debt?.text}</p>}

      <div className="outfitter-stats" aria-label="Your ship now">
        {STATS.map(([key, label]) => (
          <StatBar key={key} label={label} value={shop[key]} />
        ))}
        <div className="outfitter-extra">
          <span>
            Guns <b>{shop.guns || "none"}</b>
          </span>
          <span>
            Payout <b className={shop.payout > 1.001 ? "gold" : ""}>x{shop.payout.toFixed(2)}</b>
          </span>
        </div>
      </div>

      <div className="outfitter-counters">
        <section className="outfitter-counter common">
          <h3>{shop.commonTitle}</h3>
          <p className="outfitter-blurb">{shop.commonBlurb}</p>
          <ul>
            {common.map((i) => (
              <UpgradeCard key={i.id} item={i} credits={credits} flash={flash === i.id} />
            ))}
          </ul>
        </section>
        <section className="outfitter-counter exotic">
          <h3>{shop.exoticTitle}</h3>
          <p className="outfitter-blurb">{shop.exoticBlurb}</p>
          <ul>
            {exotic.map((i) => (
              <UpgradeCard key={i.id} item={i} credits={credits} flash={flash === i.id} />
            ))}
          </ul>
        </section>
      </div>
    </div>
  );
}

/** One stat (a multiplier, 1 = baseline): a bar to x2, and the number. */
function StatBar({ label, value }: { label: string; value: number }) {
  const v = value > 0 ? value : 1;
  const tone = v > 1.005 ? " up" : v < 0.995 ? " down" : "";
  return (
    <div className={`outfitter-stat${tone}`}>
      <span className="label">{label}</span>
      <span className="bar">
        <span className="fill" style={{ width: `${Math.min(100, (100 * v) / 2)}%` }} />
        <span className="base" />
      </span>
      <span className="num">x{v.toFixed(2)}</span>
    </div>
  );
}

function UpgradeCard({ item, credits, flash }: { item: Item; credits: number; flash: boolean }) {
  const maxed = item.cost <= 0;
  const short = maxed ? 0 : item.cost - credits;
  const label = maxed
    ? item.levels > 1 ? "Maxed" : "Installed"
    : short > 0 ? `Need ${short.toLocaleString()} more` : `${item.level > 0 ? "Upgrade" : "Buy"} · ${item.cost.toLocaleString()} cr`;
  return (
    <li className={`upgrade${item.level > 0 ? " owned" : ""}${maxed ? " maxed" : ""}${flash ? " flash" : ""}`}>
      <div className="upgrade-main">
        <div className="upgrade-title">
          <b>{item.name}</b>
          {item.tag && <span className="upgrade-tag">{item.tag}</span>}
        </div>
        <div className="upgrade-effect">{item.effect}</div>
        <small>{item.description}</small>
        {item.levels > 1 && (
          <span className="upgrade-pips" title={`Level ${item.level} of ${item.levels}`}>
            {Array.from({ length: item.levels }, (_, n) => (
              <span key={n} className={n < item.level ? "on" : ""} />
            ))}
          </span>
        )}
      </div>
      <button
        type="button"
        className="upgrade-buy"
        disabled={maxed || short > 0}
        onClick={() => LN.send("UpgradeBuyRequested", { id: item.id })}
      >
        {label}
      </button>
    </li>
  );
}
