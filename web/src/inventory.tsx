// The Inventory (I) and Equipment tabs. The pack is a grid (10 x 6) that items fit into by shape: drag an item to move
// it, R to turn it while dragging (or the selected one in place), click for details, double-click to use. On the
// Equipment tab the slots sit beside the grid: drag gear onto its slot to wear it (its bonuses go on the sheet), or a
// worn item back onto the grid to take it off. Nothing is drawn in the world: you're first-person arms. The ghost shows
// green where an item fits (checked here for feedback; Unity checks again: Core/InventoryPresenter).
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { LN, useLNState } from "./ln";

const KINDS = ["", "Gear", "Consumable", "Relic", "Tool", "Weapon", "Armor"];
// EquipSlot order (proto): 1 head, 2 body, 3 hands, 4 weapon, 5 tool.
const SLOTS = [
  { id: 1, name: "Head" },
  { id: 2, name: "Body" },
  { id: 3, name: "Hands" },
  { id: 4, name: "Weapon" },
  { id: 5, name: "Tool" },
];
const slotName = (id: number) => SLOTS.find((s) => s.id === id)?.name ?? "";

type Shape = boolean[][];

interface Item {
  uid: number;
  id: string;
  name: string;
  kind: number;
  x: number;
  y: number;
  rot: number;
  shape: Shape;
  bulk: number;
  value: number;
  color: string;
  description: string;
  canUse: boolean;
  canDrop: boolean;
  canEquip: boolean;
  slot: number;
  equipped: number;
  bonuses: string;
  count: number;
}

const parse = (s: string): Shape => s.split("/").map((row) => [...row].map((ch) => ch === "#"));

/** One quarter turn clockwise (same as InventoryPresenter.Rotate). */
const turn = (cells: Shape): Shape => {
  const h = cells.length;
  const w = cells[0]?.length ?? 0;
  const out: Shape = Array.from({ length: w }, () => Array<boolean>(h).fill(false));
  for (let r = 0; r < h; r++) for (let c = 0; c < w; c++) out[c][h - 1 - r] = cells[r][c];
  return out;
};

/** What each kind of item is (PlayerInventory.kinds, sent once), by index. */
type Kind = Omit<Item, "uid" | "x" | "y" | "rot" | "equipped" | "count">;

function readKinds(rows: string[]): Kind[] {
  return rows.map((row) => {
    const [id, name, kind, shape, bulk, value, color, description, actions, slot, bonuses] = row.split("|");
    const a = Number(actions);
    return {
      id, name, kind: Number(kind), shape: parse(shape), bulk: Number(bulk), value: Number(value), color, description,
      canUse: (a & 1) !== 0, canDrop: (a & 2) !== 0, canEquip: (a & 4) !== 0,
      slot: Number(slot ?? 0), bonuses: bonuses ?? "",
    };
  });
}

/** The pack's rows ("uid|kind|x|y|rot|equipped|count") joined to their kinds; shapes turned `rot` times here. */
function readItems(rows: string[], kinds: Kind[]): Item[] {
  return rows.flatMap((row) => {
    const [uid, kind, x, y, rot, equipped, count] = row.split("|").map(Number);
    const k = kinds[kind];
    if (!k) return [];   // kinds not here yet (or content out of step): skip until they are
    let shape = k.shape;
    for (let t = 0; t < rot; t++) shape = turn(shape);
    return [{ ...k, uid, x, y, rot, shape, equipped: equipped || 0, count: count || 1 }];
  });
}

/** First filled cell, row-major (where a label sits, and where a slot drag grabs). */
function firstCell(shape: Shape): [number, number] {
  for (let r = 0; r < shape.length; r++) {
    const c = shape[r].findIndex(Boolean);
    if (c >= 0) return [r, c];
  }
  return [0, 0];
}

interface Drag {
  uid: number;
  shape: Shape;
  turns: number;
  grabR: number;
  grabC: number;
  x: number;
  y: number;
  inGrid: boolean;
  overSlot: number;
  startX: number;
  startY: number;
  moved: boolean;
}

export function InventoryTab() {
  return <PackBoard equipment={false} />;
}

export function EquipmentTab() {
  return <PackBoard equipment />;
}

function PackBoard({ equipment }: { equipment: boolean }) {
  const inv = useLNState("PlayerInventory");
  const kinds = useMemo(() => readKinds(inv?.kinds ?? []), [inv?.kinds]);
  const items = useMemo(() => readItems(inv?.items ?? [], kinds), [inv?.items, kinds]);
  const packed = useMemo(() => items.filter((i) => i.equipped === 0), [items]);
  const [selected, setSelected] = useState<number | null>(null);
  const [drag, setDrag] = useState<Drag | null>(null);
  const dragRef = useRef<Drag | null>(null);
  dragRef.current = drag;
  const boardRef = useRef<HTMLDivElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);
  const width = inv?.width ?? 10;
  const height = inv?.height ?? 6;

  // Grid cells taken, by uid (for the fit check while dragging). Worn items aren't in the grid.
  const occupied = useMemo(() => {
    const grid: number[][] = Array.from({ length: height }, () => Array<number>(width).fill(0));
    for (const it of packed)
      it.shape.forEach((row, r) => row.forEach((on, c) => {
        if (on && grid[it.y + r] && it.x + c < width) grid[it.y + r][it.x + c] = it.uid;
      }));
    return grid;
  }, [packed, width, height]);

  const fits = (shape: Shape, x: number, y: number, uid: number) => {
    for (let r = 0; r < shape.length; r++)
      for (let c = 0; c < shape[r].length; c++) {
        if (!shape[r][c]) continue;
        const gx = x + c;
        const gy = y + r;
        if (gx < 0 || gy < 0 || gx >= width || gy >= height) return false;
        const taken = occupied[gy][gx];
        if (taken !== 0 && taken !== uid) return false;
      }
    return true;
  };

  // R turns the dragged item (or the selected one, in place). While these tabs are open R never reaches the game
  // (it would use a relic).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.code !== "KeyR" || e.repeat) return;
      e.stopImmediatePropagation();
      e.preventDefault();
      const d = dragRef.current;
      if (d) {
        const h = d.shape.length;
        // The grabbed cell turns with the shape, so the item pivots under the pointer.
        setDrag({ ...d, shape: turn(d.shape), turns: d.turns + 1, grabR: d.grabC, grabC: h - 1 - d.grabR,
                  x: d.x + d.grabC - (h - 1 - d.grabR), y: d.y + d.grabR - d.grabC, moved: true });
        return;
      }
      const it = items.find((i) => i.uid === selected);
      if (it && it.equipped === 0) LN.send("InventoryMoveRequested", { uid: it.uid, x: it.x, y: it.y, rot: (it.rot + 1) % 4 });
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [items, selected]);

  const startDrag = (e: ReactPointerEvent, it: Item, r: number, c: number) => {
    e.preventDefault();
    e.stopPropagation();
    setSelected(it.uid);
    boardRef.current?.setPointerCapture(e.pointerId);
    setDrag({ uid: it.uid, shape: it.shape, turns: 0, grabR: r, grabC: c, x: it.x, y: it.y, inGrid: it.equipped === 0,
              overSlot: 0, startX: e.clientX, startY: e.clientY, moved: false });
  };

  const onMove = (e: ReactPointerEvent) => {
    const d = dragRef.current;
    if (!d) return;
    const far = Math.abs(e.clientX - d.startX) + Math.abs(e.clientY - d.startY) > 4;
    const box = gridRef.current!.getBoundingClientRect();
    const c = Math.floor(((e.clientX - box.left) / box.width) * width);
    const r = Math.floor(((e.clientY - box.top) / box.height) * height);
    const inGrid = e.clientX >= box.left && e.clientX <= box.right && e.clientY >= box.top && e.clientY <= box.bottom;
    const slotEl = (document.elementFromPoint(e.clientX, e.clientY) as HTMLElement | null)?.closest<HTMLElement>("[data-slot]");
    setDrag({ ...d, x: c - d.grabC, y: r - d.grabR, inGrid, overSlot: slotEl ? Number(slotEl.dataset.slot) : 0, moved: d.moved || far });
  };

  const onUp = () => {
    const d = dragRef.current;
    setDrag(null);
    if (!d || !d.moved) return;
    const it = items.find((i) => i.uid === d.uid);
    if (!it) return;
    if (d.overSlot) {
      if (d.overSlot === it.slot && it.equipped !== it.slot) LN.send("InventoryEquipRequested", { uid: it.uid });
      return;
    }
    if (!d.inGrid || !fits(d.shape, d.x, d.y, d.uid)) return;
    if (it.equipped === 0 && d.x === it.x && d.y === it.y && d.turns % 4 === 0) return;
    LN.send("InventoryMoveRequested", { uid: d.uid, x: d.x, y: d.y, rot: (it.rot + d.turns) % 4 });
  };

  const sel = items.find((i) => i.uid === selected) ?? null;
  const dragged = drag ? items.find((i) => i.uid === drag.uid) : undefined;

  return (
    <div
      ref={boardRef}
      className={`pack${equipment ? " pack--equipment" : ""}`}
      onPointerMove={onMove}
      onPointerUp={onUp}
      onPointerCancel={() => setDrag(null)}
    >
      {equipment && (
        <div className="gear-slots">
          <h3>Worn</h3>
          {SLOTS.map((slot) => {
            const it = items.find((i) => i.equipped === slot.id);
            const target = drag?.moved && dragged?.slot === slot.id;
            return (
              <div
                key={slot.id}
                data-slot={slot.id}
                className={`gear-slot${it ? " filled" : ""}${target ? " target" : ""}${target && drag?.overSlot === slot.id ? " over" : ""}${it && selected === it.uid ? " selected" : ""}`}
                style={it ? { ["--c" as string]: it.color } : undefined}
                onPointerDown={(e) => {
                  if (!it) return;
                  const [r, c] = firstCell(it.shape);
                  startDrag(e, it, r, c);
                }}
              >
                <span className="gear-slot-name">{slot.name}</span>
                {it ? (
                  <>
                    <span className="gear-slot-item">{it.name}</span>
                    {it.bonuses && <span className="gear-slot-bonus">{it.bonuses}</span>}
                  </>
                ) : (
                  <span className="gear-slot-empty">empty</span>
                )}
              </div>
            );
          })}
        </div>
      )}

      <div className="pack-main">
        <div
          ref={gridRef}
          className="pack-grid"
          style={{ ["--w" as string]: width, ["--h" as string]: height }}
          onPointerDown={(e) => { if (e.target === e.currentTarget) setSelected(null); }}
        >
          {packed.map((it) => (
            <ItemView
              key={it.uid}
              item={it}
              dragging={drag?.uid === it.uid && drag.moved}
              selected={selected === it.uid}
              onGrab={startDrag}
              onUse={() => it.canUse && LN.send("InventoryUseRequested", { uid: it.uid })}
            />
          ))}
          {drag?.moved && drag.inGrid && !drag.overSlot && (
            <Ghost shape={drag.shape} x={drag.x} y={drag.y} ok={fits(drag.shape, drag.x, drag.y, drag.uid)} color={dragged?.color ?? "#00e5ff"} />
          )}
        </div>
        <div className="pack-hint">
          Drag to move &middot; <kbd>R</kbd> rotate &middot; double-click to use{equipment ? " · drag gear onto its slot" : ""}
        </div>
      </div>

      <aside className="pack-side">
        {equipment ? <GearTotals rows={inv?.gearBonuses ?? []} /> : <Load />}
        <h3>Item</h3>
        {sel ? <Detail item={sel} onDropped={() => setSelected(null)} /> : <p className="muted">Select an item.</p>}
        {inv?.message && <div className="pack-message">{inv.message}</div>}
      </aside>
    </div>
  );
}

function Load() {
  const inv = useLNState("PlayerInventory");
  const LOAD = ["Light", "Encumbered", "Overloaded"];
  const pct = (v: number) => `${Math.min(100, (100 * v) / Math.max(0.01, inv?.bulkMax ?? 1))}%`;
  return (
    <>
      <h3>Load</h3>
      <div className={`pack-load load-${inv?.load ?? 0}`}>
        <div className="pack-load-head">
          <span>{LOAD[inv?.load ?? 0]}</span>
          <b>
            {(inv?.bulk ?? 0).toFixed(1)} <small>/ {(inv?.bulkLimit ?? 0).toFixed(1)} bulk</small>
          </b>
        </div>
        <div className="pack-load-bar">
          <div className="pack-load-fill" style={{ width: pct(inv?.bulk ?? 0) }} />
          <div className="pack-load-mark" style={{ left: pct(inv?.bulkLimit ?? 0) }} />
        </div>
        <div className="pack-load-sub">
          {(inv?.speed ?? 1) < 1 ? `Walking at ${Math.round((inv?.speed ?? 1) * 100)}%` : "Full speed"} &middot; limit from STR
        </div>
      </div>
      <div className="pack-value">
        Carrying <b>{(inv?.value ?? 0).toLocaleString()}</b> cr
      </div>
    </>
  );
}

function GearTotals({ rows }: { rows: string[] }) {
  return (
    <>
      <h3>Gear bonuses</h3>
      {rows.length === 0 ? (
        <p className="muted">Nothing worn.</p>
      ) : (
        <ul className="gear-totals">
          {rows.map((row) => {
            const [label, value] = row.split("|");
            return (
              <li key={label} className={value.startsWith("-") ? "down" : ""}>
                <span>{label}</span>
                <b>{value}</b>
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}

function Detail({ item, onDropped }: { item: Item; onDropped: () => void }) {
  const worn = item.equipped !== 0;
  const inv = useLNState("PlayerInventory");
  const onTitle = useLNState("FlowState")?.scene === "Title";
  // Drop lays it at your feet where there's a world; while setting up (title) it just leaves the kit.
  const dropLabel = inv?.canDrop ? "Drop" : onTitle ? "Remove" : null;
  return (
    <div className="pack-detail" style={{ ["--c" as string]: item.color }}>
      <div className="pack-detail-name">
        {item.name}
        {item.count > 1 && <span className="pack-detail-count"> &times;{item.count}</span>}
      </div>
      <div className="pack-detail-kind">
        {KINDS[item.kind] ?? ""}
        {item.slot && slotName(item.slot) !== KINDS[item.kind] ? ` · ${slotName(item.slot)} slot` : ""} &middot; {item.bulk} bulk{item.value > 0 ? ` · ${item.value.toLocaleString()} cr` : ""}
        {worn ? " · worn" : ""}
      </div>
      {item.bonuses && <div className="pack-detail-bonus">{item.bonuses}</div>}
      <p>{item.description}</p>
      <div className="pack-actions">
        {item.canUse && (
          <button type="button" className="sheet-btn" onClick={() => LN.send("InventoryUseRequested", { uid: item.uid })}>
            Use
          </button>
        )}
        {item.canEquip && !worn && (
          <button type="button" className="sheet-btn" onClick={() => LN.send("InventoryEquipRequested", { uid: item.uid })}>
            Equip
          </button>
        )}
        {worn && (
          <button type="button" className="sheet-btn" onClick={() => LN.send("InventoryUnequipRequested", { slot: item.equipped })}>
            Take off
          </button>
        )}
        {!worn && (
          <button type="button" className="sheet-btn"
                  onClick={() => LN.send("InventoryMoveRequested", { uid: item.uid, x: item.x, y: item.y, rot: (item.rot + 1) % 4 })}>
            Rotate <kbd>R</kbd>
          </button>
        )}
        {item.canDrop && dropLabel && (
          <button type="button" className="sheet-btn danger" onClick={() => { LN.send("InventoryDropRequested", { uid: item.uid }); onDropped(); }}
                  title={dropLabel === "Drop" ? "Lay it at your feet (E picks it back up)" : "Take it out of your kit"}>
            {dropLabel}
            {item.count > 1 ? ` ×${item.count}` : ""}
          </button>
        )}
      </div>
    </div>
  );
}

function ItemView({ item, dragging, selected, onGrab, onUse }: {
  item: Item; dragging: boolean; selected: boolean;
  onGrab: (e: ReactPointerEvent, it: Item, r: number, c: number) => void; onUse: () => void;
}) {
  const [labelR, labelC] = firstCell(item.shape);
  return (
    <div
      className={`pack-item${dragging ? " dragging" : ""}${selected ? " selected" : ""}`}
      style={{ ["--x" as string]: item.x, ["--y" as string]: item.y, ["--c" as string]: item.color }}
      title={`${item.name} (${KINDS[item.kind] ?? ""})`}
    >
      {item.shape.map((row, r) =>
        row.map((on, c) =>
          on ? (
            <div
              key={`${r},${c}`}
              className="pack-cell"
              style={{ ["--r" as string]: r, ["--cc" as string]: c }}
              onPointerDown={(e) => onGrab(e, item, r, c)}
              onDoubleClick={onUse}
            />
          ) : null,
        ),
      )}
      <span className="pack-label" style={{ ["--r" as string]: labelR, ["--cc" as string]: labelC }}>
        {item.name}
      </span>
      {item.count > 1 && (
        <span className="pack-count" style={{ ["--r" as string]: labelR, ["--cc" as string]: labelC }}>
          &times;{item.count}
        </span>
      )}
    </div>
  );
}

function Ghost({ shape, x, y, ok, color }: { shape: Shape; x: number; y: number; ok: boolean; color: string }) {
  return (
    <div className={`pack-ghost${ok ? " ok" : " bad"}`} style={{ ["--x" as string]: x, ["--y" as string]: y, ["--c" as string]: color }}>
      {shape.map((row, r) => row.map((on, c) => (on ? <div key={`${r},${c}`} className="pack-cell" style={{ ["--r" as string]: r, ["--cc" as string]: c }} /> : null)))}
    </div>
  );
}
