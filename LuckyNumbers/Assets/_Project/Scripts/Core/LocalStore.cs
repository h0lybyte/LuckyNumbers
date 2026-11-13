using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// Saves and settings that last across releases. On the web: the browser's localStorage (LocalStore.jslib),
    /// keyed under "luckynumbers." (itch.io serves every HTML game from one origin). Unity's PlayerPrefs there live
    /// in IndexedDB under a hash of the page's folder URL, and itch gives each upload a new folder, so a new release
    /// couldn't see the last one's. PlayerPrefs stay as the fallback (storage blocked) and as a second copy; a key
    /// only PlayerPrefs has (an older build's, carried over by the page's rescue in index.html) moves across when
    /// it's first read. Elsewhere (editor, desktop): PlayerPrefs alone.
    /// </summary>
    public static class LocalStore
    {
        const string Prefix = "luckynumbers.";

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern string LN_StoreGet(string key);
        [DllImport("__Internal")] static extern int LN_StoreSet(string key, string value);
        [DllImport("__Internal")] static extern void LN_StoreDelete(string key);
        static readonly bool Web = true;
#else
        static string LN_StoreGet(string key) => null;
        static int LN_StoreSet(string key, string value) => 0;
        static void LN_StoreDelete(string key) { }
        static readonly bool Web = false; // readonly, not const: no unreachable-code warnings
#endif

        /// <summary>The string under `key`, or null.</summary>
        public static string GetString(string key)
        {
            if (Web && LN_StoreGet(Prefix + key) is { } stored) return stored;
            if (!PlayerPrefs.HasKey(key)) return null;
            var value = PlayerPrefs.GetString(key, "");
            if (Web && value != "") LN_StoreSet(Prefix + key, value);   // an older build's: carry it over
            return value;
        }

        public static float GetFloat(string key, float fallback)
        {
            if (Web && LN_StoreGet(Prefix + key) is { } stored &&
                float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            if (!PlayerPrefs.HasKey(key)) return fallback;
            var value = PlayerPrefs.GetFloat(key, fallback);
            if (Web) LN_StoreSet(Prefix + key, value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        public static int GetInt(string key, int fallback)
        {
            if (Web && LN_StoreGet(Prefix + key) is { } stored &&
                int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            if (!PlayerPrefs.HasKey(key)) return fallback;
            var value = PlayerPrefs.GetInt(key, fallback);
            if (Web) LN_StoreSet(Prefix + key, value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        /// <summary>Written at once (a tab can close any time on the web).</summary>
        public static void SetString(string key, string value)
        {
            if (Web) LN_StoreSet(Prefix + key, value);
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public static void SetFloat(string key, float value)
        {
            if (Web) LN_StoreSet(Prefix + key, value.ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.SetFloat(key, value);
            PlayerPrefs.Save();
        }

        public static void SetInt(string key, int value)
        {
            if (Web) LN_StoreSet(Prefix + key, value.ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.SetInt(key, value);
            PlayerPrefs.Save();
        }

        public static void Delete(string key)
        {
            if (Web) LN_StoreDelete(Prefix + key);
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
