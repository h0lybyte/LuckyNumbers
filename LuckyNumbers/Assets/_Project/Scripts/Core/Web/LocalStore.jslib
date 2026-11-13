// LocalStore.cs: the browser's localStorage, which stays put across builds. (Unity's PlayerPrefs live in IndexedDB
// under a hash of the page's folder URL, and itch.io gives every upload a new folder, so they're lost on each
// release.) Every call is guarded: storage can be blocked (private windows, strict cookie settings), and then
// LocalStore falls back to PlayerPrefs.
mergeInto(LibraryManager.library, {
  // The value, or null when there's none (or no storage). The C# marshaller frees the buffer.
  LN_StoreGet: function (key) {
    try {
      var v = window.localStorage.getItem(UTF8ToString(key));
      if (v === null) return 0;
      var size = lengthBytesUTF8(v) + 1;
      var ptr = _malloc(size);
      stringToUTF8(v, ptr, size);
      return ptr;
    } catch (e) {
      return 0;
    }
  },

  // 1 if it was written.
  LN_StoreSet: function (key, value) {
    try {
      window.localStorage.setItem(UTF8ToString(key), UTF8ToString(value));
      return 1;
    } catch (e) {
      return 0;
    }
  },

  LN_StoreDelete: function (key) {
    try {
      window.localStorage.removeItem(UTF8ToString(key));
    } catch (e) {}
  },
});
