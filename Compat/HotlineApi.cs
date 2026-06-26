using System;
using System.Collections.Generic;
using System.Reflection;

namespace Hotline.Api
{
    /// <summary>
    /// The Hotline framework's modder API. Reference Hotline.Api.dll OR drop this single file into your mod.
    /// Register a panel and you instantly get a ready-made, toggleable, draggable in-game window inside the one
    /// unified Hotline overlay - no custom hotkey, no custom uGUI, no per-mod debug window. One master key opens
    /// the overlay; every mod's controls live there as buttons. Optionally bind a central hotkey to an action and
    /// Hotline owns the polling and conflict detection for you.
    ///
    /// Every call is a zero-overhead no-op when Hotline is not installed and lights up automatically when it is,
    /// so you can ship this unconditionally with no hard dependency.
    ///
    /// <code>
    ///   using Hotline.Api;
    ///   Hud.RegisterPanel("MyMod", "My Mod")
    ///      .Text(() =&gt; "queue = " + _queue.Count)
    ///      .Action("Reload", Reload)
    ///      .Toggle("Verbose", () =&gt; _verbose, v =&gt; _verbose = v)
    ///      .Hotkey("Reload", HotlineKey.F8, Reload);   // optional central hotkey
    /// </code>
    ///
    /// Tip: a class named <c>HotlineProbe</c> with a static <c>Register()</c> is auto-discovered and called on bind
    /// (see <see cref="AutoRegister"/>), so your mod's Core does not have to wire anything.
    ///
    /// All calls MUST be made from the Unity main thread.
    /// </summary>
    public static class Hud
    {
        // bound bridge delegates (null until the host is found)
        private static bool _bound;
        private static bool _autoDone;
        private static int _probeAttempts;
        private static readonly List<Action> _pending = new List<Action>();

        private static Action<string, string> _registerPanel;
        private static Action<string, string, string, Action> _registerAction;
        private static Action<string, string, string, Func<bool>, Action<bool>> _registerToggle;
        private static Action<string, Func<string>> _registerText;
        private static Action<string> _bindPanelLog;
        private static Action<string, int, string> _log;
        private static Action<string, string, int, Action> _registerHotkey;

        /// <summary>True only when the Hotline host is installed AND bound. You rarely need this - the API is a safe
        /// no-op when absent.</summary>
        public static bool Available { get { EnsureBound(); return _bound; } }

        /// <summary>Declare a panel: a named, toggleable, movable window in the Hotline overlay that groups everything
        /// this mod exposes (text readouts, action buttons, toggles, a log channel). Returns a fluent builder.
        /// Load-order-proof; a no-op (the builder still works) if Hotline is absent.</summary>
        public static Panel RegisterPanel(string id, string title = null)
        {
            var panel = new Panel(id);
            if (string.IsNullOrEmpty(id)) return panel;
            string t = title;
            EnsureBound();
            if (_registerPanel != null) _registerPanel(id, t);
            else _pending.Add(() => _registerPanel?.Invoke(id, t));
            return panel;
        }

        /// <summary>A clickable button in a mod's panel - the in-game replacement for a debug hotkey action. Runs on
        /// the main thread when clicked. Load-order-proof.</summary>
        public static void RegisterAction(string panelId, string label, Action run)
        {
            if (run == null || string.IsNullOrEmpty(label)) return;
            string actionId = panelId + ":" + Slug(label);
            EnsureBound();
            if (_registerAction != null) _registerAction(panelId, actionId, label, run);
            else _pending.Add(() => _registerAction?.Invoke(panelId, actionId, label, run));
        }

        /// <summary>An on/off control in a mod's panel - the in-game replacement for a toggle hotkey. <paramref name="get"/>
        /// reports the current state, <paramref name="set"/> applies it; both run on the main thread. Load-order-proof.</summary>
        public static void RegisterToggle(string panelId, string label, Func<bool> get, Action<bool> set)
        {
            if (get == null || set == null || string.IsNullOrEmpty(label)) return;
            string toggleId = panelId + ":" + Slug(label);
            EnsureBound();
            if (_registerToggle != null) _registerToggle(panelId, toggleId, label, get, set);
            else _pending.Add(() => _registerToggle?.Invoke(panelId, toggleId, label, get, set));
        }

        /// <summary>A free-text, multi-line readout in a mod's panel. Polled by the host on the main thread. Load-order-proof.</summary>
        public static void RegisterText(string panelId, Func<string> provider)
        {
            if (provider == null) return;
            EnsureBound();
            if (_registerText != null) _registerText(panelId, provider);
            else _pending.Add(() => _registerText?.Invoke(panelId, provider));
        }

        /// <summary>Mark that a panel should display its own log channel (the lines you send via <see cref="Log"/> /
        /// <see cref="Panel.Write"/> with the same id). Load-order-proof.</summary>
        public static void BindPanelLog(string panelId)
        {
            EnsureBound();
            if (_bindPanelLog != null) _bindPanelLog(panelId);
            else _pending.Add(() => _bindPanelLog?.Invoke(panelId));
        }

        /// <summary>Send a log line to a channel (use your mod/panel id as the channel). It appears in that mod's panel
        /// log AND in Hotline's combined timeline. Load-order-proof; a no-op if Hotline is absent.</summary>
        public static void Log(string channel, string message, LogLevel level = LogLevel.Info)
        {
            if (string.IsNullOrEmpty(message)) return;
            int lv = (int)level;
            EnsureBound();
            if (_log != null) _log(channel, lv, message);
            else _pending.Add(() => _log?.Invoke(channel, lv, message));
        }

        /// <summary>Bind a central hotkey to an action. Hotline owns the key polling and conflict detection, so every
        /// mod's hotkeys live in one place. Pass <see cref="HotlineKey.None"/> to skip the key and only label the
        /// action. The same action typically also appears as a panel button. Load-order-proof.</summary>
        public static void RegisterHotkey(string ownerId, string label, HotlineKey key, Action run)
        {
            if (run == null || string.IsNullOrEmpty(label)) return;
            int k = (int)key;
            EnsureBound();
            if (_registerHotkey != null) _registerHotkey(ownerId, label, k, run);
            else _pending.Add(() => _registerHotkey?.Invoke(ownerId, label, k, run));
        }

        /// <summary>Discover a convention type named <c>HotlineProbe</c> with a static <c>Register()</c> in THIS mod's
        /// own assembly and invoke it once - so a mod never has to wire a Register() call into its Core. Drive it from
        /// a <c>[ModuleInitializer]</c> in your probe file. No-op + load-order-proof.</summary>
        public static void AutoRegister()
        {
            EnsureBound();
            if (_bound) RunAutoRegister();   // else: the bind flush will run it (load-order-proof, both directions)
        }

        private static void RunAutoRegister()
        {
            if (_autoDone) return;
            _autoDone = true;   // latch before invoking so a throw can't loop
            try
            {
                Assembly self = typeof(Hud).Assembly;   // only this mod's assembly - single, fast, no AppDomain scan
                Type probe = self.GetType("HotlineProbe", false) ?? FindByLeafName(self, "HotlineProbe");
                MethodInfo reg = probe?.GetMethod("Register",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                reg?.Invoke(null, null);
            }
            catch { /* a mod's probe threw -> stays a no-op, never crashes the mod */ }
        }

        private static Type FindByLeafName(Assembly asm, string leaf)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { return null; }
            if (types == null) return null;
            foreach (Type t in types)
                if (t != null && t.IsClass && t.IsAbstract && t.IsSealed && t.Name == leaf) return t;   // static class
            return null;
        }

        private static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        // ----- reflection handshake (runs until it binds, then latches) -----

        private static void EnsureBound()
        {
            if (_bound) return;   // bound once, never probe again (fast path)
            try
            {
                Type t = FindBridge((_probeAttempts++ % 30) == 0);
                if (t == null) return;   // host not present yet - cheap re-probe next call (load-order proof)
                object abi = t.GetField("AbiVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (abi is int v && v < 1) return;

                _registerPanel = Get<Action<string, string>>(t, "RegisterPanel");
                _registerAction = Get<Action<string, string, string, Action>>(t, "RegisterAction");
                _registerToggle = Get<Action<string, string, string, Func<bool>, Action<bool>>>(t, "RegisterToggle");
                _registerText = Get<Action<string, Func<string>>>(t, "RegisterText");
                _bindPanelLog = Get<Action<string>>(t, "BindPanelLog");
                _log = Get<Action<string, int, string>>(t, "Log");
                _registerHotkey = Get<Action<string, string, int, Action>>(t, "RegisterHotkey");

                if (_registerPanel == null) return;   // partial table - try again next call
                _bound = true;

                // flush any registrations made before the host was up
                for (int i = 0; i < _pending.Count; i++) { try { _pending[i](); } catch { } }
                _pending.Clear();
                RunAutoRegister();
            }
            catch { /* any failure -> stays a no-op, retries next call */ }
        }

        private static T Get<T>(Type t, string field) where T : class
        {
            object v = t.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return v as T;   // works because Func<>/Action<> are shared BCL types in both assemblies
        }

        private static Type FindBridge(bool scan)
        {
            Type t = Type.GetType("Hotline.Bridge.HotlineBridge, Hotline", false);
            if (t != null || !scan) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Hotline.Bridge.HotlineBridge", false); if (t != null) return t; }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Severity for <see cref="Hud.Log"/>. Mirrors the host's 0=info / 1=warning / 2=error.</summary>
    public enum LogLevel { Info = 0, Warning = 1, Error = 2 }

    /// <summary>Keys Hotline can bind centrally. The integer values match <c>UnityEngine.KeyCode</c> so the host
    /// casts them straight back - the shim stays Unity-free.</summary>
    public enum HotlineKey
    {
        None = 0,
        F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287,
        F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293
    }

    /// <summary>
    /// Fluent builder for a mod's Hotline panel (returned by <see cref="Hud.RegisterPanel"/>). Everything is a safe
    /// no-op when the Hotline host is absent.
    /// </summary>
    public sealed class Panel
    {
        private readonly string _id;
        internal Panel(string id) { _id = id ?? ""; }

        /// <summary>The panel id (also the default log channel).</summary>
        public string Id => _id;

        /// <summary>A free-text, multi-line readout in this panel.</summary>
        public Panel Text(Func<string> provider) { Hud.RegisterText(_id, provider); return this; }

        /// <summary>A clickable button (replaces a debug hotkey action).</summary>
        public Panel Action(string label, Action run) { Hud.RegisterAction(_id, label, run); return this; }

        /// <summary>An on/off control (replaces a debug toggle hotkey).</summary>
        public Panel Toggle(string label, Func<bool> get, Action<bool> set) { Hud.RegisterToggle(_id, label, get, set); return this; }

        /// <summary>Bind a central hotkey to an action owned by this panel.</summary>
        public Panel Hotkey(string label, HotlineKey key, Action run) { Hud.RegisterHotkey(_id, label, key, run); return this; }

        /// <summary>Show this panel's own log channel inside the panel.</summary>
        public Panel Log() { Hud.BindPanelLog(_id); return this; }

        /// <summary>Send a log line to this panel's channel (and the combined timeline).</summary>
        public void Write(string message, LogLevel level = LogLevel.Info) { Hud.Log(_id, message, level); }
    }
}
