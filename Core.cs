using System;
using MelonLoader;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Server;

[assembly: MelonInfo(typeof(Snitch.Core), "Snitch", "1.4.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Snitch")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("ModManager&PhoneApp", "Hotline")]

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

            // Snitch's own in-game surface is now a Hotline panel (the overlay lives in the Hotline framework): the
            // profiler overview as a text readout plus sampling controls. Load-order-proof; a no-op if Hotline is absent.
            Hotline.Api.Hud.RegisterPanel("Snitch", "Snitch (Profiler)")
                .Text(Snitch.UI.ProfilerHud.BuildOverview)
                .Action("Start sampling", SnitchCore.Start)
                .Action("Stop sampling", SnitchCore.Stop)
                .Action("Reset", () => { SnitchCore.Stop(); SnitchCore.Start(); })
                .Toggle("Phone remote (scan the QR)", () => LanServer.Running, SetLanRemote)
                .Image(Snitch.UI.QrImage.Build);

            // The console bridge (Console.SubmitCommand prefixes) is the product's control surface. PatchAll
            // only patches the console classes; vanilla cost probes are patched on demand so a probe failure
            // can never break the console.
            try { HarmonyInstance.PatchAll(); }
            catch (Exception e) { Log.Warning("[Snitch] Harmony patch failed: " + e.Message); }

            // Local data server for the SnitchWeb dashboard (loopback only; idle data until sampling is armed).
            if (Preferences.Enabled && Preferences.ServerEnabled)
                SnitchServer.Start(Preferences.ServerPort, Preferences.ServerToken, Preferences.AllowedOrigins);

            // Optional phone remote (OFF by default, token-gated): a LAN endpoint for same-Wi-Fi phones plus a relay
            // session so a phone on any network can reach the game. Toggle live with 'snitch lan on|off'.
            if (Preferences.Enabled && Preferences.ServerEnabled && Preferences.LanAccess)
            {
                LanServer.Start(Preferences.LanPort);
                RelayHost.Start(System.Guid.NewGuid().ToString("N").Substring(0, 12));
            }

            Log.Msg("Snitch v1.4.0 - profiler. Console: 'snitch start' to begin, 'snitch help' for commands.");
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
            if (!_inWorld) return;
            if (Preferences.Enabled) SnitchCore.Tick();
        }

        /// <summary>Turn the phone remote on/off from the in-game panel toggle. On = start the LAN endpoint AND a relay
        /// session (so the QR works both on the same Wi-Fi and across networks) and persist the preference; off = stop
        /// both. The relay E2E and the LAN shortcut share the LAN server's token, so one QR drives both.</summary>
        private static void SetLanRemote(bool on)
        {
            Preferences.LanAccess = on;
            try { MelonPreferences.Save(); } catch { }
            if (on)
            {
                if (!LanServer.Running) LanServer.Start(Preferences.LanPort);
                if (!RelayHost.Running) RelayHost.Start(System.Guid.NewGuid().ToString("N").Substring(0, 12));
            }
            else { RelayHost.Stop(); LanServer.Stop(); }
        }

        public override void OnApplicationQuit()
        {
            SnitchServer.Stop();
            RelayHost.Stop();
            LanServer.Stop();
            LogHub.Uninstall();
        }

        public override void OnDeinitializeMelon()
        {
            SnitchServer.Stop();
            RelayHost.Stop();
            LanServer.Stop();
            LogHub.Uninstall();
        }
    }
}
