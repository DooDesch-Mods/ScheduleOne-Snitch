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
    /// Snitch's in-game surface: a Sideload page on its own screen-space overlay canvas, drawn after the game's
    /// post-processing. Compact is drawn bottom-left above the hotbar with the top riders and the unattributed row;
    /// its canvas has the raycaster switched off, so clicks reach the game. Expanded adds the sampling controls, the
    /// phone pairing and every mod's panel, enables the raycaster and frees the cursor. The page is built once
    /// in the background after the save loads and its root is switched off while hidden, so opening only switches it on.
    /// </summary>
    internal static class Overlay
    {
        private const string SurfaceId = "snitch-overlay";
        private const string Bundle = "Snitch.Assets.overlay";

        // The host rect covers the whole canvas (1920 reference px wide), so the page can place windows anywhere. The
        // page is authored in reference pixels: its design short side is the canvas height, one css px per reference px.
        private const float ReferenceWidth = 1920f;
        // Expanded standings are capped so the window stays short; the rest is a "+N more" line.
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

        // Window drags (pointer capture), @font-face and the surface repaint fix first shipped together in this release.
        private const string RequiredSideload = "1.36.1";

        /// <summary>Reads the loaded Sideload version from MelonLoader's registered melons and logs once whether it is
        /// new enough for the overlay. Informational only: the overlay is not blocked either way. Run after all melons
        /// have initialised so Sideload is registered.</summary>
        internal static void CheckSideloadVersion()
        {
            string found = null;
            try
            {
                var melons = MelonLoader.MelonBase.RegisteredMelons;
                if (melons == null)
                {
                    Core.Log?.Warning("[overlay] MelonBase.RegisteredMelons returned null, so the Sideload version cannot be checked (needs " + RequiredSideload + "+).");
                    return;
                }
                foreach (MelonLoader.MelonBase melon in melons)
                {
                    if (melon == null || !string.Equals(melon.Info?.Name, "Sideload", StringComparison.OrdinalIgnoreCase)) continue;
                    found = melon.Info.Version;
                    if (string.IsNullOrEmpty(found)) found = melon.MelonAssembly?.Assembly?.GetName()?.Version?.ToString(3);
                    break;
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[overlay] the Sideload version could not be read (needs " + RequiredSideload + "+) - " + e.Message);
                return;
            }

            if (string.IsNullOrEmpty(found))
            {
                Core.Log?.Warning("[overlay] no Sideload version could be read from the loaded melons (needs " + RequiredSideload
                                + "+). If the overlay windows cannot be moved or resized, update Sideload.");
                return;
            }

            // Leading digits and dots only, so a suffix such as "-beta" does not break the parse.
            Match m = Regex.Match(found, @"^\d+(\.\d+){0,3}");
            if (!m.Success || !Version.TryParse(m.Value.Contains(".") ? m.Value : m.Value + ".0", out Version have))
            {
                Core.Log?.Warning("[overlay] Sideload version '" + found + "' is not readable as a version (needs " + RequiredSideload + "+).");
                return;
            }

            if (have < Version.Parse(RequiredSideload))
                Core.Log?.Warning("[overlay] Sideload " + found + " is older than " + RequiredSideload
                                + " - overlay windows cannot be moved or resized and may render offset. Update Sideload.");
            else
                Core.Log?.Msg("[overlay] Sideload " + found + " ok (needs " + RequiredSideload + "+).");
        }

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
                Hide();
                _mode = mode;
                Core.Log?.Msg("[overlay] hidden (" + why + ").");
                return true;
            }

            if (!Surfaces.Available)
            {
                Core.Log?.Error("[overlay] Sideload is not available, so the overlay cannot be drawn. "
                              + "Install Sideload " + RequiredSideload + " or newer, or use 'snitch dashboard' for the web view.");
                return false;
            }

            bool wasHidden = _mode == OverlayMode.Hidden;
            if (!Surfaces.IsMounted(SurfaceId))
            {
                Core.Log?.Msg("[overlay] no page built in the background, mounting it now.");
                if (!Mount(background: false)) return false;
            }
            bool ready = wasHidden && mode == OverlayMode.Compact && _prep == Prep.Done && _pageCurrent;
            Show();
            if (wasHidden) { _openAt = Time.realtimeSinceStartup; _openFrames = 0; _openWorst = 0f; }
            _pageCurrent = false;
            _pushAtFrame = ready ? Time.frameCount + 2 : -1;

            _mode = mode;
            _moveHeld = false;
            if (_raycaster != null) _raycaster.enabled = mode == OverlayMode.Expanded;
            if (mode == OverlayMode.Expanded) OverlayInput.Free();
            else OverlayInput.Restore("compact");
            _dirty = !ready;
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

            if (_mode == OverlayMode.Hidden)
            {
                TickBackground();
                return;
            }

            TickOpenCost();
            TickMoveHold();

            // The game locked the cursor again (its own menu closed, a scene loaded): stop holding it.
            if (_mode == OverlayMode.Expanded && OverlayInput.LostToGame())
            {
                OverlayInput.Restore("game took the cursor");
                SetMode(OverlayMode.Compact, "cursor");
                return;
            }

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

            if (_pushAtFrame >= 0)
            {
                if (Time.frameCount < _pushAtFrame) return;
                _pushAtFrame = -1;
                _dirty = true;
            }

            if (!_dirty && now - _lastEmit < EmitEvery) return;
            // Not on every frame: the page rebuilds on a changed value, and the numbers only move per poll.
            if (!_dirty && now - _lastEmit < 1.0 / Math.Max(1f, Preferences.PollHz)) return;

            // A changed value lays the page out again and replaces the pressed uGUI button, which then raises no
            // click on release. While the left mouse button is held over the open overlay the push waits.
            if (_mode == OverlayMode.Expanded && (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0)))
            {
                if (!_pushHeld) Core.Log?.Msg("[overlay] state push held while the mouse button is down.");
                _pushHeld = true;
                _dirty = true;
                return;
            }
            _pushHeld = false;

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

        /// <summary>Mounts the page. In the background the canvas draws at alpha 0 and takes no clicks, so the fonts load
        /// and the first build runs while the player sees nothing; TickBackground then switches the root off.</summary>
        private static bool Mount(bool background)
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

                // The page has to be active to be built (TextMeshPro measures text only after its Awake), so the
                // background build hides it by alpha instead.
                _group = _root.AddComponent<CanvasGroup>();
                _group.alpha = background ? 0f : 1f;
                _group.blocksRaycasts = !background;
                _group.interactable = !background;

                // A fixed 1920x1080 reference, so the panel keeps its share of the screen at any resolution.
                var scaler = _root.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0f;

                // Enabled only while expanded: an enabled raycaster swallows every click inside the page rect, which
                // in compact mode lies over the game view.
                _raycaster = _root.AddComponent<GraphicRaycaster>();
                _raycaster.enabled = false;

                var holder = new GameObject("overlay");
                var rect = holder.AddComponent<RectTransform>();
                rect.SetParent(_root.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0f, 0f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                // The canvas is ReferenceWidth wide at any aspect; its height follows the screen shape.
                float refHeight = Screen.width > 0 ? ReferenceWidth * Screen.height / Screen.width : 1080f;
                _surface = Surfaces.Mount(rect, SurfaceId, Bundle, designShortSide: Mathf.Min(ReferenceWidth, refHeight),
                                          hostAssembly: typeof(Overlay).Assembly);
                RegisterCalls(_surface);

                if (!Surfaces.IsMounted(SurfaceId))
                {
                    Core.Log?.Error("[overlay] the page could not be mounted - see the Sideload log lines above.");
                    Unmount();
                    return false;
                }

                _qrUrl = "\u0001";   // forces the next Push to publish the picture again
                _prep = background ? Prep.Building : Prep.Done;
                _settleIsHide = false;
                _prepStart = Time.realtimeSinceStartup;
                _prepPushFrame = -1;
                Core.Log?.Msg("[overlay] surface '" + SurfaceId + "' mounted" + (background ? " in the background." : "."));
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
            _moveHeld = false;
            OverlayInput.Restore("unmount");
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
            _group = null;
            _pageReady = false;
            _pageCurrent = false;
            _pushAtFrame = -1;
            _prep = Prep.None;
        }

        // ----- background build, show, hide -----

        private enum Prep { None, Building, Done }

        private static CanvasGroup _group;
        private static Prep _prep = Prep.None;
        private static bool _pageReady;
        private static float _prepStart;
        private static int _prepPushFrame = -1;
        private static bool _prepFailed;
        private static bool _prepSkipLogged;
        // The settle in progress follows a hide rather than the first build (only the log line differs).
        private static bool _settleIsHide;
        // The switched-off page already shows the compact layout with recent numbers.
        private static bool _pageCurrent;
        // After such an open the next state push waits for this frame, so switching the page on and laying out
        // the new numbers do not land in the same frame.
        private static int _pushAtFrame = -1;
        private static float _openAt = -1f;
        private static int _openFrames;
        private static float _openWorst;
        private static float _openSwitch;
        // The first frames after an open, before the deferred state push (Tick pushes two frames later and Sideload
        // lays it out on its next tick).
        private const int SwitchOnFrames = 3;
        private static readonly System.Text.StringBuilder _openList = new System.Text.StringBuilder();

        // Frames between the state push and switching the page off: the push queues a rebuild, which Sideload runs on
        // its next tick.
        private const int PrepSettleFrames = 3;
        private const float PrepTimeout = 15f;
        // Frames after an open whose worst time is logged as the cost of opening.
        private const int OpenCostFrames = 10;

        /// <summary>While hidden: builds the page once in the background (fonts, first layout, the current standings),
        /// then switches its root off. An inactive root costs Sideload nothing per frame and catches no input, and the
        /// overlay key only switches it on again.</summary>
        private static void TickBackground()
        {
            if (_prep == Prep.Done || _prepFailed) return;

            if (_prep == Prep.None)
            {
                if (!Preferences.Enabled)
                {
                    if (!_prepSkipLogged) Core.Log?.Msg("[overlay] overlay disabled in the preferences, no background build.");
                    _prepSkipLogged = true;
                    return;
                }
                if (!Surfaces.Available)
                {
                    _prepFailed = true;
                    Core.Log?.Warning("[overlay] Sideload is not available, no background build; the overlay key will report it.");
                    return;
                }
                if (!Mount(background: true))
                {
                    _prepFailed = true;
                    Core.Log?.Warning("[overlay] the background build could not mount the page; the overlay key mounts it on demand.");
                }
                return;
            }

            // Building.
            if (Time.realtimeSinceStartup - _prepStart > PrepTimeout)
            {
                Core.Log?.Warning("[overlay] the background build did not report ready within " + PrepTimeout
                                + " s; switching the page off anyway, the next open rebuilds it.");
                FinishBackground();
                return;
            }
            if (!_pageReady)
            {
                return;
            }
            if (_prepPushFrame < 0)
            {
                Push();
                _prepPushFrame = Time.frameCount;
                return;
            }
            if (Time.frameCount - _prepPushFrame < PrepSettleFrames) return;
            FinishBackground();
        }

        private static void FinishBackground()
        {
            _prep = Prep.Done;
            _pageCurrent = _pageReady;
            if (_root != null) _root.SetActive(false);
            if (_group != null) { _group.alpha = 1f; _group.blocksRaycasts = true; _group.interactable = true; }
            string ms = ((Time.realtimeSinceStartup - _prepStart) * 1000f).ToString("0", Inv);
            Core.Log?.Msg(_settleIsHide
                ? "[overlay] page set to compact and switched off in " + ms + " ms."
                : "[overlay] page built in the background in " + ms + " ms, switched off until opened.");
        }

        private static void Show()
        {
            if (_prep == Prep.Building)
            {
                _prep = Prep.Done;
                Core.Log?.Msg(_settleIsHide
                    ? "[overlay] opened again before the page was switched off."
                    : "[overlay] opened before the background build finished; it completes visibly.");
            }
            if (_group != null) { _group.alpha = 1f; _group.blocksRaycasts = true; _group.interactable = true; }
            if (_root != null && !_root.activeSelf) _root.SetActive(true);
        }

        /// <summary>Hides without unmounting. The page first takes the compact state while invisible (alpha 0, no
        /// clicks), then TickBackground switches its root off, so the next open finds the page already laid out and
        /// only switches it on.</summary>
        private static void Hide()
        {
            _moveHeld = false;
            _pushAtFrame = -1;
            _pageCurrent = false;
            if (_raycaster != null) _raycaster.enabled = false;
            OverlayInput.Restore("hidden");
            _openAt = -1f;
            if (_root == null) return;

            if (_pageReady && _group != null)
            {
                _group.alpha = 0f;
                _group.blocksRaycasts = false;
                _group.interactable = false;
                _prep = Prep.Building;
                _settleIsHide = true;
                _prepStart = Time.realtimeSinceStartup;
                _prepPushFrame = -1;
                return;
            }
            Core.Log?.Msg("[overlay] the page never reported ready, switching it off without the compact reset.");
            _root.SetActive(false);
        }

        /// <summary>Logs the worst frame time of the first frames after an open, the hitch the player feels.</summary>
        private static void TickOpenCost()
        {
            if (_openAt < 0f) return;
            // The frame that ran SetMode is not part of it; the next ones carry the page's rebuild.
            if (_openFrames == 0) { _openList.Clear(); _openSwitch = 0f; }
            else
            {
                float ms = Time.unscaledDeltaTime * 1000f;
                _openWorst = Mathf.Max(_openWorst, ms);
                if (_openFrames <= SwitchOnFrames) _openSwitch = Mathf.Max(_openSwitch, ms);
                _openList.Append(_openList.Length == 0 ? "" : " ").Append(ms.ToString("0", Inv));
            }
            if (++_openFrames <= OpenCostFrames) return;
            // Switch-on: the frames before the first state push lands, i.e. showing the page that was already built.
            // The rest of the window also holds the page's regular rebuilds for new numbers.
            Core.Log?.Msg("[overlay] open cost: switch-on " + _openSwitch.ToString("0.0", Inv) + " ms, worst of the next "
                        + OpenCostFrames + " frames " + _openWorst.ToString("0.0", Inv) + " ms (" + _openList + ").");
            _openAt = -1f;
        }

        // ----- page calls -----

        private static void RegisterCalls(SurfaceHandle s)
        {
            s.OnCall("page.ready", _ => Call("page.ready", () => { _dirty = true; _qrUrl = "\u0001"; _pageReady = true; return "ok"; }));
            s.OnCall("page.log", a => Call("page.log", () => { Core.Log?.Warning("[overlay] page: " + a); return "ok"; }));
            s.OnCall("layout.get", _ => Call("layout.get", () => OverlayLayout.Value));
            s.OnCall("layout.set", a => Call("layout.set", () => OverlayLayout.Store(a) >= 0 ? "ok" : "bad"));
            s.OnCall("layout.reset", _ => Call("layout.reset", () => { OverlayLayout.Reset(); return "ok"; }));
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

        /// <summary>Clears the remembered layout and, when the page is up, tells it to lay its windows out again.</summary>
#if DEBUG
        /// <summary>
        /// Screen position of a page element, for the real-mouse check: finds the painted object named
        /// <c>tag#id</c> under the overlay canvas and returns its centre in Unity screen pixels (origin bottom left).
        /// </summary>
        internal static string Where(string id)
        {
            if (_root == null) return "overlay not mounted";
            if (_mode != OverlayMode.Expanded) return "overlay is " + _mode + ", not expanded";
            string suffix = "#" + id;
            int matches = 0;
            string first = "";
            Vector2 bestA = default, bestB = default;
            float bestArea = 0f;
            foreach (var rt in _root.GetComponentsInChildren<RectTransform>(false))
            {
                if (rt == null || !rt.gameObject.name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                matches++;
                if (first.Length == 0)
                    first = " (rect=" + rt.rect + " lossyScale=" + rt.lossyScale + " pos=" + rt.position
                        + " rootScale=" + _root.transform.lossyScale + " path=" + PathOf(rt.transform) + ")";
                // No GetWorldCorners: under IL2CPP the managed array is copied into the call and comes back unfilled.
                Rect r = rt.rect;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                Vector2 b = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
                float area = (b.x - a.x) * (b.y - a.y);
                if (area > bestArea) { bestArea = area; bestA = a; bestB = b; }
            }
            if (matches == 0) return "no element '" + suffix + "' under the overlay";
            if (bestArea <= 0f) return "element '" + suffix + "' found " + matches + " time(s) but has no size on screen" + first;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(inv, "where {0} x={1:F0} y={2:F0} w={3:F0} h={4:F0} screen={5}x{6} matches={7}",
                id, (bestA.x + bestB.x) / 2f, (bestA.y + bestB.y) / 2f, bestB.x - bestA.x, bestB.y - bestA.y,
                Screen.width, Screen.height, matches);
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (int i = 0; i < 6 && t.parent != null; i++) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }
#endif

        internal static void ResetLayout(string why)
        {
            OverlayLayout.Reset();
            if (_surface == null)
            {
                Core.Log?.Msg("[overlay] layout cleared while hidden (" + why + "), the next open uses the defaults.");
                return;
            }
            try
            {
                _surface.Emit("layout", "reset");
                Core.Log?.Msg("[overlay] page told to use the default layout (" + why + ").");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not tell the page to reset its layout: " + e.Message);
            }
        }

        /// <summary>Holding Alt in compact mode frees the cursor so the card can be dragged, and puts it back on release.</summary>
        private static void TickMoveHold()
        {
            bool held = _mode == OverlayMode.Compact && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
            if (held == _moveHeld) return;
            _moveHeld = held;
            if (_raycaster != null) _raycaster.enabled = held;
            if (held) OverlayInput.Free(); else OverlayInput.Restore("compact move released");
            Core.Log?.Msg("[overlay] compact card " + (held ? "can be moved (Alt held)." : "fixed again (Alt released)."));
        }

        private static bool _moveHeld;
        private static bool _pushHeld;

        private static string Call(string name, Func<string> body)
        {
            try
            {
                string result = body();
#if DEBUG
                // Shows that a page event reached the host; the overlay check reads it after a real mouse click.
                if (name != "page.log") Core.Log?.Msg("[overlay] call '" + name + "' answered " + result);
#endif
                return result;
            }
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
            double win = _window;
            FrameWindow f = Standings.Frame(win);
            double frameMs = f.MeanMs;
            AttributionStats at = SnitchCore.LatestAttribution;
            bool expanded = _mode == OverlayMode.Expanded;

            List<Rider> riders = Standings.Rank(win);
            // Panel ids join the known mod names; the groups of reported sections joined in Standings.Pull.
            var names = new List<string>(PanelRegistry.Count + 8);
            for (int i = 0; i < PanelRegistry.All.Count; i++) names.Add(PanelRegistry.All[i].Id);
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
            Num(sb, "scale", Standings.ScaleFor(leader));

            // Compact lists the top three named riders, expanded the top ten; the unattributed row (bib 0) is listed
            // in both and does not count toward that limit.
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
                if (!unattr) shown++;
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
