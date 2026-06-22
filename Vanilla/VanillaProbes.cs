using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.GameTime;   // TimeManager
using Snitch.Sections;

namespace Snitch.Vanilla
{
    /// <summary>
    /// Attributes per-frame CPU cost to VANILLA hot paths without modifying them. A Harmony
    /// Prefix+Finalizer wraps each target method; one aggregated section label sums the time across ALL
    /// instances (e.g. "Vanilla.NPC.Movement.Update" = total NPCMovement.Update ms/frame across every NPC).
    /// This works in this IL2CPP build (calls scale with the NPC count) and direct
    /// Stopwatch timing is low-noise. Patches are applied on demand ('snitch vanilla on') and stay dormant
    /// (one bool read) until enabled, so they add no cost when off. A single shared prefix/finalizer maps the
    /// original method to its section id via __originalMethod, so adding a target is one Patch() line.
    ///
    /// HONESTY: these numbers are SELF-MEASURED (only what we explicitly wrap) and include a small patch
    /// overhead. They are NOT a sampling profiler. NPCMovement.Update is cheap (~0.06ms/frame) - the dominant
    /// per-NPC cost is native (NavMeshAgent) and is only attributable via the ablation harness.
    /// </summary>
    internal static class VanillaProbes
    {
        internal static volatile bool Enabled;
        private static bool _patched;
        private static readonly Dictionary<MethodBase, int> _ids = new Dictionary<MethodBase, int>();
        private static readonly List<string> _applied = new List<string>();
        private static readonly List<string> _failed = new List<string>();

        internal static void Enable()
        {
            EnsurePatched();
            Enabled = true;
            Core.Log?.Msg("[snitch] vanilla probes ON. " + Status());
        }

        internal static void Disable()
        {
            Enabled = false;
            Core.Log?.Msg("[snitch] vanilla probes OFF (patches stay installed but dormant).");
        }

        internal static string Status()
        {
            return $"enabled={Enabled} applied=[{string.Join(", ", _applied)}] failed=[{string.Join(", ", _failed)}]";
        }

        private static void EnsurePatched()
        {
            if (_patched) return;
            _patched = true;
            Patch(typeof(NPCMovement), "Update", "Vanilla.NPC.Movement.Update");
            Patch(typeof(NPCMovement), "FixedUpdate", "Vanilla.NPC.Movement.FixedUpdate");
            Patch(typeof(TimeManager), "Update", "Vanilla.Time.Update");
        }

        private static void Patch(Type type, string method, string label)
        {
            try
            {
                MethodInfo mi = AccessTools.Method(type, method, Type.EmptyTypes);
                if (mi == null) { _failed.Add(label + "(method not found)"); return; }
                int id = SectionProfiler.GetId(label);
                _ids[mi] = id;
                Core.HarmonyInst.Patch(mi,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaProbes), nameof(SharedPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(VanillaProbes), nameof(SharedFinalizer))));
                _applied.Add(label);
            }
            catch (Exception e) { _failed.Add(label + "(" + e.Message + ")"); }
        }

        // One shared prefix/finalizer for every target; __originalMethod selects the section id. Finalizer form
        // so End runs even if the original throws.
        private static void SharedPrefix(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.Begin(id);
        }

        private static void SharedFinalizer(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.End(id);
        }
    }
}
