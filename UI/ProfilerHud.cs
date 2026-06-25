using System.Text;
using UnityEngine;
using Snitch.Engine;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.UI
{
    /// <summary>
    /// Builds the text for the "Overview" overlay window: the load-bearing frame stats, the top section costs and
    /// the state distributions. Drawing, dragging and resizing are handled generically by <see cref="WindowManager"/>
    /// (which hosts the overview alongside the per-mod panels and the log timeline). This is the in-game cousin of
    /// the SnitchWeb dashboard's header/sections.
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
    }
}
