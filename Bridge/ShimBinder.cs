using System;
using System.Collections;
using System.Reflection;

namespace Snitch.Bridge
{
    /// <summary>
    /// Binds every mod's embedded Snitch.Api shim from the host side.
    ///
    /// A shim only looks for the host when its own mod calls the API. A mod that registers its panels before Snitch
    /// loads and then stops calling would keep them queued forever, and shims from before 1.7.1 make that likely: they
    /// probed on every 30th call only, so a probe that registers fifteen controls never reached the second probe.
    /// The host therefore calls into each shim once it is up. New shims expose <c>BindNow()</c>; older ones get their
    /// call counter reset and <c>EnsureBound()</c> called, which makes their next probe the one that binds.
    /// </summary>
    internal static class ShimBinder
    {
        private const string ShimType = "Snitch.Api.Profiler";
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        internal static void BindAll()
        {
            Assembly self = typeof(ShimBinder).Assembly;
            int bound = 0, already = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(asm, self)) continue;
                Type shim;
                try { shim = asm.GetType(ShimType, false); }
                catch (Exception e) { Core.Log?.Warning($"[snitch] shim scan: {Name(asm)} would not resolve {ShimType}: {e.Message}"); continue; }
                if (shim == null) continue;

                if (IsBound(shim)) { already++; continue; }
                if (Bind(shim, Name(asm))) bound++;
            }
            Core.Log?.Msg($"[snitch] shim scan: bound {bound} mod shim(s), {already} had bound themselves.");
        }

        /// <summary>True when this mod's own <c>Profiler.AutoRegister()</c> already ran its SnitchProbe.</summary>
        internal static bool ProbeAlreadyRan(Assembly modAssembly) =>
            Field(modAssembly, "_autoDone") is bool done && done;

        /// <summary>Latches the shim's AutoRegister after the host ran the probe, so the probe does not run twice.</summary>
        internal static void MarkProbeRan(Assembly modAssembly)
        {
            try { modAssembly.GetType(ShimType, false)?.GetField("_autoDone", Static)?.SetValue(null, true); }
            catch (Exception e) { Core.Log?.Warning($"[snitch] could not latch the probe of {Name(modAssembly)}; it may register twice: {e.Message}"); }
        }

        private static bool Bind(Type shim, string mod)
        {
            try
            {
                int queued = (shim.GetField("_pending", Static)?.GetValue(null) as ICollection)?.Count ?? 0;
                MethodInfo bindNow = shim.GetMethod("BindNow", Static, null, Type.EmptyTypes, null);
                if (bindNow != null)
                {
                    bindNow.Invoke(null, null);
                }
                else
                {
                    // Shim from before 1.7.1: EnsureBound only probes when its call counter is a multiple of 30.
                    FieldInfo attempts = shim.GetField("_probeAttempts", Static);
                    MethodInfo ensure = shim.GetMethod("EnsureBound", Static, null, Type.EmptyTypes, null);
                    if (attempts == null || ensure == null)
                    {
                        Core.Log?.Warning($"[snitch] shim in {mod} has neither BindNow nor EnsureBound; its queued registrations stay queued.");
                        return false;
                    }
                    attempts.SetValue(null, 0);
                    ensure.Invoke(null, null);
                }

                if (IsBound(shim))
                {
                    Core.Log?.Msg($"[snitch] bound the shim in {mod}, applied {queued} queued registration(s).");
                    return true;
                }
                string why = shim.GetField("_lastBindError", Static)?.GetValue(null) as string ?? "no reason recorded";
                Core.Log?.Warning($"[snitch] shim in {mod} did not bind: {why}");
                return false;
            }
            catch (Exception e)
            {
                Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Core.Log?.Error($"[snitch] binding the shim in {mod} threw: {inner.GetType().Name}: {inner.Message}");
                return false;
            }
        }

        private static bool IsBound(Type shim) => shim.GetField("_bound", Static)?.GetValue(null) is bool b && b;

        private static object Field(Assembly asm, string field)
        {
            try { return asm.GetType(ShimType, false)?.GetField(field, Static)?.GetValue(null); }
            catch (Exception e) { Core.Log?.Warning($"[snitch] could not read {field} of the shim in {Name(asm)}: {e.Message}"); return null; }
        }

        private static string Name(Assembly asm)
        {
            try { return asm.GetName().Name; }
            catch (Exception e) { Core.Log?.Warning("[snitch] assembly without a readable name: " + e.Message); return "?"; }
        }
    }
}
