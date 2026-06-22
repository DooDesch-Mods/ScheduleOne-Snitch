using System.Collections.Generic;
using Snitch.Config;
using Snitch.Providers;
using Snitch.Registries;
using Snitch.Sections;
using Snitch.Server;

namespace Snitch.Engine
{
    /// <summary>
    /// The profiler's master orchestrator. Everything is idle (near-zero cost) until <see cref="Start"/> - then
    /// each in-world frame it samples frame-time, flushes section accumulators, and (at the low provider Hz)
    /// re-polls the state providers + counters into a cached snapshot that the HUD, console, server and report
    /// all read. The profiler wraps its own per-frame work in a "Snitch.Self" section so its cost is visible and
    /// honest.
    /// </summary>
    internal static class SnitchCore
    {
        private static bool _active;
        private static bool _registered;
        private static float _pollAccum;
        private static int _selfId = -1;

        // cached snapshot (rebuilt on the poll tick; read by HUD/console/server/report on the main thread)
        internal static FrameStats LatestFrame;
        internal static List<SectionRow> LatestSections = new List<SectionRow>();
        internal static List<StateSnapshot> LatestStates = new List<StateSnapshot>();
        internal static List<CounterRow> LatestCounters = new List<CounterRow>();

        // pre-serialized wire JSON + frame/scene, set on the main thread so the server's background threads can
        // hand them out without touching Unity or the mutable lists.
        internal static volatile string LatestJson;
        internal static volatile string CapsJson;
        internal static volatile int LastFrame;
        internal static volatile string LastScene = "";

        internal static bool Active => _active;

        internal static void RegisterBuiltins()
        {
            if (_registered) return;
            _registered = true;
            StateRegistry.Register(new NpcStateProvider());
            StateRegistry.Register(new TrashStateProvider());
            StateRegistry.Register(new QuestStateProvider());
        }

        internal static void Start()
        {
            RegisterBuiltins();
            if (CapsJson == null) CapsJson = WireProtocol.BuildCaps();
            _active = true;
            FrameSampler.ResetGcWindow();
            SectionProfiler.Reset();
            _pollAccum = 999f;   // force a poll on the next tick
            Core.Log?.Msg("[snitch] sampling started.");
        }

        internal static void Stop()
        {
            _active = false;
            Core.Log?.Msg("[snitch] sampling stopped.");
        }

        internal static void Tick()
        {
            if (!_active) return;

            if (_selfId < 0) _selfId = SectionProfiler.GetId("Snitch.Self");
            SectionProfiler.Begin(_selfId);

            FrameSampler.Tick();
            LastFrame = Time.frameCount;

            _pollAccum += Time.unscaledDeltaTime;
            float interval = 1f / Preferences.PollHz;
            if (_pollAccum >= interval)
            {
                _pollAccum = 0f;
                Poll();
            }

            Ablation.AblationEngine.Tick();   // advance an active ablation sweep (no-op when idle)

            SectionProfiler.End(_selfId);
            SectionProfiler.Flush();   // frame boundary: push per-frame section totals into the rolling windows
        }

        private static void Poll()
        {
            LatestFrame = FrameSampler.Snapshot();
            LatestSections = SectionProfiler.Report(LatestFrame.MeanMs);
            LatestStates = StateRegistry.PollAll();
            LatestCounters = CounterRegistry.ReadAll();

            // serialize on the main thread, then stream to any connected dashboards
            LatestJson = WireProtocol.BuildSnapshot(LastFrame, LastScene);
            SnitchServer.Broadcast(LatestJson);
        }
    }
}
