// The page's on-screen sticks and buttons (web/src/touch.tsx) -> TouchPad.cs, once per input update, no JSON:
// the page keeps window.LNTouch = { on, axes: Float32Array(4) [lx, ly, rx, ry], buttons: bitmask }, and this
// copies the four axes into the C# buffer and returns the buttons, or -1 while the touch controls are off.
mergeInto(LibraryManager.library, {
  LN_TouchRead: function (axesPtr) {
    if (typeof window === "undefined") return -1;
    var t = window.LNTouch;
    if (!t || !t.on) return -1;
    // With threads the heap can grow under us: take a fresh view (GROWABLE_HEAP_F32) where Emscripten has one.
    var heap = typeof GROWABLE_HEAP_F32 !== "undefined" ? GROWABLE_HEAP_F32() : HEAPF32;
    heap.set(t.axes, axesPtr >> 2);
    return t.buttons | 0;
  },
});
