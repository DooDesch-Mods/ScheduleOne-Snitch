using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;

namespace Snitch.UI
{
    /// <summary>
    /// The in-game overlay: a small windowing layer for the "Overview", every mod's panel, and the log "Timeline".
    /// Each window is independently toggleable, movable (drag the title bar) and resizable (drag the bottom-right
    /// grip), with a scrollable body and persisted layout. Rendering uses cached-rebuild IMGUI; interaction uses
    /// polled legacy <see cref="Input"/> (proven in this IL2CPP build, where Event.current mouse input is not) -
    /// so buttons/toggles are hit-tested against the rects drawn last frame, exactly like the old single overlay's
    /// drag. <see cref="Draw"/> runs from OnGUI, <see cref="HandleInput"/> from OnUpdate.
    /// </summary>
    internal static class WindowManager
    {
        private const float TitleH = 20f;
        private const float Grip = 14f;
        private const float Pad = 6f;
        private const float Gap = 4f;
        private const float MinW = 160f;
        private const float MinH = 70f;
        private static float _btnH = 22f;   // recomputed per-frame from the font size so descenders never clip

        // styles + background textures (built once)
        private static bool _stylesReady;
        private static GUIStyle _bg, _title, _close, _grip, _text, _btn;
        private static Texture2D _bgTex, _titleTex, _whiteTex, _gripTex;

        // hover state (legacy Input, set once per Draw); hover only matters while the cursor is free
        private static Vector2 _mouse;
        private static bool _cursorFree;

        // button tint palette - one white base texture, tinted via GUI.backgroundColor per state
        private static readonly Color BtnNormal = new Color(0.30f, 0.32f, 0.38f, 0.98f);
        private static readonly Color BtnHover = new Color(0.44f, 0.47f, 0.56f, 0.98f);
        private static readonly Color BtnOn = new Color(0.36f, 0.40f, 0.80f, 0.98f);
        private static readonly Color BtnOnHover = new Color(0.47f, 0.51f, 0.92f, 0.98f);
        private static readonly Color BtnGreen = new Color(0.20f, 0.52f, 0.32f, 0.98f);
        private static readonly Color BtnGreenHover = new Color(0.28f, 0.64f, 0.42f, 0.98f);
        private static readonly Color BtnRed = new Color(0.60f, 0.27f, 0.31f, 0.98f);
        private static readonly Color BtnRedHover = new Color(0.73f, 0.35f, 0.39f, 0.98f);
        private static readonly Color TitleFront = new Color(0.97f, 0.98f, 1f);
        private static readonly Color TitleDim = new Color(0.60f, 0.62f, 0.70f);
        private static readonly Color TitleBarCol = new Color(0.12f, 0.13f, 0.20f, 0.99f);

        // window registry / z-order (back -> front)
        private static readonly List<string> _order = new List<string>();
        private static readonly Dictionary<string, float> _scroll = new Dictionary<string, float>();

        // per-frame geometry (from the last Draw; read by HandleInput)
        private static readonly Dictionary<string, Rect> _frameRects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, Rect> _titleRects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, Rect> _closeRects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, Rect> _gripRects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, Rect> _bodyRects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, float> _contentH = new Dictionary<string, float>();

        private struct Hit { public string Win; public Rect Rect; public int Type; public string Arg; } // 1=action 2=toggle 3=window-toggle
        private static readonly List<Hit> _hits = new List<Hit>(32);

        // text cache (rebuilt ~10 Hz)
        private static readonly Dictionary<string, string> _textCache = new Dictionary<string, string>();
        private static float _nextText;

        // drag state
        private enum Drag { None, Move, Resize }
        private static Drag _drag = Drag.None;
        private static string _dragId;
        private static Vector2 _grab;
        private static Rect _startRect;

        // ---------------------------------------------------------------- draw

        internal static void Draw()
        {
            EnsureStyles();
            int fs = Preferences.HudFontSize;
            _text.fontSize = _btn.fontSize = fs;
            _title.fontSize = fs;
            _btnH = Mathf.Max(22f, fs + 10f);   // room for the line box + descenders at any font size

            _cursorFree = Cursor.lockState != CursorLockMode.Locked;
            _mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            RefreshWindowList();
            MaybeRebuildText();

            _hits.Clear();
            _frameRects.Clear(); _titleRects.Clear(); _closeRects.Clear(); _gripRects.Clear(); _bodyRects.Clear();

            // the front (top-most) visible window gets a brighter title bar so focus is obvious
            string front = null;
            for (int i = _order.Count - 1; i >= 0; i--)
                if (WindowLayout.IsVisible(_order[i])) { front = _order[i]; break; }

            for (int i = 0; i < _order.Count; i++)
            {
                string id = _order[i];
                if (WindowLayout.IsVisible(id)) DrawWindow(id, id == front);
            }
        }

        private static void RefreshWindowList()
        {
            WindowLayout.Get("overview", 8f, 8f, 360f, 320f, true);
            EnsureInOrder("overview");

            IReadOnlyList<PanelModel> panels = PanelRegistry.All;
            for (int i = 0; i < panels.Count; i++)
            {
                WindowLayout.Get(panels[i].Id, 380f + (i % 3) * 28f, 40f + i * 34f, 320f, 240f, false);
                EnsureInOrder(panels[i].Id);
            }

            WindowLayout.Get("timeline", 8f, 360f, 560f, 220f, false);
            EnsureInOrder("timeline");
        }

        private static void EnsureInOrder(string id) { if (!_order.Contains(id)) _order.Add(id); }

        private static void DrawWindow(string id, bool isFront)
        {
            WinState s = WindowLayout.Get(id, 8f, 8f, 320f, 240f, false);
            float w = Mathf.Clamp(s.W, MinW, Screen.width);
            float h = Mathf.Clamp(s.H, MinH, Screen.height);
            float x = Mathf.Clamp(s.X, 0f, Mathf.Max(0f, Screen.width - w));
            float y = Mathf.Clamp(s.Y, 0f, Mathf.Max(0f, Screen.height - h));
            var rect = new Rect(x, y, w, h);
            _frameRects[id] = rect;

            GUI.Box(rect, GUIContent.none, _bg);

            var titleRect = new Rect(x, y, w, TitleH);
            GUI.contentColor = isFront ? TitleFront : TitleDim;   // brighter title = focused window
            GUI.Box(titleRect, "  " + TitleOf(id), _title);
            GUI.contentColor = Color.white;
            _titleRects[id] = titleRect;

            var closeRect = new Rect(rect.xMax - TitleH, y, TitleH, TitleH);
            bool closeHover = _cursorFree && closeRect.Contains(_mouse);
            GUI.backgroundColor = closeHover ? BtnRedHover : TitleBarCol;
            GUI.Box(closeRect, "x", _close);
            GUI.backgroundColor = Color.white;
            _closeRects[id] = closeRect;

            var bodyRect = new Rect(x + 2f, y + TitleH, w - 4f, h - TitleH - 2f);
            _bodyRects[id] = bodyRect;

            float scroll = GetScroll(id);
            float innerW = bodyRect.width - 2f * Pad - 2f;

            GUI.BeginGroup(bodyRect);
            float localY = Pad;
            DrawBody(id, innerW, scroll, bodyRect, ref localY);
            GUI.EndGroup();

            float contentH = localY + Pad - Gap;
            _contentH[id] = contentH;
            // clamp scroll if content shrank
            float max = Mathf.Max(0f, contentH - bodyRect.height);
            if (scroll > max) _scroll[id] = max;

            var gripRect = new Rect(rect.xMax - Grip, rect.yMax - Grip, Grip, Grip);
            GUI.Box(gripRect, GUIContent.none, _grip);
            _gripRects[id] = gripRect;
        }

        private static void DrawBody(string id, float innerW, float scroll, Rect bodyRect, ref float localY)
        {
            string kind = KindOf(id);
            if (kind == "overview")
            {
                // Sampling control right at the top so you can arm/disarm Snitch from the overlay (no console needed).
                if (SnitchCore.Active)
                {
                    Button(id, innerW, scroll, bodyRect, ref localY, "Stop sampling", 4, "stop", false, 2);
                    Button(id, innerW, scroll, bodyRect, ref localY, "Reset", 4, "reset", false);
                }
                else
                {
                    Button(id, innerW, scroll, bodyRect, ref localY, "Start sampling", 4, "start", false, 1);
                }
                Label(innerW, scroll, bodyRect, ref localY, Text("overview"));
                Label(innerW, scroll, bodyRect, ref localY, "<b>windows</b>");
                IReadOnlyList<PanelModel> panels = PanelRegistry.All;
                for (int i = 0; i < panels.Count; i++)
                    Button(id, innerW, scroll, bodyRect, ref localY, panels[i].Title, 3, panels[i].Id, WindowLayout.IsVisible(panels[i].Id));
                Button(id, innerW, scroll, bodyRect, ref localY, "Timeline (logs)", 3, "timeline", WindowLayout.IsVisible("timeline"));
            }
            else if (kind == "timeline")
            {
                Label(innerW, scroll, bodyRect, ref localY, Text("timeline"));
            }
            else
            {
                PanelModel p = PanelRegistry.Get(id);
                if (p == null) { Label(innerW, scroll, bodyRect, ref localY, "(panel gone)"); return; }
                Label(innerW, scroll, bodyRect, ref localY, Text("panel:" + id));
                for (int i = 0; i < p.Actions.Count; i++)
                    Button(id, innerW, scroll, bodyRect, ref localY, p.Actions[i].Label, 1, p.Actions[i].Id, false);
                for (int i = 0; i < p.Toggles.Count; i++)
                {
                    bool on = PanelRegistry.GetToggle(p.Toggles[i].Id);
                    Button(id, innerW, scroll, bodyRect, ref localY, (on ? "[x] " : "[ ] ") + p.Toggles[i].Label, 2, p.Toggles[i].Id, on);
                }
                if (p.HasLog) Label(innerW, scroll, bodyRect, ref localY, Text("log:" + id));
            }
        }

        private static void Label(float innerW, float scroll, Rect bodyRect, ref float localY, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var gc = new GUIContent(text);
            float eh = _text.CalcHeight(gc, innerW);
            float drawY = localY - scroll;
            if (drawY + eh > 0f && drawY < bodyRect.height) GUI.Label(new Rect(Pad, drawY, innerW, eh), gc, _text);
            localY += eh + Gap;
        }

        // accent: 0 = neutral, 1 = start (green), 2 = stop (red). on = toggle active (violet).
        private static void Button(string id, float innerW, float scroll, Rect bodyRect, ref float localY, string label, int type, string arg, bool on, int accent = 0)
        {
            float bw = Mathf.Min(innerW, _btn.CalcSize(new GUIContent(label)).x + 16f);
            float drawY = localY - scroll;
            if (drawY + _btnH > 0f && drawY < bodyRect.height)
            {
                var screen = new Rect(bodyRect.x + Pad, bodyRect.y + drawY, bw, _btnH);
                bool hover = _cursorFree && screen.Contains(_mouse);
                GUI.backgroundColor = ButtonColor(on, accent, hover);
                GUI.Box(new Rect(Pad, drawY, bw, _btnH), label, _btn);
                GUI.backgroundColor = Color.white;
                _hits.Add(new Hit { Win = id, Rect = screen, Type = type, Arg = arg });
            }
            localY += _btnH + Gap;
        }

        private static Color ButtonColor(bool on, int accent, bool hover)
        {
            if (on) return hover ? BtnOnHover : BtnOn;
            if (accent == 1) return hover ? BtnGreenHover : BtnGreen;
            if (accent == 2) return hover ? BtnRedHover : BtnRed;
            return hover ? BtnHover : BtnNormal;
        }

        // ---------------------------------------------------------------- input (polled legacy Input, from OnUpdate)

        internal static void HandleInput()
        {
            if (Cursor.lockState == CursorLockMode.Locked) { _drag = Drag.None; return; }

            var m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                string wid = TopWindowAt(m, true);
                if (wid != null)
                {
                    float max = Mathf.Max(0f, GetContentH(wid) - GetBodyH(wid));
                    _scroll[wid] = Mathf.Clamp(GetScroll(wid) - wheel * 28f, 0f, max);
                }
            }

            if (Input.GetMouseButtonDown(0))
            {
                string wid = TopWindowAt(m, false);
                if (wid == null) return;
                BringToFront(wid);

                if (_closeRects.TryGetValue(wid, out Rect cr) && cr.Contains(m)) { WindowLayout.SetVisible(wid, false); return; }
                if (_gripRects.TryGetValue(wid, out Rect gr) && gr.Contains(m)) { _drag = Drag.Resize; _dragId = wid; _grab = m; _startRect = _frameRects[wid]; return; }
                if (_titleRects.TryGetValue(wid, out Rect tr) && tr.Contains(m))
                {
                    _drag = Drag.Move; _dragId = wid;
                    Rect r = _frameRects[wid];
                    _grab = m - new Vector2(r.x, r.y);
                    return;
                }
                for (int i = 0; i < _hits.Count; i++)
                {
                    Hit hit = _hits[i];
                    if (hit.Win != wid || !hit.Rect.Contains(m)) continue;
                    switch (hit.Type)
                    {
                        case 1: PanelRegistry.Invoke(hit.Arg); break;
                        case 2: PanelRegistry.SetToggle(hit.Arg, !PanelRegistry.GetToggle(hit.Arg)); break;
                        case 3: WindowLayout.Toggle(hit.Arg); break;
                        case 4:
                            if (hit.Arg == "start") SnitchCore.Start();
                            else if (hit.Arg == "stop") SnitchCore.Stop();
                            else { SnitchCore.Stop(); SnitchCore.Start(); }
                            break;
                    }
                    return;
                }
            }
            else if (Input.GetMouseButton(0) && _drag != Drag.None)
            {
                if (_drag == Drag.Move) WindowLayout.SetPos(_dragId, m.x - _grab.x, m.y - _grab.y);
                else WindowLayout.SetSize(_dragId, Mathf.Max(MinW, _startRect.width + (m.x - _grab.x)), Mathf.Max(MinH, _startRect.height + (m.y - _grab.y)));
            }
            else if (Input.GetMouseButtonUp(0) && _drag != Drag.None)
            {
                _drag = Drag.None;
                WindowLayout.Save();
            }
        }

        private static string TopWindowAt(Vector2 m, bool bodyOnly)
        {
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                string id = _order[i];
                if (!WindowLayout.IsVisible(id)) continue;
                var dict = bodyOnly ? _bodyRects : _frameRects;
                if (dict.TryGetValue(id, out Rect r) && r.Contains(m)) return id;
            }
            return null;
        }

        private static void BringToFront(string id)
        {
            if (_order.Remove(id)) _order.Add(id);
        }

        // ---------------------------------------------------------------- text content (cached)

        private static void MaybeRebuildText()
        {
            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.1f;

            _textCache["overview"] = ProfilerHud.BuildOverview();
            IReadOnlyList<PanelModel> panels = PanelRegistry.All;
            for (int i = 0; i < panels.Count; i++)
            {
                PanelModel p = panels[i];
                _textCache["panel:" + p.Id] = BuildPanelText(p);
                if (p.HasLog) _textCache["log:" + p.Id] = BuildLogText(p.Id, 16);
            }
            _textCache["timeline"] = BuildTimelineText(220);
        }

        private static string Text(string key) => _textCache.TryGetValue(key, out string v) ? v : "";

        private static string BuildPanelText(PanelModel p)
        {
            var sb = new StringBuilder(256);
            for (int i = 0; i < p.Texts.Count; i++)
            {
                string s = null;
                try { s = p.Texts[i]?.Invoke(); } catch { }
                if (!string.IsNullOrEmpty(s)) Line(sb, s);
            }

            string prefix = p.Id + ".";
            var cs = SnitchCore.LatestCounters;
            if (cs != null)
                for (int i = 0; i < cs.Count; i++)
                {
                    CounterRow c = cs[i];
                    if (c.Id != p.Id && !c.Id.StartsWith(prefix)) continue;
                    string name = c.Id.StartsWith(prefix) ? c.Id.Substring(prefix.Length) : c.Id;
                    Line(sb, $"{name} = {c.Value:0.##} {c.Unit}".TrimEnd());
                }

            var st = SnitchCore.LatestStates;
            if (st != null)
                for (int i = 0; i < st.Count; i++)
                {
                    Snitch.Registries.StateSnapshot ss = st[i];
                    if (ss.Id != p.Id && !ss.Id.StartsWith(prefix)) continue;
                    var line = new StringBuilder();
                    line.Append(ss.Title).Append(' ').Append(ss.EffectiveTotal()).Append(": ");
                    for (int k = 0; k < ss.Buckets.Count; k++)
                    {
                        if (k > 0) line.Append(' ');
                        line.Append(ss.Buckets[k].Name).Append('=').Append(ss.Buckets[k].Count);
                    }
                    Line(sb, line.ToString());
                }

            return sb.ToString();
        }

        private static string BuildLogText(string channel, int n)
        {
            List<LogEntry> es = LogHub.Channel(channel, n);
            if (es.Count == 0) return "";
            var sb = new StringBuilder(256);
            Line(sb, "<b>log</b>");
            for (int i = 0; i < es.Count; i++) AppendLogLine(sb, es[i], false);
            return sb.ToString();
        }

        private static string BuildTimelineText(int n)
        {
            List<LogEntry> es = LogHub.Timeline(n);
            if (es.Count == 0) return "(no log output yet)";
            var sb = new StringBuilder(1024);
            for (int i = 0; i < es.Count; i++) AppendLogLine(sb, es[i], true);
            return sb.ToString();
        }

        private static void AppendLogLine(StringBuilder sb, LogEntry e, bool withChannel)
        {
            if (sb.Length > 0) sb.Append('\n');
            string col = e.Lvl == 2 ? "#f55" : (e.Lvl == 1 ? "#fd5" : "#cdd6f4");
            sb.Append("<color=").Append(col).Append('>').Append(e.Time).Append(' ');
            if (withChannel) sb.Append('[').Append(e.Ch).Append("] ");
            sb.Append(e.Msg).Append("</color>");
        }

        private static void Line(StringBuilder sb, string s) { if (sb.Length > 0) sb.Append('\n'); sb.Append(s); }

        // ---------------------------------------------------------------- helpers

        private static string KindOf(string id) => id == "overview" ? "overview" : (id == "timeline" ? "timeline" : "panel");

        private static string TitleOf(string id)
        {
            if (id == "overview") return "Snitch - Overview";
            if (id == "timeline") return "Timeline (all logs)";
            PanelModel p = PanelRegistry.Get(id);
            return p != null ? p.Title : id;
        }

        private static float GetScroll(string id) => _scroll.TryGetValue(id, out float v) ? v : 0f;
        private static float GetContentH(string id) => _contentH.TryGetValue(id, out float v) ? v : 0f;
        private static float GetBodyH(string id) => _bodyRects.TryGetValue(id, out Rect r) ? r.height : 0f;

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _bgTex = Solid(new Color(0.055f, 0.065f, 0.085f, 0.93f));
            _titleTex = Solid(new Color(0.12f, 0.13f, 0.20f, 0.99f));
            _whiteTex = Solid(Color.white);   // tinted per-use via GUI.backgroundColor
            _gripTex = Solid(new Color(0.45f, 0.48f, 0.85f, 0.9f));

            _bg = new GUIStyle { padding = new RectOffset(0, 0, 0, 0) };
            _bg.normal.background = _bgTex;

            _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, richText = true };
            _title.normal.background = _titleTex;
            _title.normal.textColor = new Color(0.90f, 0.92f, 1f);

            // white base so the close button can tint to the title bar normally and to red on hover
            _close = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _close.normal.background = _whiteTex;
            _close.normal.textColor = new Color(1f, 0.78f, 0.78f);

            _grip = new GUIStyle();
            _grip.normal.background = _gripTex;

            _text = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, alignment = TextAnchor.UpperLeft };
            _text.normal.textColor = new Color(0.84f, 0.87f, 0.93f);

            // Built from scratch (NOT GUI.skin.box, whose Clip clipping + border crop descenders like g/p/y).
            // White background, tinted per-button via GUI.backgroundColor (normal/hover/on/start/stop).
            _btn = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                richText = true,
                clipping = TextClipping.Overflow,
                padding = new RectOffset(6, 6, 2, 2),
                wordWrap = false,
            };
            _btn.normal.background = _whiteTex;
            _btn.normal.textColor = Color.white;
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
