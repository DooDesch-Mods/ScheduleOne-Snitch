using System.Collections.Generic;
using System.Text;
using Snitch.Engine;
using Snitch.Registries;

namespace Snitch.UI
{
    /// <summary>
    /// Plain-text readouts for the overlay's mod panels: the counters and state distributions registered under a
    /// panel id, one line each. They are matched by the "&lt;panelId&gt;." prefix, so a probe lights up in its mod's
    /// panel without registering anything extra.
    /// </summary>
    internal static class ProfilerHud
    {
        /// <summary>The counters and state distributions of one panel as display lines. Empty until sampling
        /// produces data.</summary>
        internal static List<string> PanelMetrics(string panelId)
        {
            var lines = new List<string>(8);
            if (string.IsNullOrEmpty(panelId)) return lines;
            string prefix = panelId + ".";

            var cs = SnitchCore.LatestCounters;
            if (cs != null)
                for (int i = 0; i < cs.Count; i++)
                {
                    CounterRow c = cs[i];
                    if (c.Id != panelId && !c.Id.StartsWith(prefix)) continue;
                    string name = c.Id.StartsWith(prefix) ? c.Id.Substring(prefix.Length) : c.Id;
                    lines.Add($"{name} = {c.Value:0.##} {c.Unit}".TrimEnd());
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
                    lines.Add(line.ToString());
                }

            return lines;
        }
    }
}
