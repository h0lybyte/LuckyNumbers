// Typed access to window.LN (the bridge runtime in the WebGL template) plus React hooks.
// Message types come from proto via tools/ln.py gen (src/generated/ln.g.ts).
import { useEffect, useRef, useSyncExternalStore } from "react";
import type { InboundEvent, WebEvents, WebStates } from "./generated/ln.g";

export type * from "./generated/ln.g";

type StateName = keyof WebStates;
type EventName = keyof WebEvents;

interface LNRuntime {
  readonly states: Partial<WebStates>;
  readonly ready: boolean;
  getState<K extends StateName>(name: K): WebStates[K] | undefined;
  subscribe<K extends StateName>(name: K, fn: (s: WebStates[K]) => void): () => void;
  subscribe(name: "*", fn: (s: WebStates[StateName], name: StateName) => void): () => void;
  on<K extends EventName>(name: K, fn: (e: WebEvents[K]) => void): () => void;
  on(name: "*", fn: (e: WebEvents[EventName], name: EventName) => void): () => void;
  onReady(fn: () => void): void;
  send<K extends InboundEvent>(name: K, payload: WebEvents[K]): void;
}

declare global {
  interface Window {
    LN?: LNRuntime;
  }
}

/** The bridge runtime. Missing only when the overlay runs outside the Unity template. */
export const LN: LNRuntime = window.LN ?? fallback();

function fallback(): LNRuntime {
  console.warn("[overlay] window.LN missing; running without Unity");
  const noop = () => () => {};
  return { states: {}, ready: false, getState: () => undefined, subscribe: noop, on: noop, onReady() {}, send() {} };
}

/** Latest snapshot of a state; re-renders when Unity sends a new one (at most once per game frame). */
export function useLNState<K extends StateName>(name: K): WebStates[K] | undefined {
  return useSyncExternalStore(
    (notify) => LN.subscribe(name, notify),
    () => LN.getState(name),
  );
}

/** Run `fn` for every `name` event (or "*" for all). The latest fn is used; no resubscribe on render. */
export function useLNEvent<K extends EventName>(name: K | "*", fn: (e: WebEvents[K], name: K) => void): void {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => LN.on(name as K, ((e: WebEvents[K], n?: K) => ref.current(e, n ?? (name as K))) as never), [name]);
}

/** True once Unity's bridge is up. */
export function useLNReady(): boolean {
  return useSyncExternalStore(
    (notify) => {
      LN.onReady(notify);
      return () => {};
    },
    () => LN.ready,
  );
}
