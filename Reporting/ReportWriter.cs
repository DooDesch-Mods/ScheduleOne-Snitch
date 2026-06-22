using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Snitch.Engine;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.Reporting
{
    /// <summary>
    /// On-demand report export to <c>Mods/Snitch/runs/</c> - a human-readable Markdown summary and/or
    /// machine-readable CSVs (sections / counters / states) a modder can diff before vs after an optimization.
    /// The only filesystem write Snitch does, never on a timer. Reads the cached snapshot on the main thread.
    /// </summary>
    internal static class ReportWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static string Write(string fmt)
        {
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Snitch", "runs");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            var written = new List<string>();

            if (fmt == "md" || fmt == "all")
            {
                string p = Path.Combine(dir, "report_" + stamp + ".md");
                File.WriteAllText(p, BuildMarkdown(), Encoding.UTF8);
                written.Add(p);
            }
            if (fmt == "csv" || fmt == "all")
            {
                written.Add(WriteCsv(dir, "sections_" + stamp + ".csv", SectionsCsv()));
                written.Add(WriteCsv(dir, "counters_" + stamp + ".csv", CountersCsv()));
                written.Add(WriteCsv(dir, "states_" + stamp + ".csv", StatesCsv()));
            }
            return string.Join(" | ", written);
        }

        private static string WriteCsv(string dir, string name, string content)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, content, Encoding.UTF8);
            return p;
        }

        private static string BuildMarkdown()
        {
            FrameStats f = SnitchCore.LatestFrame;
            var sb = new StringBuilder(4096);
            sb.AppendLine("# Snitch profiler report");
            sb.AppendLine();
            sb.AppendLine("- generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            sb.AppendLine("- scene: `" + SnitchCore.LastScene + "`   active: " + SnitchCore.Active);
            sb.AppendLine();
            sb.AppendLine("## Frame time (load-bearing)");
            sb.AppendLine();
            sb.AppendLine("| mean ms | median | p95 | p99 | min | max | mean fps | min fps | gc0/1k | gc1/1k | samples |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            sb.AppendLine($"| {F(f.MeanMs)} | {F(f.MedianMs)} | {F(f.P95Ms)} | {F(f.P99Ms)} | {F(f.MinMs)} | {F(f.MaxMs)} | {F(f.MeanFps)} | {F(f.MinFps)} | {F(f.Gc0Per1000)} | {F(f.Gc1Per1000)} | {f.Samples} |");
            sb.AppendLine();

            sb.AppendLine("## Sections (by ms/frame)");
            sb.AppendLine();
            var rows = SnitchCore.LatestSections;
            if (rows == null || rows.Count == 0) sb.AppendLine("_none_");
            else
            {
                sb.AppendLine("| label | group | ms/frame | % frame | calls/frame | max ms |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (SectionRow r in rows)
                    sb.AppendLine($"| `{r.Label}` | {r.Group} | {F(r.MsPerFrame)} | {F(r.PctFrame)} | {F(r.Calls)} | {F(r.MaxMs)} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Counters");
            sb.AppendLine();
            var cs = SnitchCore.LatestCounters;
            if (cs == null || cs.Count == 0) sb.AppendLine("_none_");
            else
            {
                sb.AppendLine("| id | value | unit | state |");
                sb.AppendLine("|---|---|---|---|");
                foreach (CounterRow c in cs) sb.AppendLine($"| `{c.Id}` | {F(c.Value)} | {c.Unit} | {c.State} |");
            }
            sb.AppendLine();

            sb.AppendLine("## State distributions");
            sb.AppendLine();
            var st = SnitchCore.LatestStates;
            if (st == null || st.Count == 0) sb.AppendLine("_none_");
            else
                foreach (StateSnapshot s in st)
                {
                    sb.Append("- **").Append(s.Title).Append("** (total ").Append(s.EffectiveTotal()).Append("): ");
                    for (int i = 0; i < s.Buckets.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(s.Buckets[i].Name).Append('=').Append(s.Buckets[i].Count);
                    }
                    sb.AppendLine();
                }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("_ProfilerRecorder engine counters are inert in this IL2CPP build; frame-time + GC are the truth. " +
                "Vanilla section costs are self-measured (only wrapped methods) and include a small patch overhead._");
            return sb.ToString();
        }

        private static string SectionsCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("label,group,msPerFrame,pctFrame,callsPerFrame,maxMs");
            var rows = SnitchCore.LatestSections;
            if (rows != null)
                foreach (SectionRow r in rows)
                    sb.AppendLine($"{Csv(r.Label)},{Csv(r.Group)},{F(r.MsPerFrame)},{F(r.PctFrame)},{F(r.Calls)},{F(r.MaxMs)}");
            return sb.ToString();
        }

        private static string CountersCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,value,unit,state");
            var cs = SnitchCore.LatestCounters;
            if (cs != null)
                foreach (CounterRow c in cs) sb.AppendLine($"{Csv(c.Id)},{F(c.Value)},{Csv(c.Unit)},{Csv(c.State)}");
            return sb.ToString();
        }

        private static string StatesCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("provider,title,total,bucket,count");
            var st = SnitchCore.LatestStates;
            if (st != null)
                foreach (StateSnapshot s in st)
                    for (int i = 0; i < s.Buckets.Count; i++)
                        sb.AppendLine($"{Csv(s.Id)},{Csv(s.Title)},{s.EffectiveTotal()},{Csv(s.Buckets[i].Name)},{s.Buckets[i].Count}");
            return sb.ToString();
        }

        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.###", Inv);
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
