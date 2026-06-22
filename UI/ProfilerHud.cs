using System.Text;
using UnityEngine;
using Snitch.Engine;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.UI
{
    /// <summary>
    /// On-screen profiler overlay. Cached-rebuild IMGUI (the proven Siesta/Litterally pattern): the text is
    /// rebuilt ~10 Hz and drawn from cache every frame, so it adds no per-frame allocation in steady state.
    /// Shows the load-bearing frame stats, the top section costs, and the state distributions. Only drawn while
    /// sampling is armed and the HUD pref is on. This is the in-game cousin of the SnitchWeb dashboard.
    /// </summary>
    internal static class ProfilerHud
    {
        private static GUIStyle _box;
        private static string _cached = "";
        private static float _nextRebuild;

        internal static void Draw()
        {
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 12,
                    richText = true,
                    padding = new RectOffset(8, 8, 6, 6),
                };
                _box.normal.textColor = Color.white;
            }

            if (Time.unscaledTime >= _nextRebuild)
            {
                _cached = Build();
                _nextRebuild = Time.unscaledTime + 0.1f;
            }

            Vector2 size = _box.CalcSize(new GUIContent(_cached));
            GUI.Box(new Rect(8, 8, Mathf.Max(280f, size.x + 6f), size.y + 6f), _cached, _box);
        }

        private static string Build()
        {
            FrameStats f = SnitchCore.LatestFrame;
            var sb = new StringBuilder(512);
            string col = f.MeanFps >= 50 ? "#5f5" : (f.MeanFps >= 30 ? "#fd5" : "#f55");
            sb.Append("<b>Snitch</b>   <color=").Append(col).Append('>').Append(f.MeanFps.ToString("F0"))
              .Append(" fps</color>  (min ").Append(f.MinFps.ToString("F0")).Append(")\n");
            sb.Append(f.MeanMs.ToString("F2")).Append(" ms   p95 ").Append(f.P95Ms.ToString("F2"))
              .Append("   gc0/1k ").Append(f.Gc0Per1000.ToString("F1")).Append('\n');

            var rows = SnitchCore.LatestSections;
            if (rows != null && rows.Count > 0)
            {
                sb.Append("<b>sections</b>\n");
                int n = Mathf.Min(6, rows.Count);
                for (int i = 0; i < n; i++)
                {
                    SectionRow r = rows[i];
                    sb.Append(r.Label).Append("   ").Append(r.MsPerFrame.ToString("F2")).Append(" ms  ")
                      .Append(r.PctFrame.ToString("F0")).Append("%\n");
                }
            }

            var states = SnitchCore.LatestStates;
            if (states != null && states.Count > 0)
            {
                sb.Append("<b>states</b>\n");
                foreach (StateSnapshot b in states)
                {
                    sb.Append(b.Title).Append(' ').Append(b.EffectiveTotal()).Append(": ");
                    for (int i = 0; i < b.Buckets.Count; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(b.Buckets[i].Name).Append('=').Append(b.Buckets[i].Count);
                    }
                    sb.Append('\n');
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
