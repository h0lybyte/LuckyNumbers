// LowEnd.cs: the device's memory class (navigator.deviceMemory, GB, rounded down to a power of two by the browser;
// Chrome / Edge / Android only). 0 when the browser doesn't say.
mergeInto(LibraryManager.library, {
  LN_DeviceMemory: function () {
    return (typeof navigator !== "undefined" && navigator.deviceMemory) || 0;
  },
});
