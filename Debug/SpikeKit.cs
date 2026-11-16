#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using Snitch.Sections;

namespace Snitch.Testing
{
    /// <summary>
    /// Produces a known stutter so a check can see whether the stutter report names its cause. The test kit's types
    /// are timed under the group "SnitchTest", as if they were another mod.
    /// <list type="bullet">
    /// <item><c>snitch burn &lt;ms&gt;</c> spins outside every section, work Snitch has no label for.</item>
    /// <item><c>... section</c> spins inside the section "SnitchTest.Burn".</item>
    /// <item><c>... patch</c> spins inside a Harmony prefix (SnitchTest.SpikePatch.Prefix).</item>
    /// <item><c>... behaviour</c> spins in Update of an injected MonoBehaviour (SnitchTest.SpikeBehaviour.Update).</item>
    /// <item><c>... coroutine</c> spins in a MelonCoroutines coroutine (SnitchTest.SpikeKit.BurnRoutine).</item>
    /// <item><c>... game</c> spins inside the game's own script ScheduleOne.Tools.MonoBehaviourEvents.Update, which
    /// calls its onUpdate event every frame; the test kit only adds the listener. No mod section is open there, so
    /// the stutter is the game's: "Scripts" with deep probes off, the class with them on.</item>
    /// <item><c>snitch burn gc</c> forces a game garbage collection.</item>
    /// <item><c>snitch burn garbage [net]</c> allocates inside SnitchTest.Alloc over 30 frames, then forces a game
    /// (or .NET) collection outside every section, so the collection's garbage is the test kit's.</item>
    /// </list>
    /// </summary>
    internal static class SpikeKit
    {
        private enum Site { None, Outside, Section, Patch, Behaviour, Coroutine, Game }

        private static int _burnMs;
        private static Site _site;
        private static bool _gc;
        private static int _sectionId = -1;
        private static int _allocId = -1;

        private static int _garbageFrame = -1;
        private static bool _garbageNet;
        private static List<object> _live;
        private const int AllocFrom = 3, AllocTo = 33, CollectAt = 40, ChunkBytes = 2 << 20, LiveObjectsPerFrame = 30000;

        private static GameObject _host;
        private static bool _injected;
        private static GameObject _gameHost;
        private static Action _gameListener;

        internal static string Arm(string[] p)
        {
            string a = p.Length > 2 ? p[2].ToLowerInvariant() : "";
            string b = p.Length > 3 ? p[3].ToLowerInvariant() : "";
            if (a == "gc") { _gc = true; return "burn: garbage collection armed for the next frame."; }
            if (a == "garbage")
            {
                if (_garbageFrame >= 0) return "burn: a garbage run is already going.";
                _garbageNet = b == "net";
                _garbageFrame = 0;
                return $"burn: {(_garbageNet ? ".NET" : "game")} garbage run armed ({CollectAt} frames, then a collection).";
            }
            const string usage = "usage: snitch burn <ms 1-2000> [section|patch|behaviour|coroutine|game] | snitch burn gc | snitch burn garbage [net]";
            if (!int.TryParse(a, out int ms) || ms < 1 || ms > 2000) return usage;

            Site site;
            switch (b)
            {
                case "": site = Site.Outside; break;
                case "section": site = Site.Section; break;
                case "patch": site = Site.Patch; break;
                case "behaviour": site = Site.Behaviour; break;
                case "coroutine": site = Site.Coroutine; break;
                case "game": site = Site.Game; break;
                default: return usage;
            }

            if (site == Site.Behaviour && !EnsureBehaviour()) return "burn: the test MonoBehaviour could not be created (see the log).";
            if (site == Site.Game && !EnsureGameScript()) return "burn: the game script MonoBehaviourEvents could not be set up (see the log).";
            if (site == Site.Coroutine)
            {
                MelonCoroutines.Start(BurnRoutine(ms));
                return $"burn: {ms} ms inside coroutine BurnRoutine armed for the next frame.";
            }
            _burnMs = ms;
            _site = site;
            return $"burn: {ms} ms {Where(site)} armed for the next frame.";
        }

        private static string Where(Site s)
        {
            switch (s)
            {
                case Site.Section: return "inside SnitchTest.Burn";
                case Site.Patch: return "inside SpikePatch.Prefix";
                case Site.Behaviour: return "inside SpikeBehaviour.Update";
                case Site.Coroutine: return "inside coroutine BurnRoutine";
                case Site.Game: return "inside the game script MonoBehaviourEvents.Update";
                default: return "outside any section";
            }
        }

        /// <summary>Runs the armed spike. Called from the profiler tick, after its own section has closed.</summary>
        internal static void Tick()
        {
            if (_gc)
            {
                _gc = false;
                int before = Il2CppSystem.GC.CollectionCount(0);
                var gcClock = Stopwatch.StartNew();
                Il2CppSystem.GC.Collect();
                Core.Log?.Msg($"[snitch] burn: forced a garbage collection ({gcClock.Elapsed.TotalMilliseconds:F1} ms, "
                            + $"collections {before} -> {Il2CppSystem.GC.CollectionCount(0)}).");
            }
            GarbageStep();
            PatchedHook();

            if (_site != Site.Outside && _site != Site.Section) return;
            int ms = Take(_site);
            if (ms <= 0) return;
            Site site = _site;
            _site = Site.None;
            if (site == Site.Section)
            {
                if (_sectionId < 0) _sectionId = SectionProfiler.GetId("SnitchTest.Burn");
                SectionProfiler.Begin(_sectionId);
                Spin(ms, Where(site));
                SectionProfiler.End(_sectionId);
            }
            else Spin(ms, Where(site));
        }

        /// <summary>The armed burn for <paramref name="site"/>, consumed; 0 when nothing is armed there.</summary>
        private static int Take(Site site)
        {
            if (_burnMs <= 0 || _site != site) return 0;
            int ms = _burnMs;
            _burnMs = 0;
            if (site != Site.Outside && site != Site.Section) _site = Site.None;
            return ms;
        }

        internal static void Spin(int ms, string where)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { }
            Core.Log?.Msg($"[snitch] burn: spun {sw.Elapsed.TotalMilliseconds:F1} ms {where}.");
        }

        /// <summary>Called every tick; the test kit's Harmony prefix sits on it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PatchedHook() { }

        internal static void BurnInPatch()
        {
            int ms = Take(Site.Patch);
            if (ms > 0) Spin(ms, Where(Site.Patch));
        }

        internal static void BurnInBehaviour()
        {
            int ms = Take(Site.Behaviour);
            if (ms > 0) Spin(ms, Where(Site.Behaviour));
        }

        private static void BurnInGameScript()
        {
            int ms = Take(Site.Game);
            if (ms > 0) Spin(ms, Where(Site.Game));
        }

        /// <summary>A GameObject with the game's MonoBehaviourEvents, whose onUpdate event runs the armed burn.</summary>
        private static bool EnsureGameScript()
        {
            if (_gameHost != null) return true;
            try
            {
                _gameHost = new GameObject("SnitchTest.GameScript");
                UnityEngine.Object.DontDestroyOnLoad(_gameHost);
                var events = _gameHost.AddComponent<Il2CppScheduleOne.Tools.MonoBehaviourEvents>();
                _gameListener = BurnInGameScript;
                events.onUpdate = new UnityEngine.Events.UnityEvent();
                events.onUpdate.AddListener(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(_gameListener));
                Core.Log?.Msg("[snitch] burn: game script MonoBehaviourEvents set up.");
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] burn: could not set up the game script MonoBehaviourEvents - " + e);
                if (_gameHost != null) UnityEngine.Object.Destroy(_gameHost);
                _gameHost = null;
                return false;
            }
        }

        private static IEnumerator BurnRoutine(int ms)
        {
            yield return null;
            Spin(ms, Where(Site.Coroutine));
        }

        private static bool EnsureBehaviour()
        {
            if (_host != null) return true;
            try
            {
                if (!_injected)
                {
                    ClassInjector.RegisterTypeInIl2Cpp<SpikeBehaviour>();
                    _injected = true;
                }
                _host = new GameObject("SnitchTest.SpikeBehaviour");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<SpikeBehaviour>();
                Core.Log?.Msg("[snitch] burn: test MonoBehaviour created.");
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[snitch] burn: could not create the test MonoBehaviour - " + e);
                _host = null;
                return false;
            }
        }

        /// <summary>One frame of the garbage run: a collection that starts a clean cycle, then allocations inside
        /// SnitchTest.Alloc, then the collection the check reads, outside every section.</summary>
        private static void GarbageStep()
        {
            if (_garbageFrame < 0) return;
            int f = _garbageFrame++;
            if (f == 0)
            {
                if (_garbageNet) GC.Collect(0);
                else Il2CppSystem.GC.Collect();
                _live = _garbageNet ? new List<object>(LiveObjectsPerFrame * (AllocTo - AllocFrom)) : null;
                Core.Log?.Msg("[snitch] burn: garbage run started, the cycle was reset by a collection.");
                return;
            }
            if (f >= AllocFrom && f < AllocTo)
            {
                if (_allocId < 0) _allocId = SectionProfiler.GetId("SnitchTest.Alloc");
                SectionProfiler.Begin(_allocId);
                if (_garbageNet)
                {
                    byte[] chunk = new byte[ChunkBytes];
                    chunk[chunk.Length - 1] = 1;
                    GC.KeepAlive(chunk);
                    // Live objects give the forced .NET collection something to mark, so it lasts long enough to be
                    // a stutter; they are dropped right after it.
                    for (int i = 0; i < LiveObjectsPerFrame; i++) _live.Add(new object[2]);
                }
                else
                {
                    var chunk = new Il2CppStructArray<byte>(ChunkBytes);
                    chunk[ChunkBytes - 1] = 1;
                    GC.KeepAlive(chunk);
                }
                SectionProfiler.End(_allocId);
                return;
            }
            if (f < CollectAt) return;

            _garbageFrame = -1;
            var clock = Stopwatch.StartNew();
            if (_garbageNet) GC.Collect(2, GCCollectionMode.Forced, true, true);
            else Il2CppSystem.GC.Collect();
            double ms = clock.Elapsed.TotalMilliseconds;
            _live = null;
            Core.Log?.Msg($"[snitch] burn: forced a {(_garbageNet ? ".NET" : "game")} collection after the garbage run ({ms:F1} ms).");
        }
    }

    /// <summary>A MonoBehaviour the test kit adds through ClassInjector, standing in for a mod's own component.</summary>
    internal sealed class SpikeBehaviour : MonoBehaviour
    {
        public SpikeBehaviour(IntPtr ptr) : base(ptr) { }

        private void Update() => SpikeKit.BurnInBehaviour();
    }

    /// <summary>A Harmony prefix of the test kit, standing in for a mod's patch on a method called every frame.</summary>
    [HarmonyPatch(typeof(SpikeKit), nameof(SpikeKit.PatchedHook))]
    internal static class SpikePatch
    {
        private static void Prefix() => SpikeKit.BurnInPatch();
    }
}
#endif
