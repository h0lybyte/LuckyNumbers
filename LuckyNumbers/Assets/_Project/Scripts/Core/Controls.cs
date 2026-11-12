namespace LuckyNumbers.Core
{
    /// <summary>
    /// Control hints for whoever is playing. On a touch screen (<see cref="PointerLock.Touch"/>, the page's sticks and
    /// buttons, web/src/touch.tsx) the content's touch_hint lines stand in for the keyboard / mouse ones, and a
    /// prompt's key ("E: board your ship", "R: use the ...", "brake (X)") becomes the button that does it.
    /// </summary>
    public static class Controls
    {
        /// <summary>The keyboard / mouse line, or on a touch screen its touch version when the content has one.</summary>
        public static string Pick(string keys, string touch) =>
            PointerLock.Touch && !string.IsNullOrEmpty(touch) ? touch : keys ?? "";

        /// <summary>What to call the interact key in a line built in code: E, or the Use button.</summary>
        public static string Use => PointerLock.Touch ? "Use" : "E";

        static string _in, _out;   // the last prompt rewritten: hints are re-sent every frame

        /// <summary>A prompt as shown: on a touch screen its keys named as the on-screen buttons (Use, Relic, Brake).</summary>
        public static string Prompt(string text)
        {
            if (!PointerLock.Touch || string.IsNullOrEmpty(text)) return text;
            if (text == _in) return _out;
            var s = text;
            if (s.StartsWith("E: ")) s = "Use: " + s.Substring(3);
            else if (s.StartsWith("R: ")) s = "Relic: " + s.Substring(3);
            s = s.Replace(" - E to ", " - Use to ").Replace("press E ", "press Use ").Replace("then E to ", "then Use to ")
                 .Replace("(X)", "(Brake)");
            _in = text;
            _out = s;
            return s;
        }
    }
}
