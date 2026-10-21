// Copyright (c) 2026 DooDesch. DooDesch API License (see LICENSE.md): you may copy this file unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Snitch.Api
{
    /// <summary>
    /// The Snitch profiler's modder API. Reference Snitch.Api.dll OR drop this single file into your mod.
    /// Every call is a zero-overhead no-op when the Snitch profiler is not installed, and lights up
    /// automatically when it is - so you can ship this unconditionally with no hard dependency.
    ///
    /// <code>
    ///   using Snitch.Api;
    ///   using (Profiler.Sample("MyMod.Pathfinding")) { ...expensive work... }   // times a section
    ///   Profiler.RegisterCounter("MyMod.QueueLen", () =&gt; _queue.Count, "items");
    ///   Profiler.RegisterStateProvider("MyMod.Jobs", () =&gt; new StateSnapshot { Title = "Jobs" }
    ///       .Add("running", _running).Add("queued", _queued));
    /// </code>
    ///
    /// Tip: a class named <c>SnitchProbe</c> with a static <c>Register()</c> is auto-discovered and called on
    /// bind (see <see cref="AutoRegister"/>), so your mod's Core doesn't need to wire anything. Snitch also
    /// auto-times every mod's OnUpdate etc., so basic per-mod frame cost needs no code at all.
    ///
    /// All calls MUST be made from the Unity main thread. Counter/state delegates are invoked by the host on
    /// the main thread, so they may safely touch game objects.
    /// </summary>
    /// <remarks>
    /// Ids and labels are free text. The convention is "&lt;ModName&gt;.&lt;Thing&gt;", e.g. "MyMod.Pathfinding".
    /// A section label groups by the part before the first dot; a label without a dot lands in "(ungrouped)".
    /// Section times are in milliseconds. Counters and state providers are polled at the host's PollHz preference,
    /// 4 times a second by default and 1 to 30 at most. Sections only record while sampling is on (<see cref="Enabled"/>); panels,
    /// controls and log lines exist as soon as they are registered.
    /// </remarks>
    /// <example>
    /// A panel with a counter, a slider and a log, set up from a probe class:
    /// <code>
    /// internal static class SnitchProbe
    /// {
    ///     public static void Register()
    ///     {
    ///         Profiler.RegisterPanel("MyMod", "My Mod")
    ///             .Counter("Queue", () =&gt; Jobs.Count, "items")
    ///             .Slider("Spawn radius", 5, 100, () =&gt; Prefs.Radius, v =&gt; Prefs.Radius = (float)v, step: 5, unit: "m")
    ///             .Log();
    ///     }
    /// }
    /// </code>
    /// </example>
    public static class Profiler
    {
        // bound bridge delegates (null until the host is found)
        private static bool _bound;
        private static bool _autoDone;
        private static bool _autoRequested;
        private static bool _probedOnce;
        private static int _lastProbeTick;
        private static string _lastBindError;
        private const int ProbeIntervalMs = 1000;
        private static readonly List<Action> _pending = new List<Action>();
        private static Func<bool> _isEnabled;
        private static Func<string, int> _beginScope;
        private static Action<int> _endScope;
        private static Action<string> _beginLabel;
        private static Action<string> _endLabel;
        private static Action<string, Func<double>, string> _registerCounter;
        private static Action<string> _unregisterCounter;
        private static Action<string, Func<object[]>> _registerState;
        private static Action<string> _unregisterState;
        private static Action<string> _mark;
        private static Action<string, Action, Action> _registerLever;
        private static Action<string, string> _registerPanel;
        private static Action<string, string, string, Action> _registerAction;
        private static Action<string, string, string, Func<bool>, Action<bool>> _registerToggle;
        private static Action<string, string, string, double, double, double, string, Func<double>, Action<double>> _registerSlider;
        private static Action<string, Func<string>> _registerText;
        private static Action<string> _bindPanelLog;
        private static Action<string, int, string> _log;

        /// <summary>True only when the Snitch host is installed AND sampling is currently armed. Gate hot loops
        /// on this for the absolutely-free path: <c>if (Profiler.Enabled) using (Profiler.Sample("X")) { ... }</c>.</summary>
        public static bool Enabled
        {
            get { EnsureBound(); return _isEnabled != null && _isEnabled(); }
        }

        /// <summary>Time a section. <c>using (Profiler.Sample("MyMod.Foo")) { ... }</c>. No heap allocation; a
        /// no-op (default scope) when the host is absent or sampling is off.</summary>
        /// <param name="label">The section name, e.g. "MyMod.Pathfinding". The part before the first dot is the group. Null or empty records as "(unnamed)".</param>
        /// <returns>A scope that stops the timer when it is disposed.</returns>
        public static Scope Sample(string label)
        {
            EnsureBound();
            if (_beginScope == null) return default;
            return new Scope(_beginScope(label));
        }

        /// <summary>Manual section begin (pair with <see cref="End"/>). Prefer <see cref="Sample"/>.</summary>
        /// <param name="label">The section name, e.g. "MyMod.Pathfinding". <see cref="End"/> takes the same string.</param>
        public static void Begin(string label) { EnsureBound(); _beginLabel?.Invoke(label); }
        /// <summary>Manual section end for a <see cref="Begin"/> call.</summary>
        /// <param name="label">The same string that was passed to <see cref="Begin"/>.</param>
        public static void End(string label) { EnsureBound(); _endLabel?.Invoke(label); }

        /// <summary>Register a numeric gauge polled by the host at a few Hz. Re-registering the same id replaces
        /// it. Load-order-proof: if Snitch loads AFTER your mod, the registration is queued and applied on bind.</summary>
        /// <param name="id">The counter id, e.g. "MyMod.QueueLen". An id that starts with "&lt;panelId&gt;." shows inside that panel. Null or empty is ignored by the host.</param>
        /// <param name="read">Returns the current value. Called on the main thread at the poll rate. A throw shows the counter as UNAVAILABLE.</param>
        /// <param name="unit">Text shown after the value, e.g. "items" or "ms". Null shows no unit.</param>
        public static void RegisterCounter(string id, Func<double> read, string unit = null)
        {
            if (read == null) return;
            EnsureBound();
            if (_registerCounter != null) _registerCounter(id, read, unit ?? "");
            else _pending.Add(() => _registerCounter?.Invoke(id, read, unit ?? ""));
        }
        /// <summary>Remove a counter registered with <see cref="RegisterCounter"/>.</summary>
        /// <param name="id">The id passed to <see cref="RegisterCounter"/>.</param>
        public static void UnregisterCounter(string id) { EnsureBound(); _unregisterCounter?.Invoke(id); }

        /// <summary>Register an entity/state-distribution snapshot, polled by the host at a few Hz on the main
        /// thread. Load-order-proof (queued until the host binds).</summary>
        /// <param name="id">The provider id, e.g. "MyMod.Jobs". An id that starts with "&lt;panelId&gt;." shows inside that panel. Null or empty is ignored by the host.</param>
        /// <param name="snapshot">Returns the current distribution. Called on the main thread at the poll rate. Null shows an empty distribution titled with the id.</param>
        public static void RegisterStateProvider(string id, Func<StateSnapshot> snapshot)
        {
            if (snapshot == null) return;
            // marshal the rich snapshot down to primitives so the shim shares no type with the host
            Func<object[]> poll = () =>
            {
                StateSnapshot s = snapshot();
                if (s == null) return new object[] { id, new string[0], new int[0], 0 };
                int n = s.Buckets.Count;
                var names = new string[n];
                var counts = new int[n];
                for (int i = 0; i < n; i++) { names[i] = s.Buckets[i].Name; counts[i] = s.Buckets[i].Count; }
                return new object[] { s.Title ?? id, names, counts, s.Total };
            };
            EnsureBound();
            if (_registerState != null) _registerState(id, poll);
            else _pending.Add(() => _registerState?.Invoke(id, poll));
        }
        /// <summary>Remove a provider registered with <see cref="RegisterStateProvider"/>.</summary>
        /// <param name="id">The id passed to <see cref="RegisterStateProvider"/>.</param>
        public static void UnregisterStateProvider(string id) { EnsureBound(); _unregisterState?.Invoke(id); }

        /// <summary>Annotate a one-off event/spike in the timeline.</summary>
        /// <param name="label">Short text for the event, e.g. "MyMod.SaveLoaded". Null or empty is ignored. The host keeps the last 32 marks.</param>
        public static void Mark(string label) { EnsureBound(); _mark?.Invoke(label); }

        /// <summary>Register an ablation lever for your subsystem so 'snitch ablate &lt;name&gt;' can measure its
        /// causal frame-time cost. <paramref name="apply"/> turns your subsystem OFF, <paramref name="restore"/>
        /// turns it back ON. Both run on the main thread. Load-order-proof (queued until the host binds).</summary>
        /// <param name="name">The lever name typed after <c>snitch ablate</c>, e.g. "mymod". Matched without case. Null or empty is ignored. The same name again replaces the lever.</param>
        /// <param name="apply">Turns the subsystem off. Null is ignored.</param>
        /// <param name="restore">Turns the subsystem back on. Null marks the lever as not reversible.</param>
        public static void RegisterAblationLever(string name, Action apply, Action restore)
        {
            if (string.IsNullOrEmpty(name) || apply == null) return;
            EnsureBound();
            if (_registerLever != null) _registerLever(name, apply, restore);
            else _pending.Add(() => _registerLever?.Invoke(name, apply, restore));
        }

        /// <summary>Declare a panel: a named, toggleable, movable, resizable area in the Snitch overlay + web dashboard
        /// that groups everything this mod reports. Counters/state you register with an id starting "<paramref name="id"/>."
        /// automatically appear inside it. Returns a builder so you can fluently add text/actions/toggles/log. The in-game
        /// replacement for a mod's own debug window. Load-order-proof; a no-op (the builder still works) if Snitch is absent.</summary>
        /// <param name="id">The panel id, usually the mod name, e.g. "Siesta". It is also the panel's log channel. Null or empty registers nothing.</param>
        /// <param name="title">The heading shown on the panel, e.g. "Siesta NPC LOD". Null or empty shows the id.</param>
        /// <returns>A builder whose calls add to this panel.</returns>
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

        /// <summary>A clickable button in a mod's panel - the in-game replacement for a debug hotkey action. Runs on the
        /// main thread when clicked (overlay/dashboard) or via 'snitch act'. Load-order-proof.</summary>
        /// <param name="panelId">The id passed to <see cref="RegisterPanel"/>, e.g. "Siesta".</param>
        /// <param name="label">The button text, e.g. "Force cosmetic". Null or empty is ignored. The same label again replaces the button.
        /// The id for <c>snitch act</c> is "&lt;panelId&gt;:" plus the label in lower case, with each run of other characters than letters and digits as one "-", e.g. "Siesta:force-cosmetic".</param>
        /// <param name="run">Runs on the main thread when the button is pressed.</param>
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
        /// <param name="panelId">The id passed to <see cref="RegisterPanel"/>, e.g. "MyMod".</param>
        /// <param name="label">The text next to the switch, e.g. "Freeze LOD". Null or empty is ignored. The id for <c>snitch toggle</c> is built like the one in <see cref="RegisterAction"/>, e.g. "MyMod:freeze-lod".</param>
        /// <param name="get">Returns the current state. Null is ignored.</param>
        /// <param name="set">Applies a new state. Null is ignored.</param>
        public static void RegisterToggle(string panelId, string label, Func<bool> get, Action<bool> set)
        {
            if (get == null || set == null || string.IsNullOrEmpty(label)) return;
            string toggleId = panelId + ":" + Slug(label);
            EnsureBound();
            if (_registerToggle != null) _registerToggle(panelId, toggleId, label, get, set);
            else _pending.Add(() => _registerToggle?.Invoke(panelId, toggleId, label, get, set));
        }

        /// <summary>
        /// A draggable value in a mod's panel - the in-game replacement for typing a number into a console to find it.
        /// Appears as a real slider in the Snitch overlay and in the web dashboard, and is settable by id with
        /// <c>snitch slider</c>.
        ///
        /// <paramref name="get"/> reports the current value, <paramref name="set"/> applies it; both run on the main
        /// thread. The host clamps to [<paramref name="min"/>, <paramref name="max"/>] and snaps to
        /// <paramref name="step"/> (0 = continuous) BEFORE calling the setter, so a setter never sees a value outside
        /// the range it declared - wherever the write came from. Load-order-proof.
        /// </summary>
        /// <param name="panelId">The id passed to <see cref="RegisterPanel"/>, e.g. "MyMod".</param>
        /// <param name="label">The text next to the slider, e.g. "pull force". Null or empty is ignored. The id for <c>snitch slider</c> is built like the one in <see cref="RegisterAction"/>, e.g. "MyMod:pull-force".</param>
        /// <param name="min">The lowest value.</param>
        /// <param name="max">The highest value. It must be greater than <paramref name="min"/>, otherwise nothing is registered.</param>
        /// <param name="get">Returns the current value. Null is ignored.</param>
        /// <param name="set">Applies a new value, already clamped and snapped. Null is ignored.</param>
        /// <param name="step">The snap size, e.g. 500. 0 or less is continuous.</param>
        /// <param name="unit">Text shown after the value, e.g. "N" or "m". Null shows no unit.</param>
        /// <example>
        /// <code>
        ///   panel.Slider("pull force", 0, 40000, () =&gt; Prefs.Pull, v =&gt; Prefs.Pull = (float)v, step: 500, unit: "N");
        /// </code>
        /// </example>
        public static void RegisterSlider(string panelId, string label, double min, double max,
                                          Func<double> get, Action<double> set, double step = 0d, string unit = null)
        {
            if (get == null || set == null || string.IsNullOrEmpty(label) || max <= min) return;
            string sliderId = panelId + ":" + Slug(label);
            string u = unit ?? "";
            EnsureBound();
            if (_registerSlider != null) _registerSlider(panelId, sliderId, label, min, max, step, u, get, set);
            else _pending.Add(() => _registerSlider?.Invoke(panelId, sliderId, label, min, max, step, u, get, set));
        }

        /// <summary>A free-text, multi-line readout in a mod's panel (anything a counter/distribution can't express).
        /// Polled by the host on the main thread. Load-order-proof.</summary>
        /// <param name="panelId">The id passed to <see cref="RegisterPanel"/>, e.g. "MyMod".</param>
        /// <param name="provider">Returns the text to show. Null is ignored. Each call adds one more readout.</param>
        public static void RegisterText(string panelId, Func<string> provider)
        {
            if (provider == null) return;
            EnsureBound();
            if (_registerText != null) _registerText(panelId, provider);
            else _pending.Add(() => _registerText?.Invoke(panelId, provider));
        }

        /// <summary>Mark that a panel should display its own log channel (the lines you send via <see cref="Log"/> /
        /// <see cref="Panel.Write"/> with the same id). Load-order-proof.</summary>
        /// <param name="panelId">The id passed to <see cref="RegisterPanel"/>, e.g. "MyMod". It is also the log channel.</param>
        public static void BindPanelLog(string panelId)
        {
            EnsureBound();
            if (_bindPanelLog != null) _bindPanelLog(panelId);
            else _pending.Add(() => _bindPanelLog?.Invoke(panelId));
        }

        /// <summary>Send a log line to a channel (use your mod/panel id as the channel). It appears in that mod's panel
        /// log AND in Snitch's combined timeline. Keep calling your own MelonLogger too if you want it in Latest.log.
        /// Load-order-proof; a no-op if Snitch is absent.</summary>
        /// <param name="channel">The channel name, the panel id for a panel's log, e.g. "MyMod". Null or empty logs to "misc".</param>
        /// <param name="message">The log text. Null or empty is ignored.</param>
        /// <param name="level">Info, Warning or Error. The host keeps the last 400 lines per channel.</param>
        public static void Log(string channel, string message, LogLevel level = LogLevel.Info)
        {
            if (string.IsNullOrEmpty(message)) return;
            int lv = (int)level;
            EnsureBound();
            if (_log != null) _log(channel, lv, message);
            else _pending.Add(() => _log?.Invoke(channel, lv, message));
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

        /// <summary>Discover a convention type named <c>SnitchProbe</c> with a static <c>Register()</c> in THIS
        /// mod's own assembly and invoke it once - so a mod never has to wire a Register() call into its Core.
        /// Drive it from a <c>[ModuleInitializer]</c> in your probe file. No-op + load-order-proof: discovery is
        /// deferred until the host binds, and is a permanent no-op if Snitch is not installed.</summary>
        public static void AutoRegister()
        {
            _autoRequested = true;   // the bind runs it if the host is not up yet (load-order-proof, both directions)
            EnsureBound();
            if (_bound) RunAutoRegister();
        }

        private static void RunAutoRegister()
        {
            if (_autoDone) return;
            _autoDone = true;   // latch before invoking so a throw can't loop; the host reads it to skip this mod's probe
            try
            {
                Assembly self = typeof(Profiler).Assembly;   // only this mod's assembly - single, fast, no AppDomain scan
                Type probe = self.GetType("SnitchProbe", false) ?? FindByLeafName(self, "SnitchProbe");
                MethodInfo reg = probe?.GetMethod("Register",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                reg?.Invoke(null, null);
            }
            catch (Exception e)
            {
                // A probe that throws must not crash the mod; it goes to Snitch's log instead.
                Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                _log?.Invoke("snitch", (int)LogLevel.Error,
                    $"SnitchProbe.Register in {typeof(Profiler).Assembly.GetName().Name} threw: {inner.GetType().Name}: {inner.Message}");
            }
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

        // ----- reflection handshake (runs until it binds, then latches) -----

        private static void EnsureBound()
        {
            if (_bound) return;   // bound once, never probe again (fast path)

            // Probed at most once a second, never every call: a mod that uses the API every frame while Snitch is
            // absent must not pay for the search each time. A time gate rather than a call count, because a mod that
            // registers a handful of things before Snitch loads may never make enough calls to reach a count.
            int now = Environment.TickCount;
            if (_probedOnce && unchecked(now - _lastProbeTick) < ProbeIntervalMs) return;
            _probedOnce = true;
            _lastProbeTick = now;
            TryBind();
        }

        /// <summary>
        /// Called by the Snitch host through reflection while it scans the loaded mods, so registrations queued before
        /// Snitch loaded are applied even when this mod makes no further API call. Returns how many queued
        /// registrations it applied, or -1 when the bind failed (the reason is in <c>_lastBindError</c>).
        /// </summary>
        private static int BindNow() => _bound ? 0 : TryBind();

        private static int TryBind()
        {
            try
            {
                Type t = FindBridge();
                if (t == null) { _lastBindError = "Snitch is not loaded"; return -1; }   // probed again later
                object abi = t.GetField("AbiVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (abi is int v && v < 1) { _lastBindError = "host ABI version " + v; return -1; }

                _isEnabled = Get<Func<bool>>(t, "IsEnabled");
                _beginScope = Get<Func<string, int>>(t, "BeginScope");
                _endScope = Get<Action<int>>(t, "EndScope");
                _beginLabel = Get<Action<string>>(t, "BeginLabel");
                _endLabel = Get<Action<string>>(t, "EndLabel");
                _registerCounter = Get<Action<string, Func<double>, string>>(t, "RegisterCounter");
                _unregisterCounter = Get<Action<string>>(t, "UnregisterCounter");
                _registerState = Get<Action<string, Func<object[]>>>(t, "RegisterStateProvider");
                _unregisterState = Get<Action<string>>(t, "UnregisterStateProvider");
                _mark = Get<Action<string>>(t, "Mark");
                _registerLever = Get<Action<string, Action, Action>>(t, "RegisterAblationLever");
                _registerPanel = Get<Action<string, string>>(t, "RegisterPanel");
                _registerAction = Get<Action<string, string, string, Action>>(t, "RegisterAction");
                _registerToggle = Get<Action<string, string, string, Func<bool>, Action<bool>>>(t, "RegisterToggle");
                _registerSlider = Get<Action<string, string, string, double, double, double, string, Func<double>, Action<double>>>(t, "RegisterSlider");
                _registerText = Get<Action<string, Func<string>>>(t, "RegisterText");
                _bindPanelLog = Get<Action<string>>(t, "BindPanelLog");
                _log = Get<Action<string, int, string>>(t, "Log");

                if (_isEnabled == null) { _lastBindError = "host bridge is not filled in yet"; return -1; }   // retried later
                _bound = true;
                _lastBindError = null;

                // flush any registrations made before the host was up
                int applied = 0, failed = 0;
                string firstError = null;
                for (int i = 0; i < _pending.Count; i++)
                {
                    try { _pending[i](); applied++; }
                    catch (Exception e) { failed++; if (firstError == null) firstError = e.GetType().Name + ": " + e.Message; }
                }
                _pending.Clear();
                if (failed > 0)
                    _log?.Invoke("snitch", (int)LogLevel.Error,
                        $"{failed} queued registration(s) from {typeof(Profiler).Assembly.GetName().Name} threw on bind, first: {firstError}");

                if (_autoRequested) RunAutoRegister();
                return applied;
            }
            catch (Exception e)
            {
                // Nothing to log to before the host is bound; the host reads this when it binds the shim itself.
                _lastBindError = e.GetType().Name + ": " + e.Message;
                return -1;
            }
        }

        private static T Get<T>(Type t, string field) where T : class
        {
            object v = t.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return v as T;   // works because Func<>/Action<> are shared BCL types in both assemblies
        }

        /// <summary>
        /// The host's bridge type, or null while the host is absent.
        ///
        /// Deliberately walks the assemblies that are already loaded instead of asking for
        /// <c>"Snitch.Bridge.SnitchBridge, Snitch"</c> by assembly-qualified name. That name makes the runtime
        /// LOAD the assembly, and when there is no Snitch.dll every single call goes through MelonLoader's
        /// resolver, which searches five directories and logs both the attempt and the failure. A Debug build
        /// shipped without the profiler - which is what a tester gets - turned that into hundreds of directory
        /// scans during a world load.
        ///
        /// Nothing is lost by not asking: the host is a MelonLoader mod, so by the time it can answer at all it
        /// is already in the AppDomain.
        /// </summary>
        private static Type FindBridge()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = asm.GetType("Snitch.Bridge.SnitchBridge", false); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        internal static void InvokeEndScope(int token) { _endScope?.Invoke(token); }
    }

    /// <summary>Severity for <see cref="Profiler.Log"/>. Mirrors the host's 0=info / 1=warning / 2=error.</summary>
    public enum LogLevel { Info = 0, Warning = 1, Error = 2 }

    /// <summary>
    /// Fluent builder for a mod's Snitch panel (returned by <see cref="Profiler.RegisterPanel"/>). Counters/state added
    /// here get an id of "&lt;panelId&gt;.&lt;name&gt;" so they group under the panel automatically. Everything is a safe
    /// no-op when the Snitch host is absent.
    /// </summary>
    public sealed class Panel
    {
        private readonly string _id;
        internal Panel(string id) { _id = id ?? ""; }

        /// <summary>The panel id (also the default log channel).</summary>
        public string Id => _id;

        /// <summary>A numeric gauge shown in this panel. Registered as "&lt;panelId&gt;.&lt;name&gt;".</summary>
        /// <param name="name">The counter name inside the panel, e.g. "Queue". Null or empty uses the panel id itself.</param>
        /// <param name="read">Returns the current value. Called on the main thread at the poll rate.</param>
        /// <param name="unit">Text shown after the value, e.g. "items" or "ms". Null shows no unit.</param>
        public Panel Counter(string name, Func<double> read, string unit = null) { Profiler.RegisterCounter(Join(name), read, unit); return this; }

        /// <summary>A state distribution shown in this panel (uses the panel id).</summary>
        /// <param name="snapshot">Returns the current distribution. Called on the main thread at the poll rate.</param>
        public Panel State(Func<StateSnapshot> snapshot) { Profiler.RegisterStateProvider(_id, snapshot); return this; }

        /// <summary>A named state distribution shown in this panel. Registered as "&lt;panelId&gt;.&lt;name&gt;".</summary>
        /// <param name="name">The distribution name inside the panel, e.g. "Jobs". Null or empty uses the panel id itself.</param>
        /// <param name="snapshot">Returns the current distribution. Called on the main thread at the poll rate.</param>
        public Panel State(string name, Func<StateSnapshot> snapshot) { Profiler.RegisterStateProvider(Join(name), snapshot); return this; }

        /// <summary>A free-text, multi-line readout in this panel.</summary>
        public Panel Text(Func<string> provider) { Profiler.RegisterText(_id, provider); return this; }

        /// <summary>A clickable button (replaces a debug hotkey action).</summary>
        /// <param name="label">The button text, e.g. "Force cosmetic". The id for <c>snitch act</c> is built as in <see cref="Profiler.RegisterAction"/>.</param>
        /// <param name="run">Runs on the main thread when the button is pressed.</param>
        public Panel Action(string label, Action run) { Profiler.RegisterAction(_id, label, run); return this; }

        /// <summary>An on/off control (replaces a debug toggle hotkey).</summary>
        /// <param name="label">The text next to the switch. The id for <c>snitch toggle</c> is built as in <see cref="Profiler.RegisterAction"/>.</param>
        /// <param name="get">Returns the current state.</param>
        /// <param name="set">Applies a new state.</param>
        public Panel Toggle(string label, Func<bool> get, Action<bool> set) { Profiler.RegisterToggle(_id, label, get, set); return this; }

        /// <summary>A draggable value (replaces typing a number into a console to find it). The host clamps to the
        /// range and snaps to <paramref name="step"/> before your setter sees it.</summary>
        /// <param name="label">The text next to the slider. The id for <c>snitch slider</c> is built as in <see cref="Profiler.RegisterAction"/>.</param>
        /// <param name="min">The lowest value.</param>
        /// <param name="max">The highest value. It must be greater than <paramref name="min"/>, otherwise nothing is registered.</param>
        /// <param name="get">Returns the current value.</param>
        /// <param name="set">Applies a new value, already clamped and snapped.</param>
        /// <param name="step">The snap size, e.g. 5. 0 or less is continuous.</param>
        /// <param name="unit">Text shown after the value, e.g. "m". Null shows no unit.</param>
        public Panel Slider(string label, double min, double max, Func<double> get, Action<double> set, double step = 0d, string unit = null)
        { Profiler.RegisterSlider(_id, label, min, max, get, set, step, unit); return this; }

        /// <summary>Show this panel's own log channel inside the panel.</summary>
        public Panel Log() { Profiler.BindPanelLog(_id); return this; }

        /// <summary>Send a log line to this panel's channel (and the combined timeline).</summary>
        /// <param name="message">The log text. Null or empty is ignored.</param>
        /// <param name="level">Info, Warning or Error.</param>
        public void Write(string message, LogLevel level = LogLevel.Info) { Profiler.Log(_id, message, level); }

        private string Join(string name) => string.IsNullOrEmpty(name) ? _id : _id + "." + name;
    }

    /// <summary>Zero-heap-alloc timing scope returned by <see cref="Profiler.Sample"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly int _token;
        internal Scope(int token) { _token = token; }
        public void Dispose() { if (_token != 0) Profiler.InvokeEndScope(_token); }
    }

    /// <summary>A small ordered name-&gt;count distribution returned by a state provider.</summary>
    /// <example>
    /// <code>
    /// Profiler.RegisterStateProvider("MyMod.Widgets", () =&gt;
    ///     new StateSnapshot { Title = "Widgets" }.Add("red", red).Add("green", green).Add("blue", blue));
    /// </code>
    /// </example>
    public sealed class StateSnapshot
    {
        /// <summary>The heading shown above the distribution.</summary>
        /// <value>Free text, e.g. "Example Widgets". Null shows the provider id.</value>
        public string Title;
        /// <summary>The total the buckets are measured against.</summary>
        /// <value>0 (the default) uses the sum of all bucket counts.</value>
        public int Total;
        /// <summary>The buckets in the order they are shown.</summary>
        public readonly List<Bucket> Buckets = new List<Bucket>();
        /// <summary>Append a bucket.</summary>
        /// <param name="name">The bucket label, e.g. "red".</param>
        /// <param name="count">How many items fall into the bucket.</param>
        /// <returns>This snapshot, for chaining.</returns>
        public StateSnapshot Add(string name, int count) { Buckets.Add(new Bucket(name, count)); return this; }
        public void Clear() { Total = 0; Buckets.Clear(); }
    }

    /// <summary>One named count in a <see cref="StateSnapshot"/>.</summary>
    public readonly struct Bucket
    {
        public readonly string Name;
        public readonly int Count;
        public Bucket(string name, int count) { Name = name; Count = count; }
    }
}
