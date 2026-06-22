using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Snitch.Sections;

namespace Snitch.Vanilla
{
    /// <summary>
    /// Auto-instrumentation: when sampling is armed, every OTHER loaded MelonMod's overridden lifecycle methods
    /// (OnUpdate / OnFixedUpdate / OnLateUpdate / OnGUI) are Harmony-wrapped and timed, labelled by mod name
    /// (e.g. "Siesta.OnUpdate"). This gives per-mod frame cost for ZERO integration code - a mod author writes
    /// nothing to show up in the profiler's frame budget. One aggregated label per (mod, method); a shared
    /// prefix/finalizer keyed by the original method picks the section. Gated on a single bool so the patches
    /// are dormant (one read) until sampling starts. Snitch itself is skipped (it already reports Snitch.Self).
    /// </summary>
    internal static class AutoInstrument
    {
        internal static volatile bool Enabled;
        private static bool _patched;
        private static readonly Dictionary<MethodBase, int> _ids = new Dictionary<MethodBase, int>();
        private static readonly string[] Lifecycle = { "OnUpdate", "OnFixedUpdate", "OnLateUpdate", "OnGUI" };

        private static bool _discovered;

        internal static void Enable()
        {
            EnsurePatched();
            Enabled = true;
        }

        internal static void Disable() => Enabled = false;

        internal static int InstrumentedCount => _ids.Count;

        /// <summary>
        /// Convention discovery (runs once, independent of the auto-instrument toggle): invoke each OTHER loaded
        /// mod's static <c>SnitchProbe.Register()</c> so a mod's counters/state-providers register with ZERO wiring
        /// in its own Core. The mod's embedded shim binds to the host the moment Register() touches it (the host is
        /// loaded by the time sampling starts), then registers. A mod without a SnitchProbe (e.g. any Release build,
        /// where the probe file is excluded) is simply skipped.
        /// </summary>
        internal static void DiscoverProbes()
        {
            if (_discovered) return;
            _discovered = true;
            int found = 0;
            try
            {
                IEnumerable<MelonMod> mods = RegisteredMods();
                if (mods == null) return;
                foreach (MelonMod mod in mods)
                {
                    if (mod == null || ReferenceEquals(mod, Core.Instance)) continue;
                    try
                    {
                        Assembly asm = mod.GetType().Assembly;
                        Type probe = asm.GetType("SnitchProbe", false) ?? FindLeaf(asm, "SnitchProbe");
                        MethodInfo reg = probe?.GetMethod("Register",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                        if (reg == null) continue;
                        reg.Invoke(null, null);
                        found++;
                    }
                    catch (Exception e) { Core.Log?.Warning($"[snitch] probe discovery on {ModName(mod)} failed: {e.Message}"); }
                }
                if (found > 0) Core.Log?.Msg($"[snitch] discovered + registered {found} mod probe(s).");
            }
            catch (Exception e) { Core.Log?.Warning("[snitch] probe discovery failed: " + e.Message); }
        }

        private static Type FindLeaf(Assembly asm, string leaf)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { return null; }
            if (types == null) return null;
            foreach (Type t in types) if (t != null && t.Name == leaf) return t;
            return null;
        }

        private static void EnsurePatched()
        {
            if (_patched) return;
            _patched = true;
            try
            {
                IEnumerable<MelonMod> mods = RegisteredMods();
                if (mods == null) { Core.Log?.Warning("[snitch] auto-instrument: could not enumerate mods."); return; }

                MethodInfo pre = AccessTools.Method(typeof(AutoInstrument), nameof(Pre));
                MethodInfo fin = AccessTools.Method(typeof(AutoInstrument), nameof(Fin));

                foreach (MelonMod mod in mods)
                {
                    if (mod == null || ReferenceEquals(mod, Core.Instance)) continue;   // never instrument Snitch itself
                    string name = ModName(mod);
                    Type t = mod.GetType();
                    for (int i = 0; i < Lifecycle.Length; i++)
                    {
                        MethodInfo mi = t.GetMethod(Lifecycle[i],
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (mi == null || mi.IsAbstract || mi.DeclaringType == typeof(MelonMod) || _ids.ContainsKey(mi)) continue;
                        try
                        {
                            Core.HarmonyInst.Patch(mi, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                            _ids[mi] = SectionProfiler.GetId(name + "." + Lifecycle[i]);
                        }
                        catch (Exception e) { Core.Log?.Warning($"[snitch] auto-instrument {name}.{Lifecycle[i]} failed: {e.Message}"); }
                    }
                }
                Core.Log?.Msg($"[snitch] auto-instrumented {_ids.Count} mod lifecycle method(s) across other mods.");
            }
            catch (Exception e) { Core.Log?.Warning("[snitch] auto-instrument failed: " + e.Message); }
        }

        // One shared prefix/finalizer for every patched method; __originalMethod selects the section id.
        private static void Pre(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.Begin(id);
        }

        private static void Fin(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.End(id);
        }

        // ----- MelonLoader interop (defensive: the registry/Info API has shifted across versions) -----

        private static IEnumerable<MelonMod> RegisteredMods()
        {
            try { return MelonMod.RegisteredMelons; }
            catch { }
            // fallback: reflect a static RegisteredMelons on MelonMod / MelonBase
            foreach (Type t in new[] { typeof(MelonMod), typeof(MelonBase) })
            {
                try
                {
                    PropertyInfo p = t.GetProperty("RegisteredMelons", BindingFlags.Public | BindingFlags.Static);
                    object v = p?.GetValue(null);
                    if (v is IEnumerable<MelonMod> mods) return mods;
                }
                catch { }
            }
            return null;
        }

        private static string ModName(MelonMod mod)
        {
            string name = null;
            try { name = mod.Info?.Name; } catch { }
            if (string.IsNullOrEmpty(name)) name = mod.GetType().Namespace ?? mod.GetType().Name;
            // group prefix splits on the first '.', so keep the name dot-free for a clean group
            return name.Replace('.', '_').Replace(' ', '_');
        }
    }
}
