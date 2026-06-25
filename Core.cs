using System;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Server;

[assembly: MelonInfo(typeof(Snitch.Core), "Snitch", "1.2.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Snitch")]
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

            // Capture log output (Unity threaded callback -> "Unity" channel; mods feed their own channels via the API).
            LogHub.Install();

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

            Log.Msg("Snitch v1.2.0 - profiler. Console: 'snitch start' to begin, 'snitch help' for commands.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
            SnitchCore.LastScene = sceneName;
            if (_inWorld && Preferences.Enabled)
            {
                SnitchCore.RegisterBuiltins();
                // Discover each mod's SnitchProbe now (not just on 'snitch start') so per-mod panels, counters and
                // log channels exist as soon as you enter the world - the overlay is usable without arming sampling.
                Snitch.Vanilla.AutoInstrument.DiscoverProbes();
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
            try
            {
                // F6 is the always-available entry point. It summons the overlay AND guarantees the Overview window
                // (which hosts the per-panel toggle buttons) is visible - so the overlay can never get stuck closed
                // after the user shuts the Overview's own [x]. Press again (with the Overview up) to dismiss everything.
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    if (!Preferences.ShowHud || !UI.WindowLayout.IsVisible("overview"))
                    {
                        Preferences.SetShowHud(true);
                        UI.WindowLayout.SetVisible("overview", true);   // persists the layout too
                    }
                    else Preferences.SetShowHud(false);
                }
                if (Preferences.ShowHud) UI.WindowManager.HandleInput();
            }
            catch { /* never let overlay input break the update loop */ }
            if (Preferences.Enabled) SnitchCore.Tick();
        }

        public override void OnGUI()
        {
            // Draw whenever the overlay is on (not only while sampling) so the log timeline + panel controls are
            // usable before 'snitch start'; the data readouts simply stay empty until sampling is armed.
            if (!_inWorld || !Preferences.ShowHud) return;
            UI.WindowManager.Draw();
        }

        public override void OnApplicationQuit()
        {
            SnitchServer.Stop();
            LogHub.Uninstall();
        }

        public override void OnDeinitializeMelon()
        {
            SnitchServer.Stop();
            LogHub.Uninstall();
        }
    }
}
