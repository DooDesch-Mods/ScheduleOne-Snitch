using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.Server
{
    /// <summary>
    /// Serializes the cached profiler snapshot into the wire JSON that SnitchWeb (and the bundled offline
    /// viewer) consume. Hand-rolled with a strict escaper so it is robust under IL2CPP (no reflection-based
    /// serializer dependency) and fast. MUST be called on the main thread (it reads SnitchCore's cached lists);
    /// the result string is then safe to hand to the background socket threads. The TS mirror lives in
    /// SnitchWeb/app/lib/protocol.ts - keep them in sync when fields change.
    /// </summary>
    internal static class WireProtocol
    {
        internal const int Version = 1;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static string BuildSnapshot(int frame, string scene)
        {
            FrameStats f = SnitchCore.LatestFrame;
            var sb = new StringBuilder(8192);
            sb.Append("{\"type\":\"snapshot\",\"v\":").Append(Version).Append(",\"t\":").Append(frame).Append(',');
            sb.Append("\"meta\":{\"mod\":\"Snitch\",\"version\":\"1.3.0\",\"scene\":\"").Append(Esc(scene))
              .Append("\",\"active\":").Append(SnitchCore.Active ? "true" : "false").Append("},");

            sb.Append("\"frame\":{");
            Num(sb, "meanMs", f.MeanMs); Num(sb, "medianMs", f.MedianMs); Num(sb, "p95Ms", f.P95Ms);
            Num(sb, "p99Ms", f.P99Ms); Num(sb, "minMs", f.MinMs); Num(sb, "maxMs", f.MaxMs);
            Num(sb, "meanFps", f.MeanFps); Num(sb, "minFps", f.MinFps);
            Num(sb, "gc0", f.Gc0Per1000); Num(sb, "gc1", f.Gc1Per1000);
            sb.Append("\"samples\":").Append(f.Samples).Append("},");

            sb.Append("\"sections\":[");
            var rows = SnitchCore.LatestSections;
            if (rows != null)
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    SectionRow r = rows[i];
                    sb.Append("{\"group\":\"").Append(Esc(r.Group)).Append("\",\"label\":\"").Append(Esc(r.Label)).Append("\",");
                    Num(sb, "ms", r.MsPerFrame); Num(sb, "max", r.MaxMs); Num(sb, "calls", r.Calls);
                    sb.Append("\"pct\":").Append(F(r.PctFrame)).Append('}');
                }
            sb.Append("],");

            sb.Append("\"counters\":[");
            var cs = SnitchCore.LatestCounters;
            if (cs != null)
                for (int i = 0; i < cs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    CounterRow c = cs[i];
                    sb.Append("{\"id\":\"").Append(Esc(c.Id)).Append("\",\"value\":").Append(F(c.Value))
                      .Append(",\"unit\":\"").Append(Esc(c.Unit)).Append("\",\"state\":\"").Append(Esc(c.State)).Append("\"}");
                }
            sb.Append("],");

            sb.Append("\"states\":[");
            var st = SnitchCore.LatestStates;
            if (st != null)
                for (int i = 0; i < st.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    StateSnapshot s = st[i];
                    sb.Append("{\"id\":\"").Append(Esc(s.Id)).Append("\",\"title\":\"").Append(Esc(s.Title))
                      .Append("\",\"total\":").Append(s.EffectiveTotal()).Append(",\"buckets\":[");
                    for (int k = 0; k < s.Buckets.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append("{\"name\":\"").Append(Esc(s.Buckets[k].Name)).Append("\",\"count\":").Append(s.Buckets[k].Count).Append('}');
                    }
                    sb.Append("]}");
                }
            sb.Append("],");

            AppendPanels(sb);
            sb.Append(',');
            AppendLogs(sb);

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Per-mod panels: title + free-text readout + action buttons + toggles. Counters/state stay in the
        /// top-level arrays (the dashboard groups them under the panel by id-prefix). Text/toggle delegates run here
        /// on the main thread (called from SnitchCore.Poll).</summary>
        private static void AppendPanels(StringBuilder sb)
        {
            sb.Append("\"panels\":[");
            IReadOnlyList<PanelModel> panels = PanelRegistry.All;
            bool firstPanel = true;
            for (int i = 0; i < panels.Count; i++)
            {
                PanelModel p = panels[i];
                if (!firstPanel) sb.Append(',');
                firstPanel = false;

                sb.Append("{\"id\":\"").Append(Esc(p.Id)).Append("\",\"title\":\"").Append(Esc(p.Title))
                  .Append("\",\"hasLog\":").Append(p.HasLog ? "true" : "false").Append(",\"text\":\"");
                sb.Append(Esc(EvalText(p))).Append("\",\"actions\":[");
                for (int a = 0; a < p.Actions.Count; a++)
                {
                    if (a > 0) sb.Append(',');
                    sb.Append("{\"id\":\"").Append(Esc(p.Actions[a].Id)).Append("\",\"label\":\"").Append(Esc(p.Actions[a].Label)).Append("\"}");
                }
                sb.Append("],\"toggles\":[");
                for (int tg = 0; tg < p.Toggles.Count; tg++)
                {
                    if (tg > 0) sb.Append(',');
                    bool val = false;
                    try { val = p.Toggles[tg].Get != null && p.Toggles[tg].Get(); } catch { }
                    sb.Append("{\"id\":\"").Append(Esc(p.Toggles[tg].Id)).Append("\",\"label\":\"").Append(Esc(p.Toggles[tg].Label))
                      .Append("\",\"value\":").Append(val ? "true" : "false").Append('}');
                }
                sb.Append("]}");
            }
            sb.Append(']');
        }

        private static string EvalText(PanelModel p)
        {
            if (p.Texts.Count == 0) return "";
            var tb = new StringBuilder(128);
            for (int i = 0; i < p.Texts.Count; i++)
            {
                string s = null;
                try { s = p.Texts[i]?.Invoke(); } catch { }
                if (string.IsNullOrEmpty(s)) continue;
                if (tb.Length > 0) tb.Append('\n');
                tb.Append(s);
            }
            return tb.ToString();
        }

        /// <summary>The combined timeline (all channels, chronological). The dashboard derives per-mod views by
        /// filtering on the channel field. Bounded slice so the snapshot stays small at the stream rate.</summary>
        private static void AppendLogs(StringBuilder sb)
        {
            sb.Append("\"logs\":{\"timeline\":[");
            List<LogEntry> entries = LogHub.Timeline(200);
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                LogEntry e = entries[i];
                sb.Append("{\"seq\":").Append(e.Seq).Append(",\"t\":\"").Append(Esc(e.Time)).Append("\",\"ch\":\"")
                  .Append(Esc(e.Ch)).Append("\",\"lvl\":").Append(e.Lvl).Append(",\"msg\":\"").Append(Esc(e.Msg)).Append("\"}");
            }
            sb.Append("]}");
        }

        internal static string BuildHealth(int frame, string scene)
        {
            return "{\"ok\":true,\"mod\":\"Snitch\",\"version\":\"1.3.0\",\"active\":" + (SnitchCore.Active ? "true" : "false")
                 + ",\"scene\":\"" + Esc(scene) + "\",\"frame\":" + frame + "}";
        }

        /// <summary>Static capability statement (the honesty layer).</summary>
        internal static string BuildCaps()
        {
            return "{\"type\":\"caps\",\"v\":" + Version + ",\"frameTime\":\"load-bearing\",\"gc\":\"load-bearing\","
                 + "\"engineCounters\":\"unavailable\",\"perEntityAttribution\":\"viable\","
                 + "\"note\":\"ProfilerRecorder is inert in this IL2CPP build; frame-time + GC are the truth. "
                 + "Per-entity vanilla cost attribution is viable. Causal subsystem cost uses the ablation stability gate.\"}";
        }

        // ----- helpers -----
        private static void Num(StringBuilder sb, string key, double v) { sb.Append('"').Append(key).Append("\":").Append(F(v)).Append(','); }
        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.###", Inv);
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' || c == '\\' || c < ' ')
                {
                    if (b == null) { b = new StringBuilder(s.Length + 8); b.Append(s, 0, i); }
                    if (c == '"') b.Append("\\\"");
                    else if (c == '\\') b.Append("\\\\");
                    else b.Append("\\u").Append(((int)c).ToString("x4"));
                }
                else b?.Append(c);
            }
            return b?.ToString() ?? s;
        }
    }
}
