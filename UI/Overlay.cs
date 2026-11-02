using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sideload.Api;
using UnityEngine;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;
using Snitch.Server;
using UnityEngine.UI;

namespace Snitch.UI
{
    internal enum OverlayMode { Hidden, Compact, Expanded }

    /// <summary>
    /// Snitch's in-game surface: a Sideload page on its own screen-space overlay canvas, so tone mapping cannot
    /// flatten its colours. Compact is a lower-third in the bottom-left corner with the top riders and the
    /// unattributed row; it carries no raycaster, so it never takes a click meant for the game. Expanded adds the
    /// sampling controls, the phone pairing and every mod's panel, and does take the mouse (it needs a free cursor,
    /// as the overlay it replaces did). Nothing is mounted while hidden.
    /// </summary>
    internal static class Overlay
    {
        private const string SurfaceId = "snitch-overlay";
        private const string Bundle = "Snitch.Assets.overlay";

        // The host rect is a fixed 760 reference pixels wide, anchored to the left edge, so even the expanded state
        // stays left of the screen centre. The page is authored for that width (design short side 760).
        private const float HostWidth = 760f;
        private const float HostMargin = 24f;
        private const float HostTopAnchor = 0.92f;
        // The vanilla hotbar occupies the bottom ~10% of the screen; the card's bottom edge stays above it (1080 reference px).
        private const float HostBottom = 124f;
        // Expanded standings are capped so the mod panels below stay on screen; the rest is a "+N more" line.
        private const int ExpandedRows = 10;

        private const double EmitEvery = 0.25;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex Tags = new Regex("<[^>]+>", RegexOptions.Compiled);

        private static GameObject _root;
        private static GraphicRaycaster _raycaster;
        private static SurfaceHandle _surface;

        private static OverlayMode _mode = OverlayMode.Hidden;
        private static int _window = 30;
        private static bool _dirty;
        private static double _lastEmit;
        private static float _lastMountCheck;
        private static string _qrUrl = "";

        internal static OverlayMode Mode => _mode;

        // ----- mode -----

        /// <summary>Hidden -> compact -> expanded -> hidden, the order the overlay key walks.</summary>
        internal static void Cycle()
        {
            SetMode(_mode == OverlayMode.Hidden ? OverlayMode.Compact
                  : _mode == OverlayMode.Compact ? OverlayMode.Expanded : OverlayMode.Hidden, "key");
        }

        internal static bool SetMode(OverlayMode mode, string why)
        {
            if (mode == _mode && (mode == OverlayMode.Hidden || Surfaces.IsMounted(SurfaceId)))
            {
                Core.Log?.Msg("[overlay] already " + mode.ToString().ToLowerInvariant() + " (" + why + ").");
                return true;
            }

            if (mode == OverlayMode.Hidden)
            {
                Unmount();
                _mode = mode;
                Core.Log?.Msg("[overlay] hidden (" + why + ").");
                return true;
            }

            if (!Surfaces.Available)
            {
                Core.Log?.Error("[overlay] Sideload is not available, so the overlay cannot be drawn. "
                              + "Install Sideload 1.13 or newer, or use 'snitch dashboard' for the web view.");
                return false;
            }

            if (!Surfaces.IsMounted(SurfaceId) && !Mount()) return false;

            _mode = mode;
            if (_raycaster != null) _raycaster.enabled = mode == OverlayMode.Expanded;
            _dirty = true;
            Core.Log?.Msg("[overlay] " + mode.ToString().ToLowerInvariant() + " (" + why + ").");
            return true;
        }

        // ----- per frame -----

        /// <summary>Pumped from Core.OnUpdate: reads the key, keeps the standings history, and pushes state to the
        /// page. The history is kept while hidden so the table is already full when the overlay opens.</summary>
        internal static void Tick()
        {
            if (Preferences.Enabled && Input.GetKeyDown(Preferences.OverlayKey)) Cycle();

            double now = Time.realtimeSinceStartupAsDouble;
            if (Standings.Pull(now)) _dirty = true;

            if (_mode == OverlayMode.Hidden) return;

            // A scene change can destroy the host; the surface then reports itself unmounted.
            if (Time.realtimeSinceStartup - _lastMountCheck > 1f)
            {
                _lastMountCheck = Time.realtimeSinceStartup;
                if (!Surfaces.IsMounted(SurfaceId))
                {
                    Core.Log?.Warning("[overlay] the surface is gone (scene change?), mounting it again.");
                    OverlayMode keep = _mode;
                    Unmount();
                    _mode = OverlayMode.Hidden;
                    SetMode(keep, "remount");
                    return;
                }
            }

            if (!_dirty && now - _lastEmit < EmitEvery) return;
            // Not on every frame: the page rebuilds on a changed value, and the numbers only move per poll.
            if (!_dirty && now - _lastEmit < 1.0 / Math.Max(1f, Preferences.PollHz)) return;

            _lastEmit = now;
            _dirty = false;
            Push();
        }

        private static void Push()
        {
            if (_surface == null) return;
            try
            {
                SupplyQr();
                _surface.Emit("state", StateJson());
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not build or send the state: " + e.Message);
            }
        }

        private static void SupplyQr()
        {
            byte[] png = QrImage.Build();
            string url = png == null ? "" : QrImage.Url;
            if (url == _qrUrl) return;
            _qrUrl = url;
            _surface.Image("qr", png);
            Core.Log?.Msg(png == null ? "[overlay] phone QR cleared." : "[overlay] phone QR published (" + png.Length + " bytes).");
        }

        // ----- mounting -----

        private static bool Mount()
        {
            try
            {
                Unmount();

                _root = new GameObject("Snitch_Overlay");
                UnityEngine.Object.DontDestroyOnLoad(_root);

                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // Above the game's HUD, below a Sideload notice or the phone.
                canvas.sortingOrder = 29000;

                // A fixed 1920x1080 reference, so the panel keeps its share of the screen at any resolution.
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0f;

                // Only the expanded state is interactive. A raycaster swallows every click inside its rect, and the
                // compact lower-third sits over the game.
                _raycaster = _root.AddComponent<GraphicRaycaster>();
                _raycaster.enabled = false;

                var holder = new GameObject("overlay");
                var rect = holder.AddComponent<RectTransform>();
                rect.SetParent(_root.transform, false);
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, HostTopAnchor);
                rect.pivot = new Vector2(0f, 0f);
                rect.sizeDelta = new Vector2(HostWidth, 0f);
                rect.anchoredPosition = new Vector2(HostMargin, HostBottom);

                _surface = Surfaces.Mount(rect, SurfaceId, Bundle, designShortSide: HostWidth,
                                          hostAssembly: typeof(Overlay).Assembly);
                RegisterCalls(_surface);

                if (!Surfaces.IsMounted(SurfaceId))
                {
                    Core.Log?.Error("[overlay] the page could not be mounted - see the Sideload log lines above.");
                    Unmount();
                    return false;
                }

                _qrUrl = "\u0001";   // forces the next Push to publish the picture again
                Core.Log?.Msg("[overlay] surface '" + SurfaceId + "' mounted.");
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] mounting failed: " + e.Message);
                Unmount();
                return false;
            }
        }

        private static void Unmount()
        {
            try { if (Surfaces.Available) Surfaces.Unmount(SurfaceId); }
            catch (Exception e) { Core.Log?.Warning("[overlay] unmount failed: " + e.Message); }

            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); }
                catch (Exception e) { Core.Log?.Warning("[overlay] could not destroy the canvas: " + e.Message); }
            }
            _root = null;
            _raycaster = null;
            _surface = null;
        }

        // ----- page calls -----

        private static void RegisterCalls(SurfaceHandle s)
        {
            s.OnCall("page.ready", _ => Call("page.ready", () => { _dirty = true; _qrUrl = "\u0001"; return "ok"; }));
            s.OnCall("page.log", a => Call("page.log", () => { Core.Log?.Warning("[overlay] page: " + a); return "ok"; }));
            s.OnCall("ui.mode", a => Call("ui.mode", () =>
            {
                // Applied from the next Tick: the surface is being torn down or rebuilt inside its own call otherwise.
                if (a == "expanded") _pending = OverlayMode.Expanded;
                else if (a == "compact") _pending = OverlayMode.Compact;
                else if (a == "hidden") _pending = OverlayMode.Hidden;
                else { Core.Log?.Warning("[overlay] ui.mode got '" + a + "'."); return "bad"; }
                return "ok";
            }));
            s.OnCall("ui.window", a => Call("ui.window", () =>
            {
                if (!int.TryParse(a, NumberStyles.Integer, Inv, out int w) || (w != 10 && w != 30 && w != 120))
                {
                    Core.Log?.Warning("[overlay] ui.window got '" + a + "', expected 10, 30 or 120.");
                    return "bad";
                }
                _window = w;
                _dirty = true;
                return "ok";
            }));
            s.OnCall("snitch.sampling", a => Call("snitch.sampling", () =>
            {
                if (a == "start") SnitchCore.Start();
                else if (a == "stop") SnitchCore.Stop();
                else if (a == "reset") { SnitchCore.Stop(); SnitchCore.Start(); }
                else { Core.Log?.Warning("[overlay] snitch.sampling got '" + a + "'."); return "bad"; }
                Core.Log?.Msg("[overlay] sampling " + a + ".");
                _dirty = true;
                return "ok";
            }));
            s.OnCall("snitch.dashboard", _ => Call("snitch.dashboard", () => { Core.OpenDashboard(); return "ok"; }));
            s.OnCall("snitch.phone", a => Call("snitch.phone", () =>
            {
                Core.SetLanRemote(a == "1");
                _dirty = true;
                return "ok";
            }));
            s.OnCall("panel.act", a => Call("panel.act", () =>
            {
                bool ok = PanelRegistry.Invoke(a);
                if (!ok) Core.Log?.Warning("[overlay] no action '" + a + "'.");
                _dirty = true;
                return ok ? "ok" : "bad";
            }));
            s.OnCall("panel.toggle", a => Call("panel.toggle", () =>
            {
                string[] p = a.Split('\t');
                if (p.Length != 2) { Core.Log?.Warning("[overlay] panel.toggle got '" + a + "'."); return "bad"; }
                bool ok = PanelRegistry.SetToggle(p[0], p[1] == "1");
                if (!ok) Core.Log?.Warning("[overlay] no toggle '" + p[0] + "'.");
                _dirty = true;
                return ok ? "ok" : "bad";
            }));
            s.OnCall("panel.slider", a => Call("panel.slider", () =>
            {
                string[] p = a.Split('\t');
                if (p.Length != 2 || !double.TryParse(p[1], NumberStyles.Float, Inv, out double v))
                {
                    Core.Log?.Warning("[overlay] panel.slider got '" + a + "'.");
                    return "bad";
                }
                bool ok = PanelRegistry.SetSlider(p[0], v);
                if (!ok) Core.Log?.Warning("[overlay] no slider '" + p[0] + "'.");
                _dirty = true;
                return ok ? "ok" : "bad";
            }));
        }

        private static OverlayMode? _pending;

        /// <summary>Applies a mode the page asked for. Called from Tick through Core.OnUpdate.</summary>
        internal static void ApplyPending()
        {
            if (_pending == null) return;
            OverlayMode m = _pending.Value;
            _pending = null;
            SetMode(m, "page");
        }

        private static string Call(string name, Func<string> body)
        {
            try { return body(); }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] call '" + name + "' failed: " + e.Message);
                return "error";
            }
        }

        // ----- state for the page -----

        private static string StateJson()
        {
            var sb = new StringBuilder(4096);
            FrameStats f = SnitchCore.LatestFrame;
            double win = _window;
            double frameMs = Standings.FrameMs(win);
            AttributionStats at = SnitchCore.LatestAttribution;
            bool expanded = _mode == OverlayMode.Expanded;

            List<Rider> riders = Standings.Rank(win);
            var names = new List<string>(PanelRegistry.Count + 8);
            for (int i = 0; i < PanelRegistry.All.Count; i++) names.Add(PanelRegistry.All[i].Id);
            foreach (string g in Standings.Groups()) names.Add(g);
            Identity.Refresh(names);

            sb.Append("{\"mode\":\"").Append(expanded ? "expanded" : "compact").Append('"');
            Num(sb, "win", win);
            Bool(sb, "active", SnitchCore.Active);
            Num(sb, "samples", Standings.Count);
            Num(sb, "fps", frameMs > 0 ? 1000.0 / frameMs : 0);
            Num(sb, "ms", frameMs);
            Num(sb, "p95", f.P95Ms);
            Num(sb, "gc", f.Gc0Per1000);
            Num(sb, "minFps", f.MinFps);
            Num(sb, "bad", at.PointsAtHiddenWork ? at.SpikeFrames : 0);
            Str(sb, "key", Preferences.OverlayKey.ToString());

            double leader = riders.Count > 0 && !riders[0].Dnf ? riders[0].Ms : 0;
            Num(sb, "scale", Standings.NiceCeil(leader));

            // Compact shows the top three and the unattributed row; expanded the top ten and the unattributed row.
            sb.Append(",\"riders\":[");
            bool first = true;
            int shown = 0;
            int hidden = 0;
            for (int i = 0; i < riders.Count; i++)
            {
                Rider r = riders[i];
                bool unattr = Identity.IsUnattributed(r.Group);
                if (!unattr && shown >= (expanded ? ExpandedRows : 3)) { hidden++; continue; }
                if (!first) sb.Append(',');
                first = false;
                shown++;
                int bib = Identity.Bib(r.Group);
                sb.Append("{\"k\":\"").Append(WireProtocol.Esc(r.Key)).Append('"');
                Str(sb, "g", r.Group);
                Str(sb, "n", unattr ? r.Label : StripGroup(r.Label, r.Group));
                Num(sb, "bib", bib);
                Str(sb, "c", Identity.Color(r.Group));
                Num(sb, "rank", r.Rank);
                Num(sb, "ms", r.Ms);
                Num(sb, "gap", r.Gap);
                Num(sb, "max", r.MaxMs);
                Num(sb, "calls", r.Calls);
                Bool(sb, "dnf", r.Dnf);
                Num(sb, "silent", Math.Floor(r.SilentFor));
                sb.Append('}');
            }
            sb.Append(']');
            Num(sb, "more", hidden);

            // phone pairing
            sb.Append(",\"phone\":{\"on\":").Append(LanServer.Running ? "true" : "false");
            sb.Append(",\"qr\":").Append(_qrUrl.Length > 0 && _qrUrl[0] != '\u0001' ? "true" : "false");
            sb.Append(",\"url\":\"").Append(WireProtocol.Esc(LanServer.Running ? PhoneLine() : "")).Append("\"}");

            if (expanded)
            {
                sb.Append(",\"states\":[");
                var states = SnitchCore.LatestStates;
                for (int i = 0; states != null && i < states.Count; i++)
                {
                    StateSnapshot ss = states[i];
                    var line = new StringBuilder();
                    for (int k = 0; k < ss.Buckets.Count; k++)
                    {
                        if (k > 0) line.Append("  ");
                        line.Append(ss.Buckets[k].Name).Append(' ').Append(ss.Buckets[k].Count);
                    }
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"t\":\"").Append(WireProtocol.Esc(ss.Title)).Append("\",\"n\":").Append(ss.EffectiveTotal())
                      .Append(",\"b\":\"").Append(WireProtocol.Esc(line.ToString())).Append("\"}");
                }
                sb.Append("],\"panels\":[");
                for (int i = 0; i < PanelRegistry.All.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    PanelJson(sb, PanelRegistry.All[i]);
                }
                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        private static void PanelJson(StringBuilder sb, PanelModel p)
        {
            sb.Append("{\"id\":\"").Append(WireProtocol.Esc(p.Id)).Append('"');
            Str(sb, "title", p.Title);
            Num(sb, "bib", Identity.Bib(p.Id));
            Str(sb, "c", Identity.Color(p.Id));

            sb.Append(",\"text\":[");
            bool first = true;
            for (int i = 0; i < p.Texts.Count; i++)
            {
                string t;
                try { t = p.Texts[i](); }
                catch (Exception e)
                {
                    Core.Log?.Warning("[overlay] text provider of panel '" + p.Id + "' threw: " + e.Message);
                    continue;
                }
                foreach (string line in Tags.Replace(t ?? "", "").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(WireProtocol.Esc(line)).Append('"');
                }
            }
            foreach (string line in ProfilerHud.PanelMetrics(p.Id))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(WireProtocol.Esc(line)).Append('"');
            }
            sb.Append(']');

            sb.Append(",\"actions\":[");
            for (int i = 0; i < p.Actions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(WireProtocol.Esc(p.Actions[i].Id)).Append("\",\"l\":\"")
                  .Append(WireProtocol.Esc(p.Actions[i].Label)).Append("\"}");
            }
            sb.Append("],\"toggles\":[");
            for (int i = 0; i < p.Toggles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(WireProtocol.Esc(p.Toggles[i].Id)).Append("\",\"l\":\"")
                  .Append(WireProtocol.Esc(p.Toggles[i].Label)).Append("\",\"on\":")
                  .Append(PanelRegistry.GetToggle(p.Toggles[i].Id) ? "true" : "false").Append('}');
            }
            sb.Append("],\"sliders\":[");
            for (int i = 0; i < p.Sliders.Count; i++)
            {
                SliderItem s = p.Sliders[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(WireProtocol.Esc(s.Id)).Append("\",\"l\":\"").Append(WireProtocol.Esc(s.Label))
                  .Append("\",\"u\":\"").Append(WireProtocol.Esc(s.Unit)).Append('"');
                Num(sb, "min", s.Min);
                Num(sb, "max", s.Max);
                Num(sb, "step", s.Step);
                Num(sb, "v", s.Read());
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"log\":[");
            if (p.HasLog)
            {
                List<LogEntry> log = LogHub.Channel(p.Id, 12);
                for (int i = 0; i < log.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"t\":\"").Append(WireProtocol.Esc(log[i].Time)).Append("\",\"l\":").Append(log[i].Lvl)
                      .Append(",\"m\":\"").Append(WireProtocol.Esc(log[i].Msg)).Append("\"}");
                }
            }
            sb.Append("]}");
        }

        private static string PhoneLine()
        {
            return RelayHost.Running && !string.IsNullOrEmpty(RelayHost.Code)
                ? "Relay session " + RelayHost.Code
                : "http://" + LanServer.Ip + ":" + LanServer.Port;
        }

        /// <summary>A section label is "Group.Rest"; the bib already says the group, so the row shows the rest.</summary>
        private static string StripGroup(string label, string group)
        {
            if (!string.IsNullOrEmpty(group) && label != null && label.Length > group.Length + 1
                && label.StartsWith(group, StringComparison.Ordinal) && label[group.Length] == '.')
                return label.Substring(group.Length + 1);
            return label ?? "";
        }

        private static void Num(StringBuilder sb, string key, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            sb.Append(",\"").Append(key).Append("\":").Append(v.ToString("0.###", Inv));
        }

        private static void Bool(StringBuilder sb, string key, bool v)
            => sb.Append(",\"").Append(key).Append("\":").Append(v ? "true" : "false");

        private static void Str(StringBuilder sb, string key, string v)
            => sb.Append(",\"").Append(key).Append("\":\"").Append(WireProtocol.Esc(v ?? "")).Append('"');
    }
}
