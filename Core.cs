using System;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Server;

[assembly: MelonInfo(typeof(Snitch.Core), "Snitch", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Snitch")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Snitch
{
    /// <summary>
    /// MelonLoader entry point for the Snitch profiler. This is the Phase-0 verification build: it stands up
    /// the dev console bridge ("snitch ...") and the Phase-0 probes that prove, live in this IL2CPP build,
    /// whether the design's risky bits work (Stopwatch resolution, framerate uncap, ProfilerRecorder counters,
    /// Harmony per-call overhead, per-entity Update patching, and the in-process WebSocket upgrade) before the
    /// real engine is built on top. The product surface (engine, providers, HUD, server, web dashboard) lands
    /// in Phase 1+. Everything stays idle until explicitly armed, so the mod is near-free when not in use.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }
        internal static HarmonyLib.Harmony HarmonyInst { get; private set; }

        private bool _inWorld;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;
            HarmonyInst = HarmonyInstance;

            Preferences.Initialize();

            // Publish the modder API bridge as early as possible so other mods' Snitch.Api calls bind.
            Snitch.Bridge.BridgeHost.Install();

            // The console bridge (Console.SubmitCommand prefixes) ships in every build - it is the product's
            // control surface. PatchAll only patches the two console classes; per-entity probes are patched
            // on demand (in Phase0) so a probe failure can never break the console.
            try { HarmonyInstance.PatchAll(); }
            catch (Exception e) { Log.Warning("[Snitch] Harmony patch failed: " + e.Message); }

            // Local data server for the SnitchWeb dashboard (loopback only; idle data until sampling is armed).
            if (Preferences.Enabled && Preferences.ServerEnabled)
                SnitchServer.Start(Preferences.ServerPort, Preferences.ServerToken, Preferences.AllowedOrigins);

#if DEBUG
            Log.Msg("Snitch v1.0.0 (DEBUG) - dev build with Phase-0 probes. In-game console:");
            Log.Msg("  snitch start | hud on | top | states | vanilla on | ablate npc | report  (+ p0 <...>)");
#else
            Log.Msg("Snitch v1.0.0 - profiler. Console: 'snitch ...' (idle until 'snitch start').");
#endif
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
            SnitchCore.LastScene = sceneName;
            if (_inWorld && Preferences.Enabled)
            {
                SnitchCore.RegisterBuiltins();
                if (Preferences.AutoStart) SnitchCore.Start();
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
        }

        public override void OnUpdate()
        {
            SnitchServer.Pump();   // drain dashboard control actions onto the main thread (cheap when idle)
            if (!_inWorld)
            {
                return;
            }
            if (Preferences.Enabled) SnitchCore.Tick();
#if DEBUG
            P0.Phase0.Tick();
#endif
        }

        public override void OnGUI()
        {
            if (!_inWorld || !SnitchCore.Active || !Preferences.ShowHud) return;
            UI.ProfilerHud.Draw();
        }

        public override void OnApplicationQuit()
        {
            SnitchServer.Stop();
#if DEBUG
            P0.Phase0.Shutdown();
#endif
        }

        public override void OnDeinitializeMelon()
        {
            SnitchServer.Stop();
#if DEBUG
            P0.Phase0.Shutdown();
#endif
        }
    }
}
