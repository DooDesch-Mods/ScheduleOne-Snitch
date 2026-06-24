using System.Text;
using MelonLoader;
using UnityEngine;
using Snitch.Config;
using Snitch.Engine;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.UI
{
    /// <summary>
    /// On-screen profiler overlay. Cached-rebuild IMGUI (the proven Siesta/Litterally pattern): the text is
    /// rebuilt ~10 Hz and drawn from cache every frame, so it adds no per-frame allocation in steady state.
    /// Shows the load-bearing frame stats, the top section costs, and the state distributions. Only drawn while
    /// sampling is armed and the HUD pref is on. This is the in-game cousin of the SnitchWeb dashboard.
    /// Position and font size come from preferences and can be changed live (sliders, 'snitch hud ...' console,
    /// or dragging the overlay - see <see cref="HandleInput"/>).
    /// </summary>
    internal static class ProfilerHud
    {
        private static GUIStyle _box;
        private static string _cached = "";
        private static float _nextRebuild;

        // The rect drawn last frame (and its bottom-right resize grip). HandleInput - called from OnUpdate - hit-tests
        // against these; one frame of lag is irrelevant for dragging.
        internal static Rect LastBox;
        internal static Rect LastGrip;
        private const float GripPx = 14f;

        // Mouse-drag state (Input-driven, polled in HandleInput).
        private enum DragMode { None, Move, Font }
        private static DragMode _drag = DragMode.None;
        private static Vector2 _grabOffset;
        private static float _fontStartY;
        private static int _fontStart;

        internal static void Draw()
        {
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    richText = true,
                    padding = new RectOffset(8, 8, 6, 6),
                };
                _box.normal.textColor = Color.white;
            }

            _box.fontSize = Preferences.HudFontSize;   // live, no rebuild needed

            if (Time.unscaledTime >= _nextRebuild)
            {
                _cached = Build();
                _nextRebuild = Time.unscaledTime + 0.1f;
            }

            Vector2 size = _box.CalcSize(new GUIContent(_cached));
            float w = Mathf.Max(280f, size.x + 6f);
            float h = size.y + 6f;

            // Clamp against the live screen so the overlay never lands unreachable off-screen (resolution change,
            // stale/hand-edited pref, or the font growing). The stored pref keeps the user's chosen value.
            float x = Mathf.Clamp(Preferences.HudX, 0f, Mathf.Max(0f, Screen.width - w));
            float y = Mathf.Clamp(Preferences.HudY, 0f, Mathf.Max(0f, Screen.height - h));

            LastBox = new Rect(x, y, w, h);
            GUI.Box(LastBox, _cached, _box);

            // Small bottom-right grip as a visible resize hint (drag it to change the font size).
            LastGrip = new Rect(LastBox.xMax - GripPx, LastBox.yMax - GripPx, GripPx, GripPx);
            GUI.Box(LastGrip, GUIContent.none);
        }

        /// <summary>
        /// Mouse-drag the overlay. Polled from OnUpdate via legacy Input (proven in this IL2CPP build; Event.current
        /// mouse input is not). Drag the body to move, drag the bottom-right grip to change the font size. Only active
        /// while the cursor is free (a phone/pause menu is open) - during normal play the cursor is locked, so this
        /// no-ops and the sliders / console verbs are the way in.
        /// </summary>
        internal static void HandleInput()
        {
            // Free-cursor gate: a locked cursor can't be positioned, so any drag would be meaningless. Drop it.
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                _drag = DragMode.None;
                return;
            }

            // IMGUI rects use a top-left origin; Input.mousePosition is bottom-left -> flip Y for the hit-test.
            Vector2 m = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            if (Input.GetMouseButtonDown(0))
            {
                if (LastGrip.Contains(m))
                {
                    _drag = DragMode.Font;
                    _fontStartY = m.y;
                    _fontStart = Preferences.HudFontSize;
                }
                else if (LastBox.Contains(m))
                {
                    _drag = DragMode.Move;
                    _grabOffset = m - new Vector2(LastBox.x, LastBox.y);
                }
            }
            else if (Input.GetMouseButton(0) && _drag != DragMode.None)
            {
                if (_drag == DragMode.Move)
                {
                    Preferences.SetHudPos(m.x - _grabOffset.x, m.y - _grabOffset.y);
                }
                else // Font: dragging down (~3px per step) grows the text/window.
                {
                    Preferences.SetHudFontSize(_fontStart + Mathf.RoundToInt((m.y - _fontStartY) / 3f));
                }
            }
            else if (Input.GetMouseButtonUp(0) && _drag != DragMode.None)
            {
                _drag = DragMode.None;
                MelonPreferences.Save();   // persist the final position/size once, on release
            }
        }

        private static string Build()
        {
            FrameStats f = SnitchCore.LatestFrame;
            var sb = new StringBuilder(512);
            string col = f.MeanFps >= 50 ? "#5f5" : (f.MeanFps >= 30 ? "#fd5" : "#f55");
            sb.Append("<b>Snitch</b>   <color=").Append(col).Append('>').Append(f.MeanFps.ToString("F0"))
              .Append(" fps</color>  (min ").Append(f.MinFps.ToString("F0")).Append(")\n");
            sb.Append(f.MeanMs.ToString("F2")).Append(" ms   p95 ").Append(f.P95Ms.ToString("F2"))
              .Append("   gc0/1k ").Append(f.Gc0Per1000.ToString("F1")).Append('\n');

            var rows = SnitchCore.LatestSections;
            if (rows != null && rows.Count > 0)
            {
                sb.Append("<b>sections</b>\n");
                int n = Mathf.Min(6, rows.Count);
                for (int i = 0; i < n; i++)
                {
                    SectionRow r = rows[i];
                    sb.Append(r.Label).Append("   ").Append(r.MsPerFrame.ToString("F2")).Append(" ms  ")
                      .Append(r.PctFrame.ToString("F0")).Append("%\n");
                }
            }

            var states = SnitchCore.LatestStates;
            if (states != null && states.Count > 0)
            {
                sb.Append("<b>states</b>\n");
                foreach (StateSnapshot b in states)
                {
                    sb.Append(b.Title).Append(' ').Append(b.EffectiveTotal()).Append(": ");
                    for (int i = 0; i < b.Buckets.Count; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(b.Buckets[i].Name).Append('=').Append(b.Buckets[i].Count);
                    }
                    sb.Append('\n');
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
