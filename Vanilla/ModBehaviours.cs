using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Snitch.Sections;

namespace Snitch.Vanilla
{
    /// <summary>
    /// Times the work a mod hands to the engine instead of running it in OnUpdate: the Unity messages of its own
    /// MonoBehaviours (types it registered through ClassInjector) and its coroutines. The engine runs both inside
    /// its script steps, so without this a mod's MonoBehaviour that stalls a frame reads as "Scripts" - the game.
    ///
    /// MonoBehaviours: every type in a mod assembly that derives from UnityEngine.MonoBehaviour gets its declared
    /// Update, LateUpdate, FixedUpdate and OnGUI wrapped, labelled "&lt;Mod&gt;.&lt;Type&gt;.Update".
    /// Coroutines: every compiler-made iterator class that is an IEnumerator but not an IEnumerable (that is what a
    /// method returning IEnumerator compiles to, the shape MelonCoroutines and StartCoroutine run) gets MoveNext
    /// wrapped, labelled "&lt;Mod&gt;.&lt;Class&gt;.&lt;Method&gt; (coroutine)". Iterators that are also IEnumerable are
    /// LINQ-style sequences, called per element, and are left alone.
    ///
    /// Snitch's own types are never timed, except the Debug test kit. The wrappers go in a few per frame through
    /// <see cref="PatchQueue"/>; dormant (one bool read) while sampling is off.
    /// </summary>
    internal static class ModBehaviours
    {
        internal static volatile bool Enabled;
        private static volatile Dictionary<MethodBase, int> _ids = new Dictionary<MethodBase, int>();
        private static bool _queued;
        private static readonly string[] Messages = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

        internal static int Behaviours { get; private set; }
        internal static int Coroutines { get; private set; }

        internal static void Enable()
        {
            if (!_queued)
            {
                _queued = true;
                Queue();
            }
            Enabled = true;
        }

        internal static void Disable() => Enabled = false;

        private sealed class Found
        {
            internal MethodInfo Method;
            internal string Label;
        }

        private static void Queue()
        {
            MethodInfo pre = AccessTools.Method(typeof(ModBehaviours), nameof(Pre));
            MethodInfo fin = AccessTools.Method(typeof(ModBehaviours), nameof(Fin));
            if (pre == null || fin == null)
            {
                Core.Log?.Error("[snitch] mod behaviour timing aborted (guard: Snitch's own Pre/Fin did not resolve through AccessTools).");
                return;
            }

            IEnumerable<MelonMod> mods = AutoInstrument.RegisteredMods();
            if (mods == null)
            {
                Core.Log?.Error("[snitch] mod behaviour timing aborted (guard: the loaded mod list is unreadable).");
                return;
            }

            var found = new List<Found>();
            var seen = new HashSet<Assembly>();
            int modCount = 0;
            foreach (MelonMod mod in mods)
            {
                if (mod == null) continue;
                Assembly asm = mod.GetType().Assembly;
                if (!seen.Add(asm)) continue;
                bool self = ReferenceEquals(mod, Core.Instance);
#if !DEBUG
                if (self) continue;
#endif
                int before = found.Count;
                Scan(asm, self ? AutoInstrument.TestKitGroup : AutoInstrument.ModName(mod), self, found);
                if (found.Count > before) modCount++;
            }

            var map = new Dictionary<MethodBase, int>(found.Count);
            for (int i = 0; i < found.Count; i++) map[found[i].Method] = SectionProfiler.GetId(found[i].Label);
            _ids = map;

            var jobs = new List<Func<bool>>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                Found f = found[i];
                jobs.Add(() => Wrap(f, pre, fin));
            }
            Core.Log?.Msg($"[snitch] mod behaviour timing: {Behaviours} MonoBehaviour message(s) and {Coroutines} coroutine(s) "
                        + $"in {modCount} mod(s).");
            PatchQueue.Add("mod behaviour timing", jobs);
        }

        private static bool Wrap(Found f, MethodInfo pre, MethodInfo fin)
        {
            try
            {
                Core.HarmonyInst.Patch(f.Method, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                return true;
            }
            catch (Exception e)
            {
                var repaired = new Dictionary<MethodBase, int>(_ids);
                repaired.Remove(f.Method);
                _ids = repaired;
                PatchInstrument.Withdraw(f.Method, f.Label + " (" + f.Method.DeclaringType?.FullName + "." + f.Method.Name + ")", e);
                return false;
            }
        }

        private static void Scan(Assembly asm, string group, bool testKitOnly, List<Found> found)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                Core.Log?.Warning($"[snitch] mod behaviour timing: {asm.GetName().Name} loaded only part of its types - " + e.Message);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[snitch] mod behaviour timing: {asm.GetName().Name} would not list its types, skipped - " + e.Message);
                return;
            }
            if (types == null)
            {
                Core.Log?.Warning($"[snitch] mod behaviour timing: {asm.GetName().Name} returned no type list, skipped.");
                return;
            }

            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null) continue;
                if (testKitOnly && !AutoInstrument.IsTestKit(t)) continue;
                try
                {
                    if (t.ContainsGenericParameters || t.IsInterface) continue;
                    if (typeof(MonoBehaviour).IsAssignableFrom(t))
                    {
                        for (int m = 0; m < Messages.Length; m++)
                        {
                            MethodInfo mi = t.GetMethod(Messages[m], Declared, null, Type.EmptyTypes, null);
                            if (!Wrappable(mi)) continue;
                            found.Add(new Found { Method = mi, Label = group + "." + Clean(t.Name) + "." + Messages[m] });
                            Behaviours++;
                        }
                    }
                    else if (IsCoroutine(t))
                    {
                        MethodInfo mi = t.GetMethod("MoveNext", Declared, null, Type.EmptyTypes, null);
                        if (!Wrappable(mi)) continue;
                        string outer = t.DeclaringType != null ? Clean(t.DeclaringType.Name) : "(global)";
                        found.Add(new Found { Method = mi, Label = group + "." + outer + "." + IteratorName(t.Name) + " (coroutine)" });
                        Coroutines++;
                    }
                }
                catch (Exception e)
                {
                    // A type whose base or interfaces reference a missing assembly cannot be inspected; it is reported
                    // and left untimed, its work stays with the engine step that runs it.
                    Core.Log?.Warning($"[snitch] mod behaviour timing: could not inspect {t.FullName} - {e.Message}");
                }
            }
        }

        private static bool IsCoroutine(Type t)
        {
            return t.IsClass && t.IsNested && t.Name.IndexOf("d__", StringComparison.Ordinal) >= 0
                && typeof(IEnumerator).IsAssignableFrom(t) && !typeof(IEnumerable).IsAssignableFrom(t);
        }

        private static bool Wrappable(MethodInfo mi)
        {
            if (mi == null || mi.IsAbstract || mi.IsGenericMethodDefinition) return false;
            try { return mi.GetMethodBody() != null; }
            catch (Exception e)
            {
                Core.Log?.Warning($"[snitch] mod behaviour timing: {mi.DeclaringType?.FullName}.{mi.Name} has no readable body - {e.Message}");
                return false;
            }
        }

        /// <summary>"&lt;FadeIn&gt;d__12" is the iterator class of the method FadeIn.</summary>
        private static string IteratorName(string typeName)
        {
            int open = typeName.IndexOf('<');
            int close = typeName.IndexOf('>');
            if (open >= 0 && close > open + 1) return Clean(typeName.Substring(open + 1, close - open - 1));
            return Clean(typeName);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(unnamed)";
            return s.Replace('`', '_').Replace('<', '_').Replace('>', '_').Replace(' ', '_').Replace('.', '_');
        }

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
