using System;
using System.Globalization;
using System.Text;
using Snitch.Config;

namespace Snitch.UI
{
    /// <summary>
    /// The overlay's remembered window layout, kept as one preference string so the set of windows can grow with the
    /// mods that register panels. Format: <c>id=x,y,w,h,v;...</c> - x, y, w, h are fractions of the screen (so the
    /// layout survives a resolution change) and v is 1 for an open window, 0 for a closed one. The entry <c>c</c> is
    /// the compact card: x is its left edge and y its bottom edge. The page writes and reads the string; this class
    /// validates it, counts the windows and holds it across a page that is mounted again.
    /// </summary>
    internal static class OverlayLayout
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The stored, validated layout string; empty means "defaults".</summary>
        internal static string Value => Preferences.OverlayLayout;

        /// <summary>
        /// Keeps the entries that parse and drops the rest, each drop logged. Returns the number of windows kept, or -1
        /// when nothing was handed over. The preference is only set, never saved here: MelonPreferences.Save() runs
        /// every mod's preference listeners on the calling thread, and from a page callback that killed the game.
        /// MelonLoader writes the value on shutdown.
        /// </summary>
        internal static int Store(string raw)
        {
            if (raw == null)
            {
                Core.Log?.Warning("[overlay] layout write got no text, nothing stored.");
                return -1;
            }

            var kept = new StringBuilder();
            int n = 0;
            foreach (string entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Valid(entry))
                {
                    Core.Log?.Warning("[overlay] layout entry '" + entry + "' is malformed, dropped.");
                    continue;
                }
                if (kept.Length > 0) kept.Append(';');
                kept.Append(entry);
                n++;
            }

            Preferences.OverlayLayout = kept.ToString();
            Core.Log?.Msg("[overlay] layout saved (" + n + " windows).");
            return n;
        }

        internal static void Reset()
        {
            Preferences.OverlayLayout = "";
            Core.Log?.Msg("[overlay] layout reset to the defaults.");
        }

        private static bool Valid(string entry)
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) return false;
            string[] f = entry.Substring(eq + 1).Split(',');
            if (f.Length != 5) return false;
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(f[i], NumberStyles.Float, Inv, out double d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
            }
            return f[4] == "0" || f[4] == "1";
        }
    }
}
