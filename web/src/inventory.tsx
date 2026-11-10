// The Inventory tab (I): the pack as a grid (10 x 6) that items fit into by shape. Drag an item to move it, R to
// turn it while dragging (or the selected one in place), click for details, double-click to use. The ghost shows
// green where it fits and red where it doesn't (checked here for feedback; Unity checks again: Core/InventoryPresenter).
import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { LN, useLNState } from "./ln";

const KINDS = ["", "Gear", "Consumable", "Relic", "Tool", "Weapon", "Armor"];

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

function readItems(rows: string[]): Item[] {
  return rows.map((row) => {
    const [uid, id, name, kind, x, y, rot, shape, bulk, value, color, description, actions] = row.split("|");
    const a = Number(actions);
    return {
      uid: Number(uid), id, name, kind: Number(kind), x: Number(x), y: Number(y), rot: Number(rot),
      shape: parse(shape), bulk: Number(bulk), value: Number(value), color, description,
      canUse: (a & 1) !== 0, canDrop: (a & 2) !== 0,
    };
  });
}

interface Drag {
  uid: number;
  shape: Shape;
  turns: number;
  grabR: number;
  grabC: number;
  x: number;
  y: number;
  startX: number;
  startY: number;
  moved: boolean;
}

export function InventoryTab() {
  const inv = useLNState("PlayerInventory");
  const items = useMemo(() => readItems(inv?.items ?? []), [inv?.items]);
  const [selected, setSelected] = useState<number | null>(null);
  const [drag, setDrag] = useState<Drag | null>(null);
  const dragRef = useRef<Drag | null>(null);
  dragRef.current = drag;
  const gridRef = useRef<HTMLDivElement>(null);
  const width = inv?.width ?? 10;
  const height = inv?.height ?? 6;

  // Cells taken, by uid (for the fit check while dragging).
  const occupied = useMemo(() => {
    const grid: number[][] = Array.from({ length: height }, () => Array<number>(width).fill(0));
    for (const it of items)
      it.shape.forEach((row, r) => row.forEach((on, c) => {
        if (on && grid[it.y + r] && it.x + c < width) grid[it.y + r][it.x + c] = it.uid;
      }));
    return grid;
  }, [items, width, height]);

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

  // R turns the dragged item (or the selected one, in place). While this tab is open R never reaches the game
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
      if (it) LN.send("InventoryMoveRequested", { uid: it.uid, x: it.x, y: it.y, rot: (it.rot + 1) % 4 });
    };
    window.addEventListener("keydown", onKey, { capture: true });
    return () => window.removeEventListener("keydown", onKey, { capture: true });
  }, [items, selected]);

  const cellAt = (e: { clientX: number; clientY: number }) => {
    const box = gridRef.current!.getBoundingClientRect();
    return { c: Math.floor(((e.clientX - box.left) / box.width) * width), r: Math.floor(((e.clientY - box.top) / box.height) * height) };
  };

  const startDrag = (e: ReactPointerEvent, it: Item, r: number, c: number) => {
    e.preventDefault();
    setSelected(it.uid);
    gridRef.current?.setPointerCapture(e.pointerId);
    setDrag({ uid: it.uid, shape: it.shape, turns: 0, grabR: r, grabC: c, x: it.x, y: it.y,
              startX: e.clientX, startY: e.clientY, moved: false });
  };

  const onMove = (e: ReactPointerEvent) => {
    const d = dragRef.current;
    if (!d) return;
    const far = Math.abs(e.clientX - d.startX) + Math.abs(e.clientY - d.startY) > 4;
    const { r, c } = cellAt(e);
    setDrag({ ...d, x: c - d.grabC, y: r - d.grabR, moved: d.moved || far });
  };

  const onUp = () => {
    const d = dragRef.current;
    setDrag(null);
    if (!d || !d.moved) return;
    const it = items.find((i) => i.uid === d.uid);
    if (!it || !fits(d.shape, d.x, d.y, d.uid)) return;
    if (d.x === it.x && d.y === it.y && d.turns % 4 === 0) return;
    LN.send("InventoryMoveRequested", { uid: d.uid, x: d.x, y: d.y, rot: (it.rot + d.turns) % 4 });
  };

  const sel = items.find((i) => i.uid === selected) ?? null;
  const loadPct = (v: number) => `${Math.min(100, (100 * v) / Math.max(0.01, inv?.bulkMax ?? 1))}%`;
  const LOAD = ["Light", "Encumbered", "Overloaded"];

  return (
    <div className="pack">
      <div className="pack-main">
        <div
          ref={gridRef}
          className="pack-grid"
          style={{ ["--w" as string]: width, ["--h" as string]: height }}
          onPointerMove={onMove}
          onPointerUp={onUp}
          onPointerCancel={() => setDrag(null)}
          onPointerDown={(e) => { if (e.target === e.currentTarget) setSelected(null); }}
        >
          {items.map((it) => (
            <ItemView
              key={it.uid}
              item={it}
              dragging={drag?.uid === it.uid && drag.moved}
              selected={selected === it.uid}
              onGrab={startDrag}
              onUse={() => it.canUse && LN.send("InventoryUseRequested", { uid: it.uid })}
            />
          ))}
          {drag?.moved && (
            <Ghost shape={drag.shape} x={drag.x} y={drag.y} ok={fits(drag.shape, drag.x, drag.y, drag.uid)}
                   color={items.find((i) => i.uid === drag.uid)?.color ?? "#00e5ff"} />
          )}
        </div>
        <div className="pack-hint">
          Drag to move &middot; <kbd>R</kbd> rotate &middot; double-click to use
        </div>
      </div>

      <aside className="pack-side">
        <h3>Load</h3>
        <div className={`pack-load load-${inv?.load ?? 0}`}>
          <div className="pack-load-head">
            <span>{LOAD[inv?.load ?? 0]}</span>
            <b>
              {(inv?.bulk ?? 0).toFixed(1)} <small>/ {(inv?.bulkLimit ?? 0).toFixed(1)} bulk</small>
            </b>
          </div>
          <div className="pack-load-bar">
            <div className="pack-load-fill" style={{ width: loadPct(inv?.bulk ?? 0) }} />
            <div className="pack-load-mark" style={{ left: loadPct(inv?.bulkLimit ?? 0) }} />
          </div>
          <div className="pack-load-sub">
            {(inv?.speed ?? 1) < 1 ? `Walking at ${Math.round((inv?.speed ?? 1) * 100)}%` : "Full speed"} &middot; limit from STR
          </div>
        </div>
        <div className="pack-value">
          Carrying <b>{(inv?.value ?? 0).toLocaleString()}</b> cr
        </div>

        <h3>Item</h3>
        {sel ? (
          <div className="pack-detail" style={{ ["--c" as string]: sel.color }}>
            <div className="pack-detail-name">{sel.name}</div>
            <div className="pack-detail-kind">
              {KINDS[sel.kind] ?? ""} &middot; {sel.bulk} bulk{sel.value > 0 ? ` · ${sel.value.toLocaleString()} cr` : ""}
            </div>
            <p>{sel.description}</p>
            <div className="pack-actions">
              {sel.canUse && (
                <button type="button" className="sheet-btn" onClick={() => LN.send("InventoryUseRequested", { uid: sel.uid })}>
                  Use
                </button>
              )}
              <button type="button" className="sheet-btn"
                      onClick={() => LN.send("InventoryMoveRequested", { uid: sel.uid, x: sel.x, y: sel.y, rot: (sel.rot + 1) % 4 })}>
                Rotate <kbd>R</kbd>
              </button>
              {sel.canDrop && (
                <button type="button" className="sheet-btn danger" onClick={() => { LN.send("InventoryDropRequested", { uid: sel.uid }); setSelected(null); }}>
                  Drop
                </button>
              )}
            </div>
          </div>
        ) : (
          <p className="muted">Select an item.</p>
        )}
        {inv?.message && <div className="pack-message">{inv.message}</div>}
      </aside>
    </div>
  );
}

function ItemView({ item, dragging, selected, onGrab, onUse }: {
  item: Item; dragging: boolean; selected: boolean;
  onGrab: (e: ReactPointerEvent, it: Item, r: number, c: number) => void; onUse: () => void;
}) {
  // The label sits on the first filled cell.
  let labelR = 0;
  let labelC = item.shape[0].findIndex(Boolean);
  if (labelC < 0) { labelR = item.shape.findIndex((row) => row.some(Boolean)); labelC = item.shape[labelR].findIndex(Boolean); }
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
