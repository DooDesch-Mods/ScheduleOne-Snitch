using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch
{
    /// <summary>
    /// Console bridge. Patches the game's <c>Console.SubmitCommand</c> (both overloads) and intercepts the
    /// "snitch ..." namespace so the profiler can be driven from the in-game console (and headlessly).
    /// </summary>
    internal static class SnitchConsole
    {
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            string[] p = new string[args.Count];
            for (int i = 0; i < args.Count; i++) p[i] = args[i];
            return Dispatch(p);
        }

        private static bool Dispatch(string[] p)
        {
            if (p.Length == 0 || !p[0].Equals("snitch", StringComparison.OrdinalIgnoreCase))
            {
                return false;   // not ours - let the game handle it
            }

            // Both SubmitCommand overloads fire for one entry - dedupe the same command within one frame.
            string sig = string.Join(" ", p);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;
            _lastFrame = frame; _lastSig = sig;
            LogHub.Write("Console", 0, sig);

            string cmd = p.Length > 1 ? p[1].ToLowerInvariant() : "status";
            try
            {
                switch (cmd)
                {
                    case "start": SnitchCore.Start(); break;
                    case "stop": SnitchCore.Stop(); break;
                    case "status": Status(); break;
                    case "frame": Frame(); break;
                    case "top":
                    case "sections": Top(IntArg(p, 2, 8), cmd == "sections"); break;
                    case "states": States(p.Length > 2 ? p[2] : null); break;
                    case "counters": Counters(); break;
                    case "panels": PanelsList(); break;
                    case "act": ActCmd(p); break;
                    case "toggle": ToggleCmd(p); break;
                    case "log": LogCmd(p); break;
                    case "vanilla": Vanilla(p); break;
                    case "report": Report(p.Length > 2 ? p[2].ToLowerInvariant() : "all"); break;
                    case "ablate": Ablate(p); break;
                    case "levers": Log("ablation levers: " + string.Join(", ", Ablation.LeverRegistry.Names)); break;
                    case "help": Help(); break;
                    default: Log($"unknown '{cmd}'. Try 'snitch help'."); break;
                }
            }
            catch (Exception e)
            {
                Log("error: " + e.Message);
            }
            return true;
        }

        private static void Help()
        {
            Log("commands: start | stop | status | frame | top [n] | sections | states [id] | counters | "
                + "panels | act <actionId> | toggle <toggleId> [on|off] | log [<channel>|all] [n] | "
                + "vanilla [on|off] | ablate <lever> | levers | report [md|csv|all]  "
                + "(in-game overlay: install Hotline, then press its master key or 'hotline help')");
        }

        // ----- per-mod panels (data only; the in-game overlay windows are owned by the Hotline framework) -----

        private static void PanelsList()
        {
            var panels = PanelRegistry.All;
            if (panels.Count == 0) { Log("no mod panels registered yet (enter the world; panels register on probe discovery)."); return; }
            Log($"{panels.Count} panel(s) (toggle their windows in the Hotline overlay):");
            for (int i = 0; i < panels.Count; i++)
            {
                PanelModel p = panels[i];
                Log($"  {p.Id,-16} actions={p.Actions.Count} toggles={p.Toggles.Count} title=\"{p.Title}\"");
            }
        }

        private static void ActCmd(string[] p)
        {
            if (p.Length <= 2) { Log("usage: snitch act <actionId> (see the panel; ids look like 'Siesta:force-cosmetic')."); return; }
            Log(PanelRegistry.Invoke(p[2]) ? "ran " + p[2] : "no action '" + p[2] + "'");
        }

        private static void ToggleCmd(string[] p)
        {
            if (p.Length <= 2) { Log("usage: snitch toggle <toggleId> [on|off] (omit to flip)."); return; }
            string id = p[2];
            bool val = BoolArg(p, 3, !PanelRegistry.GetToggle(id));
            Log(PanelRegistry.SetToggle(id, val) ? $"{id} = {val}" : "no toggle '" + id + "'");
        }

        private static void LogCmd(string[] p)
        {
            string ch = p.Length > 2 ? p[2] : "all";
            int n = IntArg(p, 3, 25);
            var entries = (ch.Equals("all", StringComparison.OrdinalIgnoreCase)) ? LogHub.Timeline(n) : LogHub.Channel(ch, n);
            if (entries.Count == 0) { Log($"log '{ch}': no entries (channels: {string.Join(", ", LogHub.Channels())})."); return; }
            Log($"log '{ch}' (last {entries.Count}):");
            foreach (LogEntry e in entries)
            {
                string lv = e.Lvl == 2 ? "E" : (e.Lvl == 1 ? "W" : "I");
                Log($"  {e.Time} {lv} [{e.Ch}] {e.Msg}");
            }
        }

        private static void Status()
        {
            FrameStats f = SnitchCore.LatestFrame;
            Log($"active={SnitchCore.Active} fps={f.MeanFps:F0} (min {f.MinFps:F0}) frame={f.MeanMs:F2}ms p95={f.P95Ms:F2}ms " +
                $"sections={SectionProfiler.LabelCount} states={StateRegistry.Count} counters={CounterRegistry.Count} " +
                $"poll={Preferences.PollHz:F0}Hz");
            if (!SnitchCore.Active) Log("(idle - run 'snitch start' to begin sampling)");
        }

        private static void Frame()
        {
            FrameStats f = SnitchCore.LatestFrame;
            Log($"frame: mean={f.MeanMs:F2}ms median={f.MedianMs:F2} p95={f.P95Ms:F2} p99={f.P99Ms:F2} " +
                $"min={f.MinMs:F2} max={f.MaxMs:F2} | fps mean={f.MeanFps:F0} min={f.MinFps:F0} | " +
                $"gc0/1000f={f.Gc0Per1000:F1} gc1/1000f={f.Gc1Per1000:F1} samples={f.Samples}");
        }

        private static void Top(int n, bool all)
        {
            var rows = SnitchCore.LatestSections;
            if (rows == null || rows.Count == 0) { Log("sections: none yet (sample a frame; modder/vanilla sections appear once registered)."); return; }
            int shown = all ? rows.Count : Math.Min(n, rows.Count);
            Log($"sections (top {shown} of {rows.Count} by ms/frame):");
            for (int i = 0; i < shown; i++)
            {
                SectionRow r = rows[i];
                Log($"  {r.Label,-28} {r.MsPerFrame,7:F3} ms/f  {r.PctFrame,5:F1}%  {r.Calls,6:F0} calls/f  (max {r.MaxMs:F3})");
            }
        }

        private static void States(string filter)
        {
            var blocks = SnitchCore.LatestStates;
            if (blocks == null || blocks.Count == 0) { Log("states: none yet (start sampling first)."); return; }
            foreach (StateSnapshot b in blocks)
            {
                if (filter != null && b.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var sb = new StringBuilder();
                sb.Append("  ").Append(b.Title).Append(" (total ").Append(b.EffectiveTotal()).Append("): ");
                for (int i = 0; i < b.Buckets.Count; i++)
                {
                    if (i > 0) sb.Append("  ");
                    sb.Append(b.Buckets[i].Name).Append('=').Append(b.Buckets[i].Count);
                }
                Log(sb.ToString());
            }
        }

        private static void Vanilla(string[] p)
        {
            string sub = p.Length > 2 ? p[2].ToLowerInvariant() : "status";
            if (sub == "on") Snitch.Vanilla.VanillaProbes.Enable();
            else if (sub == "off") Snitch.Vanilla.VanillaProbes.Disable();
            else Log("vanilla probes: " + Snitch.Vanilla.VanillaProbes.Status() + " (use 'snitch vanilla on|off')");
        }

        private static void Report(string fmt)
        {
            if (fmt != "md" && fmt != "csv" && fmt != "all") fmt = "all";
            try { string paths = Reporting.ReportWriter.Write(fmt); Log("report written: " + paths); }
            catch (Exception e) { Log("report failed: " + e.Message); }
        }

        private static void Ablate(string[] p)
        {
            if (p.Length <= 2) { Log("usage: snitch ablate <lever>. levers: " + string.Join(", ", Ablation.LeverRegistry.Names)); return; }
            Ablation.AblationEngine.Start(p[2].ToLowerInvariant());
        }

        private static void Counters()
        {
            var rows = SnitchCore.LatestCounters;
            if (rows == null || rows.Count == 0) { Log("counters: none registered."); return; }
            foreach (CounterRow c in rows)
                Log($"  {c.Id,-28} {c.Value,12:F2} {c.Unit} [{c.State}]");
        }

        private static int IntArg(string[] p, int idx, int def)
        {
            if (p.Length > idx && int.TryParse(p[idx], out int v)) return v;
            return def;
        }

        private static float FloatArg(string[] p, int idx, float def)
        {
            if (p.Length > idx && float.TryParse(p[idx], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
            return def;
        }

        private static bool BoolArg(string[] p, int idx, bool toggleDefault)
        {
            if (p.Length <= idx) return toggleDefault;
            string v = p[idx].ToLowerInvariant();
            if (v == "on" || v == "true" || v == "1" || v == "yes") return true;
            if (v == "off" || v == "false" || v == "0" || v == "no") return false;
            return toggleDefault;
        }

        internal static void Log(string msg)
        {
            Core.Log?.Msg("[snitch] " + msg);
            LogHub.Write("Snitch", 0, msg);
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(string) })]
    internal static class Snitch_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !SnitchConsole.TryHandle(args); } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Snitch_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !SnitchConsole.TryHandle(args); } catch { return true; }
        }
    }
}
