using System.Text;
using UnityEngine;
using Snitch.Engine;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.UI
{
    /// <summary>
    /// Builds the profiler's in-game text: the "Overview" readout (load-bearing frame stats, top section costs and
    /// state distributions) and the per-panel metrics readout (a panel's counters + state distributions, matched by
    /// id-prefix). Snitch no longer draws its own overlay - it feeds these strings to the Hotline framework as panel
    /// text providers, so the in-game overlay lives in Hotline while the data stays here. This is also the in-game
    /// cousin of the SnitchWeb dashboard's header/sections.
    /// </summary>
    internal static class ProfilerHud
    {
        internal static string BuildOverview()
        {
            FrameStats f = SnitchCore.LatestFrame;
            var sb = new StringBuilder(512);

            if (!SnitchCore.Active)
                sb.Append("<color=#fd5>idle</color> - run 'snitch start' (or auto-start) to sample.\n");

            string col = f.MeanFps >= 50 ? "#5f5" : (f.MeanFps >= 30 ? "#fd5" : "#f55");
            sb.Append("<color=").Append(col).Append('>').Append(f.MeanFps.ToString("F0"))
              .Append(" fps</color>  (min ").Append(f.MinFps.ToString("F0")).Append(")\n");
            sb.Append(f.MeanMs.ToString("F2")).Append(" ms   p95 ").Append(f.P95Ms.ToString("F2"))
              .Append("   gc0/1k ").Append(f.Gc0Per1000.ToString("F1"));

            var rows = SnitchCore.LatestSections;
            if (rows != null && rows.Count > 0)
            {
                sb.Append("\n<b>sections</b>");
                int n = Mathf.Min(8, rows.Count);
                for (int i = 0; i < n; i++)
                {
                    SectionRow r = rows[i];
                    sb.Append('\n').Append(r.Label).Append("   ").Append(r.MsPerFrame.ToString("F2")).Append(" ms  ")
                      .Append(r.PctFrame.ToString("F0")).Append('%');
                }
            }

            var states = SnitchCore.LatestStates;
            if (states != null && states.Count > 0)
            {
                sb.Append("\n<b>states</b>");
                foreach (StateSnapshot b in states)
                {
                    sb.Append('\n').Append(b.Title).Append(' ').Append(b.EffectiveTotal()).Append(": ");
                    for (int i = 0; i < b.Buckets.Count; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(b.Buckets[i].Name).Append('=').Append(b.Buckets[i].Count);
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>The counters and state distributions registered under a panel id (matched by "&lt;panelId&gt;." prefix,
        /// or the panel id itself), formatted as the panel's metrics readout. Empty until sampling produces data.</summary>
        internal static string BuildPanelMetrics(string panelId)
        {
            if (string.IsNullOrEmpty(panelId)) return "";
            var sb = new StringBuilder(128);
            string prefix = panelId + ".";

            var cs = SnitchCore.LatestCounters;
            if (cs != null)
                for (int i = 0; i < cs.Count; i++)
                {
                    CounterRow c = cs[i];
                    if (c.Id != panelId && !c.Id.StartsWith(prefix)) continue;
                    string name = c.Id.StartsWith(prefix) ? c.Id.Substring(prefix.Length) : c.Id;
                    Line(sb, $"{name} = {c.Value:0.##} {c.Unit}".TrimEnd());
                }

            var st = SnitchCore.LatestStates;
            if (st != null)
                for (int i = 0; i < st.Count; i++)
                {
                    StateSnapshot ss = st[i];
                    if (ss.Id != panelId && !ss.Id.StartsWith(prefix)) continue;
                    var line = new StringBuilder();
                    line.Append(ss.Title).Append(' ').Append(ss.EffectiveTotal()).Append(": ");
                    for (int k = 0; k < ss.Buckets.Count; k++)
                    {
                        if (k > 0) line.Append(' ');
                        line.Append(ss.Buckets[k].Name).Append('=').Append(ss.Buckets[k].Count);
                    }
                    Line(sb, line.ToString());
                }

            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string s) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }
    }
}
