using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;
using Snitch.Config;

namespace Snitch.UI
{
    /// <summary>Persisted position/size/visibility of one overlay window.</summary>
    internal sealed class WinState
    {
        public float X, Y, W, H;
        public bool Visible;
    }

    /// <summary>
    /// Persists every overlay window's layout (position, size, visibility) in a single preference string so the set
    /// of windows can grow per-mod without registering N MelonPreferences entries. Format is a compact
    /// <c>id=x,y,w,h,v;...</c> (v = 0/1) - only Snitch writes it, parsed tolerantly. Saved on drag-release / toggle.
    /// </summary>
    internal static class WindowLayout
    {
        private static readonly Dictionary<string, WinState> _map = new Dictionary<string, WinState>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Parse(Preferences.WindowLayouts);
        }

        /// <summary>Get the window's state, materializing it from defaults the first time (not persisted until a
        /// drag/toggle saves).</summary>
        internal static WinState Get(string id, float dx, float dy, float dw, float dh, bool dvis)
        {
            EnsureLoaded();
            if (!_map.TryGetValue(id, out WinState s))
            {
                s = new WinState { X = dx, Y = dy, W = dw, H = dh, Visible = dvis };
                _map[id] = s;
            }
            return s;
        }

        internal static bool IsVisible(string id)
        {
            EnsureLoaded();
            return _map.TryGetValue(id, out WinState s) && s.Visible;
        }

        internal static void SetPos(string id, float x, float y)
        {
            if (_map.TryGetValue(id, out WinState s)) { s.X = x; s.Y = y; }
        }

        internal static void SetSize(string id, float w, float h)
        {
            if (_map.TryGetValue(id, out WinState s)) { s.W = w; s.H = h; }
        }

        internal static void SetVisible(string id, bool v)
        {
            EnsureLoaded();
            if (!_map.TryGetValue(id, out WinState s)) { s = new WinState { X = 8, Y = 8, W = 360, H = 280 }; _map[id] = s; }
            s.Visible = v;
            Save();
        }

        internal static bool Toggle(string id)
        {
            bool v = !IsVisible(id);
            SetVisible(id, v);
            return v;
        }

        internal static void Reset(string id)
        {
            EnsureLoaded();
            _map.Remove(id);
            Save();
        }

        internal static void Save()
        {
            Preferences.SetWindowLayouts(Serialize());
            MelonPreferences.Save();
        }

        private static void Parse(string s)
        {
            _map.Clear();
            if (string.IsNullOrWhiteSpace(s)) return;
            string[] entries = s.Split(';');
            foreach (string entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string id = entry.Substring(0, eq).Trim();
                string[] f = entry.Substring(eq + 1).Split(',');
                if (f.Length < 5) continue;
                if (F(f[0], out float x) && F(f[1], out float y) && F(f[2], out float w) && F(f[3], out float h))
                {
                    _map[id] = new WinState { X = x, Y = y, W = w, H = h, Visible = f[4].Trim() == "1" };
                }
            }
        }

        private static string Serialize()
        {
            var sb = new StringBuilder(128);
            bool first = true;
            foreach (KeyValuePair<string, WinState> kv in _map)
            {
                if (!first) sb.Append(';');
                first = false;
                WinState s = kv.Value;
                sb.Append(kv.Key).Append('=')
                  .Append(N(s.X)).Append(',').Append(N(s.Y)).Append(',')
                  .Append(N(s.W)).Append(',').Append(N(s.H)).Append(',')
                  .Append(s.Visible ? '1' : '0');
            }
            return sb.ToString();
        }

        private static bool F(string s, out float v) => float.TryParse(s.Trim(), NumberStyles.Float, Inv, out v);
        private static string N(float v) => ((int)v).ToString(Inv);
    }
}
