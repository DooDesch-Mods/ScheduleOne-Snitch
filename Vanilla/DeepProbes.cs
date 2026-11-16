using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Snitch.Sections;

namespace Snitch.Vanilla
{
    /// <summary>
    /// Times the game's own scripts per class: every Update, LateUpdate and FixedUpdate declared on a MonoBehaviour of
    /// the game assembly, and every coroutine body (the MoveNext of a compiler-generated iterator), each under the
    /// label "Game.&lt;Class&gt;.&lt;Method&gt;".
    ///
    /// The engine timing names the step a spike lands in ("Update scripts", "coroutines") but not the script. This is
    /// the next level down. It is heavy and opt-in: hundreds of patches go in, and every wrapped call pays a
    /// native-to-managed round trip, so frame times read higher while it is on. Spike attribution compares a frame
    /// against the median of the same wrapped run, so that constant overhead cancels out.
    ///
    /// Patches go in a batch per frame from <see cref="Step"/>, with one log line per batch naming its last method:
    /// a native crash inside a detour leaves no exception, so the last batch line is the only pointer to the method.
    /// </summary>
    internal static class DeepProbes
    {
        internal static volatile bool Enabled;
        private static bool _collected;
        private static readonly Dictionary<MethodBase, int> _ids = new Dictionary<MethodBase, int>();
        private static readonly List<KeyValuePair<MethodInfo, string>> _queue = new List<KeyValuePair<MethodInfo, string>>();
        private static int _next;
        private static int _failed;
        private static int _skippedGeneric;
        private static long _patchMs;
        private static readonly string[] Unity = { "Update", "LateUpdate", "FixedUpdate" };
        private const int PerFrame = 5;

        internal static int Count => _ids.Count;
        internal static bool Pending => _next < _queue.Count;

        internal static string Enable()
        {
            if (!_collected) Collect();
            Enabled = true;
            return Pending
                ? $"deep probes ON: wrapping {_queue.Count - _next} game methods, {PerFrame} per frame."
                : $"deep probes ON: {_ids.Count} game methods wrapped, {_failed} failed.";
        }

        internal static void Disable() => Enabled = false;

        /// <summary>Applies the next batch of queued patches. Called once per frame while sampling.</summary>
        internal static void Step()
        {
            if (!Enabled || !Pending) return;
            MethodInfo pre = AccessTools.Method(typeof(DeepProbes), nameof(Pre));
            MethodInfo fin = AccessTools.Method(typeof(DeepProbes), nameof(Fin));
            var sw = Stopwatch.StartNew();
            int end = Math.Min(_next + PerFrame, _queue.Count);
            var names = new System.Text.StringBuilder(160);
            for (; _next < end; _next++)
            {
                var kv = _queue[_next];
                names.Append(names.Length > 0 ? ", " : "").Append(kv.Value);
                try
                {
                    _ids[kv.Key] = SectionProfiler.GetId("Game." + kv.Value);
                    Core.HarmonyInst.Patch(kv.Key, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                }
                catch (Exception e)
                {
                    _ids.Remove(kv.Key);
                    _failed++;
                    Core.Log?.Warning($"[snitch] deep probes: could not wrap {kv.Value} - {e.Message}");
                }
            }
            _patchMs += sw.ElapsedMilliseconds;
            Core.Log?.Msg($"[snitch] deep probes: {_next}/{_queue.Count} wrapped ({names}).");
            if (!Pending)
                Core.Log?.Msg($"[snitch] deep probes: wrapped {_ids.Count} game methods in {_patchMs} ms of patching, {_failed} failed, {_skippedGeneric} generic types skipped.");
        }

        private static void Collect()
        {
            _collected = true;
            Assembly game = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "Assembly-CSharp") { game = a; break; }
            if (game == null) { Core.Log?.Error("[snitch] deep probes: Assembly-CSharp is not loaded (guard)."); return; }

            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                Core.Log?.Warning("[snitch] deep probes: only part of the game types loaded - " + e.Message);
            }

            Type mb = typeof(UnityEngine.MonoBehaviour);
            foreach (Type t in types)
            {
                if (t == null || t.IsAbstract && t.IsSealed) continue;
                try
                {
                    // An open generic has no single native body to detour; Harmony cannot patch it.
                    if (t.ContainsGenericParameters) { _skippedGeneric++; continue; }
                    if (mb.IsAssignableFrom(t))
                    {
                        foreach (string m in Unity)
                        {
                            MethodInfo mi = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                            if (mi != null && !mi.IsAbstract) _queue.Add(new KeyValuePair<MethodInfo, string>(mi, Clean(t.Name) + "." + m));
                        }
                    }
                    else if (t.IsNested && t.Name.Contains("_d__"))
                    {
                        MethodInfo mi = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (mi != null) _queue.Add(new KeyValuePair<MethodInfo, string>(mi, Clean(t.DeclaringType.Name) + "." + Coroutine(t.Name)));
                    }
                }
                catch (Exception e)
                {
                    Core.Log?.Warning($"[snitch] deep probes: {t.FullName} would not list its methods - {e.Message}");
                }
            }
            int before = _queue.Count;
            HashSet<IntPtr> shared = SharedFunctions(out long[] starts);
            if (shared == null)
            {
                _queue.Clear();
                Core.Log?.Error("[snitch] deep probes: the game's native functions could not be listed, so nothing is wrapped (a detour on folded code can hang the game).");
                return;
            }
            var seen = new HashSet<IntPtr>();
            int folded = 0, noNative = 0, small = 0;
            _queue.RemoveAll(kv =>
            {
                IntPtr fn = NativeFunction(kv.Key);
                if (fn == IntPtr.Zero) { noNative++; return true; }
                if (shared.Contains(fn) || !seen.Add(fn)) { folded++; return true; }
                if (GapToNext(starts, fn.ToInt64()) < MinBody) { small++; return true; }
                return false;
            });
            Core.Log?.Msg($"[snitch] deep probes: {_queue.Count} of {before} game methods queued; {folded} skipped because IL2CPP shares their code with another method, "
                        + $"{small} too small to detour, {noNative} without a native body, {_skippedGeneric} generic types skipped.");
        }

        /// <summary>
        /// A detour writes a jump of about 14 bytes over the start of the function. A body shorter than that (a
        /// one-line setter, a method that only returns) would have the jump run into the next function, which then
        /// crashes the next time it is called. Function sizes are not stored, so the distance to the next known
        /// function start stands in for it, with a margin for the hook's own instruction alignment.
        /// </summary>
        private const long MinBody = 32;

        private static long GapToNext(long[] starts, long fn)
        {
            int i = Array.BinarySearch(starts, fn);
            if (i < 0) return 0;
            return i + 1 < starts.Length ? starts[i + 1] - fn : long.MaxValue;
        }

        /// <summary>The native function a generated method runs (Il2CppMethodInfo starts with its methodPointer), or zero.</summary>
        private static IntPtr NativeFunction(MethodBase method)
        {
            try
            {
                FieldInfo field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field == null) return IntPtr.Zero;
                var info = (IntPtr)field.GetValue(null);
                return info == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(info);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[snitch] deep probes: native function of {method.DeclaringType?.Name}.{method.Name} unreadable, not wrapped - {e.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Native functions that more than one method compiles to. IL2CPP folds identical bodies (every empty Update,
        /// every plain setter) into one function; a detour sits on the address, so it would run for all of them, and two
        /// detours on one address chain into each other. Read from the running game because the folding changes per build.
        /// </summary>
        private static HashSet<IntPtr> SharedFunctions(out long[] starts)
        {
            starts = null;
            try
            {
                var first = new HashSet<IntPtr>();
                var shared = new HashSet<IntPtr>();
                UIntPtr count = UIntPtr.Zero;
                IntPtr assemblies = DomainAssemblies(Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get(), ref count);
                for (ulong a = 0; a < (ulong)count; a++)
                {
                    IntPtr image = Il2CppInterop.Runtime.IL2CPP.il2cpp_assembly_get_image(Marshal.ReadIntPtr(assemblies, (int)a * IntPtr.Size));
                    uint classes = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class_count(image);
                    for (uint c = 0; c < classes; c++)
                    {
                        IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class(image, c);
                        if (klass == IntPtr.Zero) continue;
                        IntPtr iter = IntPtr.Zero, m;
                        while ((m = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                        {
                            IntPtr fn = Marshal.ReadIntPtr(m);
                            if (fn != IntPtr.Zero && !first.Add(fn)) shared.Add(fn);
                        }
                    }
                }
                starts = new long[first.Count];
                int k = 0;
                foreach (IntPtr p in first) starts[k++] = p.ToInt64();
                Array.Sort(starts);
                return shared;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] deep probes: listing native functions failed - " + e);
                return null;
            }
        }

        [DllImport("GameAssembly", EntryPoint = "il2cpp_domain_get_assemblies", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr DomainAssemblies(IntPtr domain, ref UIntPtr size);

        /// <summary>"_Foo_d__12" -> "Foo(coroutine)".</summary>
        private static string Coroutine(string n)
        {
            int d = n.IndexOf("_d__", StringComparison.Ordinal);
            string core = d > 0 ? n.Substring(0, d) : n;
            return core.Trim('_', '<', '>') + "(coroutine)";
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "(unnamed)" : s.Replace('`', '_').Replace('<', '_').Replace('>', '_');

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
    }
}
