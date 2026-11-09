// Browser-side checks for the web threading test (Web builds only).
mergeInto(LibraryManager.library, {
  // 1 when COOP/COEP headers are in effect (SharedArrayBuffer usable => threads possible).
  WebThreads_IsCrossOriginIsolated: function () {
    return (typeof crossOriginIsolated !== 'undefined' && crossOriginIsolated) ? 1 : 0;
  },
  WebThreads_HardwareConcurrency: function () {
    return (typeof navigator !== 'undefined' && navigator.hardwareConcurrency) ? navigator.hardwareConcurrency : 0;
  }
});
