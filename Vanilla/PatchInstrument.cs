using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Snitch.Sections;

namespace Snitch.Vanilla
{
    /// <summary>One wrapped patch method, plus what it is attached to - the detail behind "snitch patches list".</summary>
    internal sealed class WrappedPatch
    {
        internal MethodInfo Method;
        internal string Label;
        internal int SectionId;
        internal string Owner;
        internal readonly List<string> Kinds = new List<string>(2);   // prefix / postfix / finalizer
        internal readonly List<string> Targets = new List<string>(1); // the first few methods it patches, by name
        internal int TargetCount;                                     // how many it patches in total
    }

    /// <summary>
    /// Times other mods' HARMONY PATCHES, the per-frame work that lifecycle instrumentation cannot see.
    ///
    /// A mod's most expensive code does not have to live in OnUpdate. Put a postfix on a vanilla method the engine
    /// calls every frame and the cost is real, per-frame and invisible: it belongs to no section, no group and no
    /// total, and the mod reads as cheap. HarmonyLib can name that work - Harmony.GetAllPatchedMethods() lists every
    /// patched target and Harmony.GetPatchInfo(target) hands back the prefix/postfix/finalizer methods with their
    /// owner ids - so each one is wrapped exactly like a lifecycle method and lands in the same section table.
    ///
    /// OPT-IN ON PURPOSE ("snitch patches on"). Three reasons, all of them cost:
    ///  - Wrapping hundreds of patch methods is not free. A postfix called ten thousand times a frame pays a
    ///    dictionary lookup on both sides of every call, and a profiler that changes what it measures is worse than
    ///    useless. The unattributed line is what tells you to switch this on, and it stays truthful with it off.
    ///  - A Harmony patch that FAILS poisons its target for every later patcher. That risk belongs to a deliberate
    ///    act, not to every session that happens to run the profiler.
    ///  - Patches installed after the scan are not covered, so this is a snapshot: run it again after a mod that
    ///    patches late has loaded.
    ///
    /// Only patches that resolve to a loaded Melon are wrapped. Everything else (MelonLoader's own patches,
    /// Il2CppInterop's support patches, Snitch's) is counted by reason and listed, never silently dropped.
    /// </summary>
    internal static class PatchInstrument
    {
        internal static volatile bool Enabled;

        // Published as a whole reference, never mutated in place: a wrapped patch can fire from a background thread
        // (a mod may patch something the network layer calls off the main thread) and a Dictionary being rehashed
        // under a concurrent reader can spin forever. Scan builds a new map and swaps it in.
        private static volatile Dictionary<MethodBase, int> _ids = new Dictionary<MethodBase, int>();

        private static readonly Dictionary<MethodBase, WrappedPatch> _wrapped = new Dictionary<MethodBase, WrappedPatch>();
        private static readonly SortedDictionary<string, int> _skips = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private static readonly SortedDictionary<string, int> _skippedOwners = new SortedDictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> _failures = new List<string>();
        private static int _scans;

        /// <summary>The player asked for patch timing and has not asked for it back off. Stopping and restarting
        /// sampling must not quietly drop it: an option that turns itself off between two commands is a lie about
        /// what the next report measured.</summary>
        internal static bool Requested { get; private set; }

        internal static int WrappedCount => _wrapped.Count;
        internal static int FailureCount => _failures.Count;
        internal static bool EverScanned => _scans > 0;

        /// <summary>Scan for patches that are not wrapped yet, wrap them, and arm the timing.</summary>
        internal static void Enable()
        {
            int added = Scan();
            Enabled = true;
            Requested = true;
            Core.Log?.Msg($"[snitch] patch timing ON. {added} newly wrapped, {_wrapped.Count} wrapped in total. " + Status());
        }

        internal static void Disable()
        {
            Enabled = false;
            Requested = false;
            Core.Log?.Msg("[snitch] patch timing OFF (the wrappers stay installed but dormant - one bool read).");
        }

        /// <summary>Go dormant because sampling stopped, not because anyone asked. The request stands, so the next
        /// "snitch start" arms the timing again instead of measuring something else without saying so.</summary>
        internal static void Suspend() => Enabled = false;

        internal static string Status()
        {
            var sb = new StringBuilder(160);
            sb.Append("enabled=").Append(Enabled).Append(" wrapped=").Append(_wrapped.Count)
              .Append(" scans=").Append(_scans).Append(" failed=").Append(_failures.Count);
            if (_skips.Count > 0)
            {
                sb.Append(" skipped=[");
                bool first = true;
                foreach (KeyValuePair<string, int> kv in _skips)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append(' ').Append(kv.Value);
                }
                sb.Append(']');
            }
            return sb.ToString();
        }

        /// <summary>The wrapped patches, ordered by label - the body of "snitch patches list".</summary>
        internal static List<WrappedPatch> All()
        {
            var list = new List<WrappedPatch>(_wrapped.Values);
            list.Sort((x, y) => string.CompareOrdinal(x.Label, y.Label));
            return list;
        }

        internal static IEnumerable<string> Failures => _failures;

        /// <summary>Owners whose patches were left alone, with how many. Nothing is dropped without a name.</summary>
        internal static IEnumerable<KeyValuePair<string, int>> SkippedOwners => _skippedOwners;

        /// <summary>
        /// Take a snapshot of Harmony's patch table and wrap every patch method that belongs to a loaded mod and is
        /// not wrapped yet. Idempotent: run it again after a late-loading mod has patched.
        /// </summary>
        internal static int Scan()
        {
            _scans++;
            _skips.Clear();
            _skippedOwners.Clear();

            List<MethodBase> targets;
            try { targets = new List<MethodBase>(HarmonyLib.Harmony.GetAllPatchedMethods()); }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] patch scan aborted (guard: Harmony.GetAllPatchedMethods threw): " + e);
                return 0;
            }

            MethodInfo pre = AccessTools.Method(typeof(PatchInstrument), nameof(Pre));
            MethodInfo fin = AccessTools.Method(typeof(PatchInstrument), nameof(Fin));
            if (pre == null || fin == null)
            {
                Core.Log?.Error("[snitch] patch scan aborted (guard: Snitch's own Pre/Fin did not resolve through AccessTools).");
                return 0;
            }

            var byHarmonyId = new Dictionary<string, string>(StringComparer.Ordinal);
            var byAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
            BuildOwnerIndex(byHarmonyId, byAssembly);
            if (byHarmonyId.Count == 0 && byAssembly.Count == 0)
            {
                Core.Log?.Error("[snitch] patch scan aborted (guard: no loaded Melon could be indexed, so no patch could be attributed to a mod).");
                return 0;
            }

            var pending = new Dictionary<MethodBase, WrappedPatch>();
            for (int i = 0; i < targets.Count; i++)
            {
                MethodBase target = targets[i];
                if (target == null) { Skip("target-null", 1); continue; }

                Patches info;
                try { info = HarmonyLib.Harmony.GetPatchInfo(target); }
                catch (Exception e)
                {
                    Skip("patch-info-threw", 1);
                    Core.Log?.Warning($"[snitch] patch scan: GetPatchInfo({Describe(target)}) threw - {e.Message}");
                    continue;
                }
                if (info == null) { Skip("no-patch-info", 1); continue; }

                Collect(info.Prefixes, "prefix", target, byHarmonyId, byAssembly, pending);
                Collect(info.Postfixes, "postfix", target, byHarmonyId, byAssembly, pending);
                Collect(info.Finalizers, "finalizer", target, byHarmonyId, byAssembly, pending);

                // Transpilers and IL manipulators run ONCE, while the target is being patched - never per call.
                // Timing them would measure nothing that happens during a frame.
                if (info.Transpilers.Count > 0) Skip("transpiler(runs once, not per call)", info.Transpilers.Count);
                if (info.ILManipulators.Count > 0) Skip("ilmanipulator(runs once, not per call)", info.ILManipulators.Count);
            }

            return Install(pending, pre, fin);
        }

        /// <summary>Publish the section ids, then wrap. The ids go first because a wrapper can fire the instant it
        /// is installed, and a wrapper that cannot find its section would leave an unbalanced span behind.</summary>
        private static int Install(Dictionary<MethodBase, WrappedPatch> pending, MethodInfo pre, MethodInfo fin)
        {
            if (pending.Count == 0) return 0;

            var map = new Dictionary<MethodBase, int>(_ids);
            foreach (WrappedPatch w in pending.Values) map[w.Method] = w.SectionId;
            _ids = map;

            int ok = 0;
            List<WrappedPatch> failed = null;
            foreach (WrappedPatch w in pending.Values)
            {
                try
                {
                    Core.HarmonyInst.Patch(w.Method, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                    _wrapped[w.Method] = w;
                    ok++;
                }
                catch (Exception e)
                {
                    // A failed Harmony patch can leave its target unusable for later patchers, so this is an error,
                    // not a note: the mod that owns this method may misbehave from here on.
                    string msg = w.Label + " (" + Describe(w.Method) + "): " + e.Message;
                    _failures.Add(msg);
                    if (failed == null) failed = new List<WrappedPatch>();
                    failed.Add(w);
                    Core.Log?.Error("[snitch] could not wrap patch " + msg);
                }
            }

            if (failed != null)
            {
                var repaired = new Dictionary<MethodBase, int>(_ids);
                for (int i = 0; i < failed.Count; i++) repaired.Remove(failed[i].Method);
                _ids = repaired;
            }
            return ok;
        }

        /// <summary>Consider one kind of patch on one target. Every rejection is counted under a name, so
        /// "snitch patches status" can say what was left alone and why.</summary>
        private static void Collect(IList<Patch> patches, string kind, MethodBase target,
            Dictionary<string, string> byHarmonyId, Dictionary<string, string> byAssembly,
            Dictionary<MethodBase, WrappedPatch> pending)
        {
            if (patches == null) { Skip("patch-list-null", 1); return; }

            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null) { Skip("patch-entry-null", 1); continue; }

                MethodInfo pm;
                try { pm = p.PatchMethod; }
                catch (Exception e)
                {
                    Skip("patch-method-unresolvable", 1);
                    Core.Log?.Warning($"[snitch] patch scan: a {kind} on {Describe(target)} owned by '{p.owner}' "
                                    + "has no resolvable method - " + e.Message);
                    continue;
                }
                if (pm == null) { Skip("patch-method-null", 1); continue; }

                // A shared prefix serves many targets but is ONE method and ONE section: record the extra target
                // and move on. This is not a rejection, so it is not counted as a skip.
                if (pending.TryGetValue(pm, out WrappedPatch queued)) { Note(queued, kind, target); continue; }
                if (_wrapped.TryGetValue(pm, out WrappedPatch already)) { Note(already, kind, target); continue; }

                if (pm is DynamicMethod) { Skip("dynamic-method", 1); continue; }
                if (pm.DeclaringType == null) { Skip("no-declaring-type", 1); continue; }
                if (ReferenceEquals(pm.DeclaringType.Assembly, SelfAssembly)) { Skip("snitch-own", 1); continue; }
                if (pm.IsAbstract) { Skip("abstract", 1); continue; }
                if (pm.IsGenericMethodDefinition || pm.ContainsGenericParameters) { Skip("open-generic", 1); continue; }

                try { if (pm.GetMethodBody() == null) { Skip("no-il-body", 1); continue; } }
                catch (Exception e)
                {
                    Skip("method-body-threw", 1);
                    Core.Log?.Warning($"[snitch] patch scan: {Describe(pm)} would not hand over its IL body - {e.Message}");
                    continue;
                }

                string owner = ResolveOwner(pm, p.owner, byHarmonyId, byAssembly);
                if (owner == null)
                {
                    Skip("not-a-mod-patch", 1);
                    NoteSkippedOwner(p.owner, pm);
                    continue;
                }

                string label = owner + "." + Clean(pm.DeclaringType.Name) + "." + Clean(pm.Name);
                var w = new WrappedPatch
                {
                    Method = pm,
                    Label = label,
                    SectionId = SectionProfiler.GetId(label),
                    Owner = owner,
                };
                Note(w, kind, target);
                pending[pm] = w;
            }
        }

        private const int MaxListedTargets = 8;

        private static void Note(WrappedPatch w, string kind, MethodBase target)
        {
            if (!w.Kinds.Contains(kind)) w.Kinds.Add(kind);
            w.TargetCount++;
            if (w.Targets.Count < MaxListedTargets)
            {
                string d = Describe(target);
                if (!w.Targets.Contains(d)) w.Targets.Add(d);
            }
        }

        private static void Skip(string reason, int count)
        {
            _skips.TryGetValue(reason, out int n);
            _skips[reason] = n + count;
        }

        private static void NoteSkippedOwner(string harmonyOwnerId, MethodInfo pm)
        {
            string who = ShortOwnerId(harmonyOwnerId);
            if (string.IsNullOrEmpty(who))
            {
                try { who = pm.DeclaringType.Assembly.GetName().Name; }
                catch (Exception e)
                {
                    // A nameless owner is still a real skip; it just has to be reported as unnamed rather than lost.
                    Core.Log?.Warning("[snitch] patch scan: could not name the owner of " + Describe(pm) + " - " + e.Message);
                    who = "(unnamed)";
                }
            }
            _skippedOwners.TryGetValue(who, out int n);
            _skippedOwners[who] = n + 1;
        }

        /// <summary>MelonLoader builds each Melon's Harmony id as "&lt;assembly full name&gt;:&lt;melon name&gt;", so
        /// the readable half is behind the last colon. An id in any other shape is used whole.</summary>
        private static string ShortOwnerId(string harmonyOwnerId)
        {
            if (string.IsNullOrEmpty(harmonyOwnerId)) return null;
            int colon = harmonyOwnerId.LastIndexOf(':');
            return colon >= 0 && colon < harmonyOwnerId.Length - 1 ? harmonyOwnerId.Substring(colon + 1) : harmonyOwnerId;
        }

        /// <summary>
        /// Which mod a patch belongs to. The Harmony owner id is tried first because MelonLoader hands every Melon
        /// its own instance, which is exact even when the patch method lives in a shared helper library. The
        /// declaring assembly is the fallback for a mod that made its own Harmony instance with a custom id.
        /// Returns null when neither matches - the patch belongs to the loader, to Il2CppInterop or to Harmony
        /// itself, and is left alone.
        /// </summary>
        private static string ResolveOwner(MethodInfo pm, string harmonyOwnerId,
            Dictionary<string, string> byHarmonyId, Dictionary<string, string> byAssembly)
        {
            if (!string.IsNullOrEmpty(harmonyOwnerId) && byHarmonyId.TryGetValue(harmonyOwnerId, out string byId)) return byId;

            string full;
            try { full = pm.DeclaringType.Assembly.FullName; }
            catch (Exception e)
            {
                Core.Log?.Warning("[snitch] patch scan: could not read the assembly of " + Describe(pm) + " - " + e.Message);
                return null;
            }
            if (!string.IsNullOrEmpty(full) && byAssembly.TryGetValue(full, out string byAsm)) return byAsm;
            return null;
        }

        /// <summary>Index every loaded Melon by its Harmony instance id and by its assembly, so a patch can be named
        /// after the mod a player would recognise. Snitch is left out: its own patches are never wrapped.</summary>
        private static void BuildOwnerIndex(Dictionary<string, string> byHarmonyId, Dictionary<string, string> byAssembly)
        {
            IEnumerable<MelonBase> melons;
            try { melons = MelonBase.RegisteredMelons; }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] patch scan: MelonBase.RegisteredMelons is unreadable, no patch can be attributed - " + e);
                return;
            }
            if (melons == null)
            {
                Core.Log?.Error("[snitch] patch scan: MelonBase.RegisteredMelons returned null, no patch can be attributed.");
                return;
            }

            foreach (MelonBase melon in melons)
            {
                if (melon == null || ReferenceEquals(melon, Core.Instance)) continue;
                string name = MelonName(melon);

                try { if (melon.HarmonyInstance != null) byHarmonyId[melon.HarmonyInstance.Id] = name; }
                catch (Exception e) { Core.Log?.Warning($"[snitch] patch scan: {name} has no readable Harmony instance - {e.Message}"); }

                try
                {
                    Assembly a = melon.MelonAssembly?.Assembly ?? melon.GetType().Assembly;
                    if (a?.FullName != null) byAssembly[a.FullName] = name;
                }
                catch (Exception e) { Core.Log?.Warning($"[snitch] patch scan: {name} has no readable assembly - {e.Message}"); }
            }
        }

        private static string MelonName(MelonBase melon)
        {
            string name = null;
            try { name = melon.Info?.Name; }
            catch (Exception e) { Core.Log?.Warning("[snitch] patch scan: a Melon would not give its name - " + e.Message); }
            if (string.IsNullOrEmpty(name)) name = melon.GetType().Namespace ?? melon.GetType().Name;
            return AutoInstrument.GroupName(name);
        }

        private static readonly Assembly SelfAssembly = typeof(PatchInstrument).Assembly;

        private static string Describe(MethodBase m)
        {
            if (m == null) return "(null)";
            string t = m.DeclaringType != null ? m.DeclaringType.Name : "(global)";
            return t + "." + m.Name;
        }

        /// <summary>Compiler-generated and generic names carry characters that make a section label unreadable.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(unnamed)";
            return s.Replace('`', '_').Replace('<', '_').Replace('>', '_').Replace(' ', '_');
        }

        // ----- hot path: one shared prefix/finalizer, __originalMethod picks the section -----

        private static void Pre(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.Begin(id);
        }

        // Finalizer form, with no __exception parameter and a void return, so the timing closes even when the patch
        // throws and the exception still travels on untouched.
        private static void Fin(MethodBase __originalMethod)
        {
            if (!Enabled) return;
            if (_ids.TryGetValue(__originalMethod, out int id)) SectionProfiler.End(id);
        }
    }
}
