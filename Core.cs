using System;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Server;

[assembly: MelonInfo(typeof(Snitch.Core), "Snitch", "1.0.2", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Snitch")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp")]

namespace Snitch
{
    /// <summary>
    /// MelonLoader entry point for the Snitch profiler. It installs the modder API bridge, the console bridge
    /// ("snitch ..."), and the local data server, then drives the sampling engine each in-world frame.
    /// Everything stays idle (near-zero cost) until explicitly armed with "snitch start".
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

            // The console bridge (Console.SubmitCommand prefixes) is the product's control surface. PatchAll
            // only patches the console classes; vanilla cost probes are patched on demand so a probe failure
            // can never break the console.
            try { HarmonyInstance.PatchAll(); }
            catch (Exception e) { Log.Warning("[Snitch] Harmony patch failed: " + e.Message); }

            // Local data server for the SnitchWeb dashboard (loopback only; idle data until sampling is armed).
            if (Preferences.Enabled && Preferences.ServerEnabled)
                SnitchServer.Start(Preferences.ServerPort, Preferences.ServerToken, Preferences.AllowedOrigins);

            Log.Msg("Snitch v1.0.2 - profiler. Console: 'snitch start' to begin, 'snitch help' for commands.");
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
        }

        public override void OnGUI()
        {
            if (!_inWorld || !SnitchCore.Active || !Preferences.ShowHud) return;
            UI.ProfilerHud.Draw();
        }

        public override void OnApplicationQuit()
        {
            SnitchServer.Stop();
        }

        public override void OnDeinitializeMelon()
        {
            SnitchServer.Stop();
        }
    }
}
