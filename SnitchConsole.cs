using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;
using Snitch.Sections;
using Snitch.Server;
using Snitch.Vanilla;

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
                    case "unattributed":
                    case "unattr": Unattributed(); break;
                    case "patches": PatchesCmd(p); break;
                    case "panels": PanelsList(); break;
                    case "act": ActCmd(p); break;
                    case "toggle": ToggleCmd(p); break;
                    case "slider": SliderCmd(p); break;
                    case "open": OverlayCmd(true, p); break;
                    case "close": OverlayCmd(false, p); break;
                    case "dashboard": Core.OpenDashboard(); break;
                    case "log": LogCmd(p); break;
                    case "vanilla": Vanilla(p); break;
                    case "lan": Lan(p); break;
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
            Log("commands: open [all] | close [all] | start | stop | status | frame | top [n] | sections | states [id] | counters | "
                + "unattributed | patches [on|off|list|status] | "
                + "panels | act <actionId> | toggle <toggleId> [on|off] | slider <sliderId> [value] | dashboard | log [<channel>|all] [n] | "
                + "vanilla [on|off] | lan [on|off] | ablate <lever> | levers | report [md|csv|all]  "
                + "('open' shows the Snitch panel in the Hotline overlay; 'open all' shows the whole overlay)");
        }

        // ----- per-mod panels (data only; the in-game overlay windows are owned by the Hotline framework) -----

        /// <summary>
        /// Show or hide the profiler's in-game panel: <c>snitch open</c> / <c>snitch close</c>, and
        /// <c>open all</c> / <c>close all</c> for the whole overlay.
        /// <para>
        /// The overlay used to be reachable only by pressing Hotline's master key. A keypress cannot be scripted
        /// or checked from outside the game, so the profiler could not be driven - or verified - without someone
        /// at the keyboard. Everything else Snitch does is a console command; this closes the gap.
        /// </para>
        /// </summary>
        private static void OverlayCmd(bool show, string[] p)
        {
            bool whole = p.Length > 2 && p[2].ToLowerInvariant() == "all";

            if (!Hotline.Api.Hud.Available)
            {
                Log("the in-game overlay needs the Hotline mod - install it, or use 'snitch dashboard' for the web view.");
                return;
            }

            if (whole)
            {
                Hotline.Api.Hud.ShowOverlay(show);
                Log("overlay " + (show ? "shown" : "hidden") + ".");
                return;
            }

            Hotline.Api.Hud.ShowPanel(PanelId, show);
            Log($"Snitch panel {(show ? "shown" : "hidden")}"
                + (show ? " (use 'snitch open all' for every panel)." : "."));
        }

        /// <summary>The panel id Snitch registers with Hotline; kept here so the console and Core cannot drift.</summary>
        internal const string PanelId = "Snitch";

        private static void PanelsList()
        {
            var panels = PanelRegistry.All;
            if (panels.Count == 0) { Log("no mod panels registered yet (enter the world; panels register on probe discovery)."); return; }
            Log($"{panels.Count} panel(s) (toggle their windows in the Hotline overlay):");
            for (int i = 0; i < panels.Count; i++)
            {
                PanelModel p = panels[i];
                Log($"  {p.Id,-16} actions={p.Actions.Count} toggles={p.Toggles.Count} sliders={p.Sliders.Count} title=\"{p.Title}\"");
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

        /// <summary>
        /// Read or write a slider from the console. A slider is a mouse control, and a mouse control cannot be driven
        /// by an automated harness - this is the same value reachable by typing, which keeps a tuning session
        /// reproducible and lets a found value be read back and written into code.
        /// </summary>
        private static void SliderCmd(string[] p)
        {
            if (p.Length <= 2) { Log("usage: snitch slider <sliderId> [value] (omit the value to read it). 'snitch panels' lists panels."); return; }

            string id = p[2];
            SliderItem s = PanelRegistry.GetSlider(id);
            if (s == null) { Log("no slider '" + id + "'"); return; }

            if (p.Length <= 3)
            {
                Log($"{id} = {s.Read():0.###} {s.Unit} (range {s.Min:0.###}..{s.Max:0.###}, step {s.Step:0.###})".Replace("  ", " ").TrimEnd());
                return;
            }

            if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                Log("not a number: " + p[3]);
                return;
            }

            PanelRegistry.SetSlider(id, v);
            Log($"{id} = {s.Read():0.###} {s.Unit}".TrimEnd());
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
            AttributionStats a = SnitchCore.LatestAttribution;
            if (a.Samples > 0)
                Log($"unattributed={a.UnattributedMeanMs:F2}ms/f ({a.UnattributedPct:F0}% of the frame) "
                  + $"badFrames={a.SpikeFrames} patchTiming={(Snitch.Vanilla.PatchInstrument.Enabled ? "on" : "off")} "
                  + "('snitch unattributed' explains)");
            if (!SnitchCore.Active) Log("(idle - run 'snitch start' to begin sampling)");
        }

        private static void Frame()
        {
            FrameStats f = SnitchCore.LatestFrame;
            Log($"frame: mean={f.MeanMs:F2}ms median={f.MedianMs:F2} p95={f.P95Ms:F2} p99={f.P99Ms:F2} " +
                $"min={f.MinMs:F2} max={f.MaxMs:F2} | fps mean={f.MeanFps:F0} min={f.MinFps:F0} | " +
                $"gc0/1000f={f.Gc0Per1000:F1} gc1/1000f={f.Gc1Per1000:F1} samples={f.Samples}");

            AttributionStats a = SnitchCore.LatestAttribution;
            if (a.Samples == 0) return;
            Log($"attribution: sections={a.AttributedMeanMs:F2}ms/f unattributed={a.UnattributedMeanMs:F2}ms/f "
              + $"({a.UnattributedPct:F0}% of the frame, worst {a.MaxUnattributedMs:F2})"
              + (a.SpikeFrames > 0
                  ? $" | {a.SpikeFrames} bad frame(s), {a.SpikeUnexplainedPct:F0}% of their excess unexplained"
                  : " | no bad frames in the window"));
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
                if (r.Label == Engine.Attribution.RowLabel)
                {
                    // Not a call-based section: this row is the frame time NOTHING here explains, so it gets the
                    // pointer instead of a calls column that would read as zero work.
                    Log($"  {r.Label,-28} {r.MsPerFrame,7:F3} ms/f  {r.PctFrame,5:F1}%  inside no section  (max {r.MaxMs:F3})"
                      + "  <- 'snitch unattributed'");
                    continue;
                }
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

        private static void Lan(string[] p)
        {
            string sub = p.Length > 2 ? p[2].ToLowerInvariant() : "status";
            if (sub == "on")
            {
                if (LanServer.Running) { Log("phone remote already on - " + LanUrl()); return; }
                Preferences.LanAccess = true;
                SavePreferences();
                LanServer.Start(Preferences.LanPort);
                if (!RelayHost.Running) RelayHost.Start(System.Guid.NewGuid().ToString("N").Substring(0, 12));
                Log(LanServer.Running ? "phone remote ON - " + LanUrl() + " (+ relay for other networks; scan the QR from your phone)"
                                      : "phone remote failed to start - see the log (port in use? change LanPort).");
            }
            else if (sub == "off")
            {
                Preferences.LanAccess = false;
                SavePreferences();
                RelayHost.Stop();
                LanServer.Stop();
                Log("phone remote OFF.");
            }
            else
            {
                Log(LanServer.Running
                    ? "LAN remote ON - " + LanUrl()
                    : "LAN remote OFF (use 'snitch lan on'). Lets a phone on your Wi-Fi open the dashboard as a remote.");
            }
        }

        private static string LanUrl() => $"http://{LanServer.Ip}:{LanServer.Port}/ (token {LanServer.Token})";

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

        /// <summary>
        /// The frame time no section explains. Snitch already samples frame time, and the section accumulators
        /// already know how much of a frame ran inside something it wraps; the difference is a fact worth reporting
        /// on its own, because a profiler that lists only what it happens to wrap implies the rest is fine.
        ///
        /// The mean is context, not the finding: most of any frame is the game itself. The finding is the bad-frame
        /// split - when a frame is far worse than the median and the sections did not grow with it, the extra
        /// milliseconds are running somewhere the profiler does not reach.
        /// </summary>
        private static void Unattributed()
        {
            AttributionStats a = SnitchCore.LatestAttribution;
            if (a.Samples == 0)
            {
                Log("unattributed: nothing measured yet" + (SnitchCore.Active ? " (give it a second)." : " - run 'snitch start' first."));
                return;
            }

            Log($"unattributed: {a.UnattributedMeanMs:F2} ms/frame of a {a.FrameMeanMs:F2} ms frame ({a.UnattributedPct:F0}%) - inside no section at all.");
            Log($"  sections account for {a.AttributedMeanMs:F2} ms/frame; the worst single frame left {a.MaxUnattributedMs:F2} ms unexplained.");

            if (a.SpikeFrames == 0)
            {
                Log($"  bad frames: none in the last {a.Samples} (nothing above {a.SpikeFactor:F1}x the {a.FrameMedianMs:F2} ms median).");
            }
            else
            {
                Log($"  bad frames: {a.SpikeFrames} of {a.Samples} above {a.SpikeFactor:F1}x the {a.FrameMedianMs:F2} ms median; "
                  + $"on those, {a.SpikeMeanUnexplainedMs:F2} ms of the {a.SpikeMeanExcessMs:F2} ms excess ({a.SpikeUnexplainedPct:F0}%) is unexplained "
                  + $"(worst {a.WorstUnexplainedMs:F2} ms).");
            }

            Log("  most of an unattributed frame is the game itself and always will be; the number that points at a mod "
              + "is the unexplained share of the bad frames.");
            Log("  " + NextStep(a));
        }

        /// <summary>What to do about an unexplained frame, given what is already switched on. This is the line the
        /// unattributed report exists for: without it the number is a shrug.</summary>
        private static string NextStep(AttributionStats a)
        {
            if (!a.PointsAtHiddenWork)
                return "the bad frames are explained by sections that got worse, so 'snitch top' has the answer.";

            if (!Snitch.Vanilla.PatchInstrument.Enabled)
                return "-> run 'snitch patches on'. A mod's per-frame work does not have to live in OnUpdate: a Harmony "
                     + "postfix on a vanilla method the engine calls every frame belongs to no section, so the mod reads as cheap.";

            return $"-> patch timing is already on ({Snitch.Vanilla.PatchInstrument.WrappedCount} patches wrapped) and still nothing "
                 + "accounts for it. What is left: a coroutine, an event handler, a Unity message on a MonoBehaviour a mod added, "
                 + "or the game itself - 'snitch vanilla on' and 'snitch ablate' cover the last two.";
        }

        /// <summary>
        /// Time other mods' Harmony patches: <c>snitch patches on | off | list | status</c>.
        /// Opt-in, because wrapping hundreds of patch methods costs measurable time of its own and a failed Harmony
        /// patch leaves its target broken for every later patcher. 'on' rescans, so it also picks up a mod that
        /// patched late.
        /// </summary>
        private static void PatchesCmd(string[] p)
        {
            string sub = p.Length > 2 ? p[2].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "on":
                    Snitch.Vanilla.PatchInstrument.Enable();
                    Log(Snitch.Vanilla.PatchInstrument.Status());
                    Log("  numbers include the wrapper's own overhead, the same as the vanilla probes. "
                      + "Run 'snitch patches on' again after a late-loading mod has patched.");
                    ReportSkipsAndFailures();
                    break;

                case "off":
                    Snitch.Vanilla.PatchInstrument.Disable();
                    Log("patch timing OFF (the wrappers stay installed but dormant).");
                    break;

                case "list": PatchesList(); break;

                default:
                    Log("patch timing: " + Snitch.Vanilla.PatchInstrument.Status());
                    if (!Snitch.Vanilla.PatchInstrument.EverScanned)
                        Log("  nothing scanned yet - 'snitch patches on' wraps every loaded mod's Harmony prefixes, postfixes and finalizers.");
                    ReportSkipsAndFailures();
                    break;
            }
        }

        /// <summary>Every patch that was left alone, by owner, and every wrap that failed. A skipped patch that is
        /// never named is indistinguishable from a patch that does not exist.</summary>
        private static void ReportSkipsAndFailures()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in Snitch.Vanilla.PatchInstrument.SkippedOwners)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append(' ').Append(kv.Value);
            }
            if (sb.Length > 0) Log("  not a mod's patch, left alone: " + sb);

            int failed = Snitch.Vanilla.PatchInstrument.FailureCount;
            if (failed == 0) return;
            Log($"  {failed} wrap(s) FAILED - those methods may now be unusable for later patchers:");
            foreach (string f in Snitch.Vanilla.PatchInstrument.Failures) Log("    " + f);
        }

        /// <summary>The wrapped patches with what they currently cost, worst first.</summary>
        private static void PatchesList()
        {
            List<WrappedPatch> all = Snitch.Vanilla.PatchInstrument.All();
            if (all.Count == 0)
            {
                Log("no patches wrapped. 'snitch patches on' wraps every loaded mod's Harmony prefixes, postfixes and finalizers.");
                return;
            }

            var costByLabel = new Dictionary<string, SectionRow>(StringComparer.Ordinal);
            var rows = SnitchCore.LatestSections;
            if (rows != null)
                for (int i = 0; i < rows.Count; i++) costByLabel[rows[i].Label] = rows[i];

            all.Sort((x, y) => Cost(costByLabel, y).CompareTo(Cost(costByLabel, x)));
            Log($"{all.Count} wrapped patch(es), worst first:");
            for (int i = 0; i < all.Count; i++)
            {
                WrappedPatch w = all[i];
                costByLabel.TryGetValue(w.Label, out SectionRow r);
                string targets = string.Join(", ", w.Targets);
                if (w.TargetCount > w.Targets.Count) targets += $", +{w.TargetCount - w.Targets.Count} more";
                Log($"  {w.Label,-38} {r.MsPerFrame,7:F3} ms/f  {r.Calls,7:F0} calls/f  (max {r.MaxMs:F3})  "
                  + $"[{string.Join("+", w.Kinds)}] on {targets}");
            }
            if (!Snitch.Vanilla.PatchInstrument.Enabled)
                Log("  timing is OFF, so those numbers are stale - 'snitch patches on' arms them.");
        }

        private static double Cost(Dictionary<string, SectionRow> byLabel, WrappedPatch w)
        {
            return byLabel.TryGetValue(w.Label, out SectionRow r) ? r.MsPerFrame : 0.0;
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

        /// <summary>Persist the preference file. A refusal is not fatal - the setting is already live for this
        /// session, it just will not survive a restart - so it warns and carries on rather than aborting the
        /// command the player actually typed.</summary>
        private static void SavePreferences()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Core.Log?.Warning("[snitch] could not write MelonPreferences (the setting is live for this session only): " + e.Message); }
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
            try { return !SnitchConsole.TryHandle(args); }
            catch (Exception e)
            {
                // The console belongs to the game: whatever went wrong here, the command still has to reach it.
                Core.Log?.Error("[snitch] console prefix (string) threw, handing the command to the game: " + e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), "SubmitCommand", new System.Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Snitch_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !SnitchConsole.TryHandle(args); }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] console prefix (list) threw, handing the command to the game: " + e);
                return true;
            }
        }
    }
}
