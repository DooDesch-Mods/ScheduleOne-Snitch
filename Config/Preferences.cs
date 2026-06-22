using MelonLoader;
using UnityEngine;

namespace Snitch.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("Snitch_...") so it is
    /// auto-detected by the "Mod Manager &amp; Phone App" settings UI. The profiler ships its product surface
    /// (HUD, console, local data server) in Release too, but everything stays idle until <c>snitch start</c>,
    /// so the defaults here are safe/quiet.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "Snitch_01_Main";

        private static MelonPreferences_Category _category;

        // ----- always-compiled entries -----
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<bool> _enableInMp;
        private static MelonPreferences_Entry<bool> _autoStart;
        private static MelonPreferences_Entry<bool> _autoInstrument;
        private static MelonPreferences_Entry<bool> _showHud;
        private static MelonPreferences_Entry<float> _pollHz;

        // server
        private static MelonPreferences_Entry<bool> _serverEnabled;
        private static MelonPreferences_Entry<int> _serverPort;
        private static MelonPreferences_Entry<string> _serverToken;
        private static MelonPreferences_Entry<string> _allowedOrigins;

        internal static void Initialize()
        {
            if (_category != null)
            {
                return;
            }

#if DEBUG
            _category = MelonPreferences.CreateCategory(CategoryId, "Snitch (Profiler + Debug Probes)");
#else
            _category = MelonPreferences.CreateCategory(CategoryId, "Snitch (Profiler)");
#endif

            _enabled = Create("Enabled", true, "Enable Snitch",
                "Master switch. When OFF, Snitch does nothing at all. When ON, the profiler is available but stays " +
                "idle (near-zero cost) until you arm it with the in-game console command 'snitch start' or auto-start below.");
            _enableInMp = Create("EnableInMultiplayer", true, "Enable in multiplayer",
                "ON (default): profiling/measurement runs locally on every peer (safe - read-only). State-mutating " +
                "features (the ablation A/B harness, NPC/trash 'off' levers) always stay host-only regardless. OFF: do nothing in MP.");
            _autoStart = Create("AutoStart", false, "Auto-start sampling on world load",
                "OFF (default): you arm sampling manually with 'snitch start'. ON: begin sampling automatically when " +
                "you enter the world. Leave OFF unless you want the profiler always running.");
            _autoInstrument = Create("AutoInstrument", true, "Auto-instrument other mods",
                "ON (default): while sampling, every other loaded mod's per-frame methods (OnUpdate etc.) are timed " +
                "automatically and shown as '<Mod>.OnUpdate' - so any mod's frame cost appears with no code on its side. " +
                "Turn OFF to only show sections that mods (or Snitch's vanilla probes) register explicitly.");
            _showHud = Create("ShowHud", false, "Show profiler HUD",
                "On-screen overlay with frame stats, top section costs, counters and state distributions. OFF by " +
                "default; toggle live with 'snitch hud' or the F6 hotkey. Only draws while sampling is armed.");
            _pollHz = Create("PollHz", 4f, "Provider poll rate (Hz)",
                "How often the entity STATE providers and counters are sampled (the expensive part). 4 Hz is plenty " +
                "for distributions and keeps the profiler's own cost flat. Frame-time itself is always sampled every frame. Clamped 1-30.",
                new MelonLoader.Preferences.ValueRange<float>(1f, 30f));

            _serverEnabled = Create("ServerEnabled", true, "Enable local data server",
                "ON (default): run a loopback HTTP + WebSocket server so the SnitchWeb dashboard (hosted or the bundled " +
                "offline copy) can show live data. Binds 127.0.0.1 only - nothing is exposed to your network.");
            _serverPort = Create("ServerPort", 6140, "Local server port",
                "The loopback port for the data server + dashboard. Change only if 6140 clashes with another tool. Clamped 1024-65535.",
                new MelonLoader.Preferences.ValueRange<int>(1024, 65535));
            _serverToken = Create("ServerToken", "", "Pairing token (optional)",
                "Optional shared secret the dashboard must send to connect. Empty (default) = no token; safe because the " +
                "server is loopback-only and checks the browser Origin. Set a value for stricter pairing; it is shown in the log/HUD.");
            _allowedOrigins = Create("AllowedOrigins", "https://snitch.doodesch.de", "Allowed dashboard origins",
                "Comma-separated list of web origins permitted to connect from the browser (in addition to localhost, " +
                "which is always allowed). Defaults to the hosted dashboard. Used for CORS + WebSocket Origin checks.");
        }

        private static MelonPreferences_Entry<T> Create<T>(string id, T def, string name, string desc = null,
            MelonLoader.Preferences.ValueValidator validator = null)
        {
            return validator == null
                ? _category.CreateEntry(id, def, name, desc)
                : _category.CreateEntry(id, def, name, desc, false, false, validator);
        }

        // ----- accessors -----

        internal static bool Enabled => _enabled?.Value ?? true;
        internal static bool EnableInMultiplayer => _enableInMp?.Value ?? true;
        internal static bool AutoStart => _autoStart?.Value ?? false;
        internal static bool AutoInstrument => _autoInstrument?.Value ?? true;
        internal static bool ShowHud => _showHud?.Value ?? false;
        internal static void SetShowHud(bool v) { if (_showHud != null) _showHud.Value = v; }
        internal static float PollHz => Mathf.Clamp(_pollHz?.Value ?? 4f, 1f, 30f);

        internal static bool ServerEnabled => _serverEnabled?.Value ?? true;
        internal static int ServerPort => Mathf.Clamp(_serverPort?.Value ?? 6140, 1024, 65535);
        internal static string ServerToken => _serverToken?.Value ?? "";
        internal static string AllowedOrigins => _allowedOrigins?.Value ?? "https://snitch.doodesch.de";
    }
}
