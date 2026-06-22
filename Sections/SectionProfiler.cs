using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Snitch.Sections
{
    /// <summary>One row of the section report (per label, aggregated over the window).</summary>
    internal struct SectionRow
    {
        public string Group;       // text before the first '.', e.g. "MyMod" from "MyMod.Foo"
        public string Label;       // full label
        public double MsPerFrame;  // mean ms/frame over the window
        public double MaxMs;       // worst-frame (max) ms over the window
        public double Calls;       // mean calls/frame
        public double PctFrame;    // MsPerFrame / frame mean ms * 100
    }

    /// <summary>
    /// Stopwatch-accumulator section timer. Main-thread-only, allocation-free on the hot path. Backs both the
    /// public modder API (Snitch.Sample("MyMod.X") via the Bridge) and the vanilla Harmony probes - one
    /// code path. Per label: accumulate elapsed Stopwatch ticks + call count within a frame, then Flush() at the
    /// end of the frame pushes the per-frame totals into rolling windows. Recursion-safe via a depth guard so a
    /// self-recursive label counts its outermost span once. Stopwatch is high-resolution here (~18.7ns/call).
    /// </summary>
    internal static class SectionProfiler
    {
        private sealed class Accumulator
        {
            public readonly string Label;
            public readonly string Group;
            public long TicksThisFrame;
            public int CallsThisFrame;
            public int Depth;            // nesting/recursion guard (>0 = already inside)
            public long StartTs;         // outermost start timestamp
            public readonly double[] MsRing = new double[Window];
            public readonly int[] CallRing = new int[Window];
            public int Head, Count;

            public Accumulator(string label)
            {
                Label = label;
                int dot = label.IndexOf('.');
                Group = dot > 0 ? label.Substring(0, dot) : "(ungrouped)";
            }
        }

        private const int Window = 120;
        private static readonly Dictionary<string, int> _idByLabel = new Dictionary<string, int>(64);
        private static readonly List<Accumulator> _all = new List<Accumulator>(64);

        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>Intern a label and get its fast int handle (creates the accumulator on first use).</summary>
        internal static int GetId(string label)
        {
            if (string.IsNullOrEmpty(label)) label = "(unnamed)";
            if (_idByLabel.TryGetValue(label, out int id)) return id;
            id = _all.Count;
            _all.Add(new Accumulator(label));
            _idByLabel[label] = id;
            return id;
        }

        // ----- hot path (alloc-free) -----

        internal static void Begin(int id)
        {
            if ((uint)id >= (uint)_all.Count) return;
            Accumulator a = _all[id];
            if (a.Depth++ == 0) a.StartTs = Stopwatch.GetTimestamp();   // only the outermost entry starts the clock
        }

        internal static void End(int id)
        {
            if ((uint)id >= (uint)_all.Count) return;
            Accumulator a = _all[id];
            if (--a.Depth == 0)
            {
                a.TicksThisFrame += Stopwatch.GetTimestamp() - a.StartTs;
                a.CallsThisFrame++;
            }
            else if (a.Depth < 0) a.Depth = 0;   // defensive: unbalanced End
        }

        internal static void Begin(string label) => Begin(GetId(label));
        internal static void End(string label) => End(GetId(label));

        internal static Scope Sample(string label) { int id = GetId(label); Begin(id); return new Scope(id); }

        // ----- frame boundary -----

        /// <summary>Push each label's per-frame totals into its rolling window, then reset. Self-healing: a leaked
        /// Depth (e.g. an original that threw past a non-finalizer patch) is reset every frame.</summary>
        internal static void Flush()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                a.MsRing[a.Head] = a.TicksThisFrame * TickToMs;
                a.CallRing[a.Head] = a.CallsThisFrame;
                a.Head = (a.Head + 1) % Window;
                if (a.Count < Window) a.Count++;
                a.TicksThisFrame = 0;
                a.CallsThisFrame = 0;
                a.Depth = 0;
            }
        }

        internal static void Reset()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                a.Head = 0; a.Count = 0; a.TicksThisFrame = 0; a.CallsThisFrame = 0; a.Depth = 0;
            }
        }

        // ----- reporting -----

        internal static List<SectionRow> Report(double frameMeanMs)
        {
            var rows = new List<SectionRow>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                if (a.Count == 0) continue;
                double ms = 0; double maxMs = 0; long calls = 0;
                for (int k = 0; k < a.Count; k++)
                {
                    ms += a.MsRing[k];
                    if (a.MsRing[k] > maxMs) maxMs = a.MsRing[k];
                    calls += a.CallRing[k];
                }
                double meanMs = ms / a.Count;
                if (meanMs <= 0 && calls == 0) continue;   // never touched in-window -> skip
                rows.Add(new SectionRow
                {
                    Group = a.Group,
                    Label = a.Label,
                    MsPerFrame = meanMs,
                    MaxMs = maxMs,
                    Calls = (double)calls / a.Count,
                    PctFrame = frameMeanMs > 0 ? meanMs / frameMeanMs * 100.0 : 0.0,
                });
            }
            rows.Sort((x, y) => y.MsPerFrame.CompareTo(x.MsPerFrame));
            return rows;
        }

        internal static int LabelCount => _all.Count;
    }

    /// <summary>Zero-heap-alloc timing scope: <c>using (SectionProfiler.Sample("X")) { ... }</c>.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly int _id;
        internal Scope(int id) { _id = id; }
        public void Dispose() => SectionProfiler.End(_id);
    }
}
