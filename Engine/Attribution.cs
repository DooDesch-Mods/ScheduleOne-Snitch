using System;
using System.Collections.Generic;
using Snitch.Sections;

namespace Snitch.Engine
{
    /// <summary>What the profiler measured against what the frame actually cost, over the rolling window.</summary>
    internal struct AttributionStats
    {
        public int Samples;
        public double SpikeFactor;        // a frame counts as bad above SpikeFactor x the window median

        public double FrameMeanMs;
        public double FrameMedianMs;
        public double AttributedMeanMs;   // wall time inside any section, nesting counted once
        public double UnattributedMeanMs; // FrameMean - AttributedMean, floored at 0
        public double UnattributedPct;    // of the mean frame
        public double MaxUnattributedMs;  // worst single frame

        public int SpikeFrames;               // frames above the spike threshold, in the window
        public double SpikeMeanExcessMs;      // mean (frame - median) on those frames
        public double SpikeMeanUnexplainedMs; // the part of that excess no section accounts for
        public double SpikeUnexplainedPct;    // SpikeMeanUnexplained / SpikeMeanExcess * 100
        public double WorstUnexplainedMs;     // worst single spike frame

        /// <summary>True when the bad frames are bad for reasons no section explains - the signal that expensive
        /// code is running somewhere the profiler does not wrap.</summary>
        public bool PointsAtHiddenWork => SpikeFrames > 0 && SpikeUnexplainedPct >= 50.0 && SpikeMeanUnexplainedMs >= 1.0;
    }

    /// <summary>
    /// The honesty ledger: per frame it holds what the frame cost next to what every open section accounted for,
    /// and reports the difference.
    ///
    /// A profiler that lists only what it happens to wrap implies the rest is fine. It is not: a mod's most
    /// expensive per-frame work does not have to live in a lifecycle method. It can sit in a Harmony patch on a
    /// vanilla method the engine calls every frame, in a coroutine, in an event handler, or in a Unity message on
    /// a MonoBehaviour the mod added - none of which any section covers, so the mod reads as cheap.
    ///
    /// The mean unattributed number alone is not that signal, because most of a frame is the game itself and always
    /// will be. The signal is the SPIKE analysis: when a frame is far worse than the window median and the sections
    /// did not grow with it, the extra milliseconds are provably outside everything the profiler can see.
    ///
    /// Pairing: SectionProfiler accumulates from one Snitch tick to the next, and Time.unscaledDeltaTime read in
    /// that same tick is the wall time of the same interval, so the two rings line up index for index.
    /// </summary>
    internal static class Attribution
    {
        /// <summary>The label of the synthetic section row. Parenthesised so it cannot collide with a real
        /// "&lt;Mod&gt;.&lt;Method&gt;" label, and so it sorts as its own group.</summary>
        internal const string RowLabel = "(unattributed)";

        private const int Window = 120;
        private static readonly double[] _frameMs = new double[Window];
        private static readonly double[] _attributedMs = new double[Window];
        private static int _head, _count;

        internal static void Reset()
        {
            _head = 0;
            _count = 0;
            Array.Clear(_frameMs, 0, Window);
            Array.Clear(_attributedMs, 0, Window);
        }

        /// <summary>Record one frame: its wall time and the root-level section total measured inside it.</summary>
        internal static void Push(double frameMs, double attributedMs)
        {
            _frameMs[_head] = frameMs;
            _attributedMs[_head] = attributedMs;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
        }

        internal static AttributionStats Snapshot(double spikeFactor)
        {
            var s = new AttributionStats { SpikeFactor = spikeFactor, Samples = _count };
            if (_count == 0) return s;

            double[] frameSorted = new double[_count];
            double[] attrSorted = new double[_count];
            double frameSum = 0.0, attrSum = 0.0, maxUnattributed = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double f = _frameMs[i], a = _attributedMs[i];
                frameSorted[i] = f;
                attrSorted[i] = a;
                frameSum += f;
                attrSum += a;
                double u = f - a;
                if (u > maxUnattributed) maxUnattributed = u;
            }
            Array.Sort(frameSorted);
            Array.Sort(attrSorted);

            s.FrameMeanMs = frameSum / _count;
            s.FrameMedianMs = Median(frameSorted);
            s.AttributedMeanMs = attrSum / _count;
            s.UnattributedMeanMs = Math.Max(0.0, s.FrameMeanMs - s.AttributedMeanMs);
            s.UnattributedPct = s.FrameMeanMs > 0.0 ? s.UnattributedMeanMs / s.FrameMeanMs * 100.0 : 0.0;
            s.MaxUnattributedMs = Math.Max(0.0, maxUnattributed);

            // A bad frame's excess over the median, split into the part the sections grew by and the part they did
            // not. Both halves are measured against the SAME window medians, so a section that is simply expensive
            // on every frame does not count as an explanation for a spike - only a section that got worse does.
            double attrMedian = Median(attrSorted);
            double threshold = s.FrameMedianMs * (spikeFactor > 1.0 ? spikeFactor : 1.0);
            double excessSum = 0.0, unexplainedSum = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double f = _frameMs[i];
                if (f <= threshold) continue;
                double excess = f - s.FrameMedianMs;
                double explained = _attributedMs[i] - attrMedian;
                if (explained < 0.0) explained = 0.0;
                if (explained > excess) explained = excess;
                double unexplained = excess - explained;
                s.SpikeFrames++;
                excessSum += excess;
                unexplainedSum += unexplained;
                if (unexplained > s.WorstUnexplainedMs) s.WorstUnexplainedMs = unexplained;
            }
            if (s.SpikeFrames > 0)
            {
                s.SpikeMeanExcessMs = excessSum / s.SpikeFrames;
                s.SpikeMeanUnexplainedMs = unexplainedSum / s.SpikeFrames;
                s.SpikeUnexplainedPct = excessSum > 0.0 ? unexplainedSum / excessSum * 100.0 : 0.0;
            }
            return s;
        }

        /// <summary>
        /// Add the unattributed time to the section list as its own row, so it shows up in every surface at once -
        /// the console top list, the in-game readout, the exported report and the web dashboard. It sorts by cost
        /// like any other row, which normally puts it first: the point is that it cannot be scrolled past.
        /// </summary>
        internal static void AppendRow(List<SectionRow> rows, AttributionStats a, double frameMeanMs)
        {
            if (rows == null || a.Samples == 0 || a.UnattributedMeanMs <= 0.0) return;
            rows.Add(new SectionRow
            {
                Group = RowLabel,
                Label = RowLabel,
                MsPerFrame = a.UnattributedMeanMs,
                MaxMs = a.MaxUnattributedMs,
                Calls = 0.0,                 // not a call-based section: there is nothing here to count
                PctFrame = frameMeanMs > 0.0 ? a.UnattributedMeanMs / frameMeanMs * 100.0 : 0.0,
            });
            rows.Sort((x, y) => y.MsPerFrame.CompareTo(x.MsPerFrame));
        }

        private static double Median(double[] sorted)
        {
            int n = sorted.Length;
            if (n == 0) return 0.0;
            return (n & 1) == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) * 0.5;
        }
    }
}
