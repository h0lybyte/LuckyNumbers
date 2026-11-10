// Unity side of window.LN (defined in the WebGL template). Called by WebBridge.cs on the main thread
// only, once per frame. With threads on, the heap is a SharedArrayBuffer: UTF8ToString copies out of it
// (TextDecoder can't read shared memory directly), so strings are safe to keep after the call.
// `typeof window` guards keep these harmless if ever invoked off the main thread (workers have no DOM).
mergeInto(LibraryManager.library, {
  // One batch per frame: {"s":{State:{...}},"e":[["Event",{...}],...]}.
  LN_Push: function (batch) {
    if (typeof window === "undefined" || !window.LN) return;
    window.LN._fromUnity(UTF8ToString(batch));
  },

  // Page-sent events as one JSON array, or null when the page has nothing queued.
  // The returned buffer is freed by the C# marshaller.
  LN_Drain: function () {
    if (typeof window === "undefined" || !window.LN) return 0;
    var s = window.LN._drain();
    if (!s) return 0;
    var size = lengthBytesUTF8(s) + 1;
    var ptr = _malloc(size);
    stringToUTF8(s, ptr, size);
    return ptr;
  },

  LN_Ready: function () {
    if (typeof window !== "undefined" && window.LN) window.LN._ready();
  },
});
