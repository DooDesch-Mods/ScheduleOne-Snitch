using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Snitch.Compat;
using Snitch.Engine;

namespace Snitch.Ablation
{
    /// <summary>A toggleable subsystem for the ablation harness. Apply turns it OFF; Restore turns it back ON.</summary>
    internal sealed class AblationLever
    {
        public string Name;
        public Func<bool> CanApply;   // false => skip (e.g. host-only)
        public Action Apply;
        public Action Restore;        // null => terminal (not reversible)
    }

    /// <summary>Registry of ablation levers - built-ins and modder-registered (via the API bridge) alike.</summary>
    internal static class LeverRegistry
    {
        private static readonly Dictionary<string, AblationLever> _levers = new Dictionary<string, AblationLever>(StringComparer.OrdinalIgnoreCase);
        private static bool _builtins;

        internal static void Register(AblationLever l) { if (l != null && !string.IsNullOrEmpty(l.Name)) _levers[l.Name] = l; }
        internal static void RegisterDelegate(string name, Action apply, Action restore) =>
            Register(new AblationLever { Name = name, Apply = apply, Restore = restore, CanApply = () => true });

        internal static AblationLever Get(string name) { EnsureBuiltins(); return _levers.TryGetValue(name, out AblationLever l) ? l : null; }
        internal static IEnumerable<string> Names { get { EnsureBuiltins(); return _levers.Keys; } }

        private static void EnsureBuiltins()
        {
            if (_builtins) return;
            _builtins = true;
            // built-in NPC lever: halt all NPC movement (the dominant per-NPC sim cost). Host-only - it mutates
            // simulation. The game has no pause call since 0.4.7, so the lever disables each NPCMovement and stops its
            // NavMeshAgent, and Restore undoes exactly the NPCs it halted. Disable Siesta for a clean read.
            Register(new AblationLever
            {
                Name = "npc",
                CanApply = () => Net.IsAuthoritative(),
                Apply = HaltNpcMovement,
                Restore = ResumeNpcMovement,
            });
        }

        private static readonly List<NPCMovement> _halted = new List<NPCMovement>();

        private static void HaltNpcMovement()
        {
            _halted.Clear();
            int failed = 0;
            string firstError = null;
            try
            {
                var reg = NPCManager.NPCRegistry;
                if (reg == null) { Core.Log?.Warning("[snitch] ablation npc: NPC registry is not available, nothing halted."); return; }
                int n = reg.Count;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        NPC npc = reg[i];
                        NPCMovement mv = npc != null ? npc.Movement : null;
                        if (mv == null || !mv.enabled) continue;
                        var agent = mv._agent;
                        if (agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = true;
                        mv.enabled = false;
                        _halted.Add(mv);
                    }
                    catch (Exception e) { failed++; if (firstError == null) firstError = e.Message; }
                }
            }
            catch (Exception e) { Core.Log?.Error("[snitch] ablation npc: halting failed: " + e.Message); }
            Core.Log?.Msg($"[snitch] ablation npc: halted {_halted.Count} NPC(s)" + (failed > 0 ? $", {failed} failed, first: {firstError}" : "."));
        }

        private static void ResumeNpcMovement()
        {
            int resumed = 0, failed = 0;
            string firstError = null;
            foreach (NPCMovement mv in _halted)
            {
                try
                {
                    if (mv == null) continue;   // destroyed while halted
                    mv.enabled = true;
                    var agent = mv._agent;
                    if (agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = false;
                    resumed++;
                }
                catch (Exception e) { failed++; if (firstError == null) firstError = e.Message; }
            }
            _halted.Clear();
            Core.Log?.Msg($"[snitch] ablation npc: resumed {resumed} NPC(s)" + (failed > 0 ? $", {failed} failed, first: {firstError}" : "."));
        }
    }

    /// <summary>
    /// Causal A/B harness (advanced): the build-independent way to get "total cost of a subsystem".
    /// For a chosen lever it settles an all-on baseline (warmup + a stddev/mean stability gate), toggles the
    /// subsystem OFF, settles again, and reports the frame-time DELTA - which IS that subsystem's cost. Slower
    /// and more invasive than the section timers, but it sees native cost (e.g. NavMeshAgent) the Harmony
    /// probes can't. Generalized from Litterally's AblationController; writes a CSV to Mods/Snitch/runs/.
    /// </summary>
    internal static class AblationEngine
    {
        private enum S { Idle, BaseWarm, OffWarm }
        private const int WarmupFrames = 120;
        private const int MaxExtraFrames = 600;
        private const double NoiseThreshold = 0.22;

        private static S _state = S.Idle;
        private static int _timer, _extra;
        private static double _baseMs;
        private static AblationLever _lever;

        internal static bool Active => _state != S.Idle;
        internal static string Status { get; private set; } = "idle";

        internal static void Start(string name)
        {
            if (Active) { Core.Log?.Warning("[snitch] ablation already running."); return; }
            AblationLever l = LeverRegistry.Get(name);
            if (l == null) { Core.Log?.Warning($"[snitch] no lever '{name}'. Available: {string.Join(", ", LeverRegistry.Names)}"); return; }
            if (l.CanApply != null && !l.CanApply()) { Core.Log?.Warning($"[snitch] lever '{name}' not applicable here (host-only?)."); return; }
            if (!SnitchCore.Active) SnitchCore.Start();
            _lever = l;
            FrameSampler.UncapFramerate();
            EnterGate();
            _state = S.BaseWarm;
            Status = name + ": baseline";
            Core.Log?.Msg($"[snitch] ablation '{name}' started - settling all-on baseline (uncapped). 'snitch ablate' status via 'snitch status'.");
        }

        internal static void Abort(string why)
        {
            if (!Active) return;
            try { _lever?.Restore?.Invoke(); } catch { }
            FrameSampler.RestoreFramerate();
            _state = S.Idle; Status = "idle";
            Core.Log?.Warning("[snitch] ablation aborted: " + why);
        }

        internal static void Tick()
        {
            if (_state == S.Idle) return;
            switch (_state)
            {
                case S.BaseWarm:
                    if (GateReady())
                    {
                        _baseMs = FrameSampler.Snapshot().MeanMs;
                        try { _lever.Apply?.Invoke(); } catch (Exception e) { Core.Log?.Warning("[snitch] lever apply failed: " + e.Message); Abort("apply failed"); return; }
                        EnterGate();
                        _state = S.OffWarm;
                        Status = _lever.Name + ": off";
                    }
                    break;
                case S.OffWarm:
                    if (GateReady())
                    {
                        double offMs = FrameSampler.Snapshot().MeanMs;
                        try { _lever.Restore?.Invoke(); } catch { }
                        FrameSampler.RestoreFramerate();
                        double delta = _baseMs - offMs;
                        double pct = _baseMs > 0 ? delta / _baseMs * 100.0 : 0.0;
                        Core.Log?.Msg($"[snitch] ablation '{_lever.Name}': baseline={_baseMs:F2}ms  off={offMs:F2}ms  => cost ~= {delta:F2} ms/frame ({pct:F0}% of frame).");
                        WriteCsv(_lever.Name, _baseMs, offMs, delta, pct);
                        _state = S.Idle; Status = "idle";
                    }
                    break;
            }
        }

        private static void EnterGate() { _timer = WarmupFrames; _extra = MaxExtraFrames; }

        private static bool GateReady()
        {
            if (_timer > 0) { _timer--; return false; }
            double noise = FrameSampler.RelativeNoiseCheap();   // allocation-free: must not pollute the measurement
            if (noise <= NoiseThreshold || _extra <= 0) return true;
            _extra--;
            return false;
        }

        private static void WriteCsv(string lever, double baseMs, double offMs, double delta, double pct)
        {
            try
            {
                string dir = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Snitch", "runs");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(dir, $"ablate_{lever}_{stamp}.csv");
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("lever,baselineMs,offMs,deltaMs,pctOfFrame");
                sb.AppendLine($"{lever},{F(baseMs)},{F(offMs)},{F(delta)},{F(pct)}");
                File.WriteAllText(path, sb.ToString());
                Core.Log?.Msg("[snitch] ablation CSV: " + path);
            }
            catch (Exception e) { Core.Log?.Warning("[snitch] ablation CSV failed: " + e.Message); }
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
