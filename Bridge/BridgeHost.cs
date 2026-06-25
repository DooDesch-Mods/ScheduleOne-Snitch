using System;
using System.Collections.Generic;
using Snitch.Ablation;
using Snitch.Engine;
using Snitch.Logging;
using Snitch.Panels;
using Snitch.Registries;
using Snitch.Sections;

namespace Snitch.Bridge
{
    /// <summary>
    /// Installs the host implementations into <see cref="SnitchBridge"/> so the modder shim lights up. All
    /// section work is gated on <see cref="SnitchCore.Active"/> (nothing accumulates until 'snitch start').
    /// Modder state snapshots arrive as primitive arrays and are rebuilt into a per-id reused StateSnapshot.
    /// </summary>
    internal static class BridgeHost
    {
        private static readonly List<string> _recentMarks = new List<string>(32);

        internal static void Install()
        {
            SnitchBridge.IsEnabled = () => SnitchCore.Active;

            SnitchBridge.BeginScope = label =>
            {
                if (!SnitchCore.Active) return 0;
                int id = SectionProfiler.GetId(label);
                SectionProfiler.Begin(id);
                return id + 1;                       // +1 so 0 reliably means "dropped"
            };
            SnitchBridge.EndScope = token => { if (token > 0) SectionProfiler.End(token - 1); };

            SnitchBridge.BeginLabel = label => { if (SnitchCore.Active) SectionProfiler.Begin(label); };
            SnitchBridge.EndLabel = label => { if (SnitchCore.Active) SectionProfiler.End(label); };

            SnitchBridge.RegisterCounter = (id, read, unit) => CounterRegistry.RegisterDelegate(id, read, unit);
            SnitchBridge.UnregisterCounter = id => CounterRegistry.Unregister(id);

            SnitchBridge.RegisterStateProvider = (id, poll) =>
            {
                var cached = new StateSnapshot();    // one reused instance per modder provider
                StateRegistry.RegisterDelegate(id, () =>
                {
                    cached.Clear();
                    object[] a = poll();
                    if (a != null && a.Length >= 4)
                    {
                        cached.Title = a[0] as string ?? id;
                        cached.Total = a[3] is int total ? total : 0;
                        var names = a[1] as string[];
                        var counts = a[2] as int[];
                        if (names != null && counts != null)
                        {
                            int n = Math.Min(names.Length, counts.Length);
                            for (int i = 0; i < n; i++) cached.Add(names[i], counts[i]);
                        }
                    }
                    return cached;
                });
            };
            SnitchBridge.UnregisterStateProvider = id => StateRegistry.Unregister(id);

            SnitchBridge.Mark = label =>
            {
                if (string.IsNullOrEmpty(label)) return;
                _recentMarks.Add(label);
                if (_recentMarks.Count > 32) _recentMarks.RemoveAt(0);
            };

            SnitchBridge.RegisterAblationLever = (name, apply, restore) => LeverRegistry.RegisterDelegate(name, apply, restore);

            // Panels/logs are available regardless of sampling state: a mod's panel + log channel exist as soon as
            // it registers them, so the overlay/dashboard can show a mod's controls and output even before 'snitch start'.
            SnitchBridge.RegisterPanel = (id, title) => PanelRegistry.RegisterPanel(id, title);
            SnitchBridge.RegisterAction = (panelId, actionId, label, run) => PanelRegistry.RegisterAction(panelId, actionId, label, run);
            SnitchBridge.RegisterToggle = (panelId, toggleId, label, get, set) => PanelRegistry.RegisterToggle(panelId, toggleId, label, get, set);
            SnitchBridge.RegisterText = (panelId, provider) => PanelRegistry.RegisterText(panelId, provider);
            SnitchBridge.BindPanelLog = panelId => PanelRegistry.BindPanelLog(panelId);
            SnitchBridge.Log = (channel, level, message) => LogHub.Write(channel, level, message);
        }
    }
}
