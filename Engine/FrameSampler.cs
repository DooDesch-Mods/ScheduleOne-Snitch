using System;

namespace Snitch.Engine
{
    /// <summary>Computed frame-time distribution over the rolling window, plus GC pressure.</summary>
    internal struct FrameStats
    {
        public int Samples;
        public double MeanMs, MedianMs, P95Ms, P99Ms, MinMs, MaxMs, StdDevMs;
        public double Gc0Per1000, Gc1Per1000;
        public double MinFps => MaxMs > 0.0 ? 1000.0 / MaxMs : 0.0;   // worst frame -> lowest fps
        public double MeanFps => MeanMs > 0.0 ? 1000.0 / MeanMs : 0.0;
    }

    /// <summary>
    /// The load-bearing measurement layer: a rolling frame-time window (build-independent and always works)
    /// plus GC collection-count pressure. Phase 0 confirmed Unity's ProfilerRecorder engine counters are inert
    /// in this IL2CPP build, so frame-time + GC ARE the truth; engine counters (if any) are advisory only and
    /// live in a separate provider. Ported/trimmed from Litterally's PerfSampler.
    /// </summary>
    internal static class FrameSampler
    {
        private const int Window = 120;
        private static readonly double[] _ring = new double[Window];
        private static int _count, _head;

        private static int _gc0Base, _gc1Base, _gcFrames;
        private static bool _gcInit;

        private static int _savedVSync = -999, _savedTarget = -999;

        internal static void Tick()
        {
            double ms = Time.unscaledDeltaTime * 1000.0;
            _ring[_head] = ms;
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
            _gcFrames++;
        }

        internal static FrameStats Snapshot()
        {
            var s = new FrameStats { Samples = _count };
            if (_count == 0) return s;

            double[] tmp = new double[_count];
            double sum = 0.0, min = double.MaxValue, max = 0.0;
            for (int i = 0; i < _count; i++)
            {
                double v = _ring[i];
                tmp[i] = v; sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            double mean = sum / _count;
            double varc = 0.0;
            for (int i = 0; i < _count; i++) { double d = tmp[i] - mean; varc += d * d; }
            Array.Sort(tmp);

            s.MeanMs = mean; s.MinMs = min; s.MaxMs = max;
            s.StdDevMs = Math.Sqrt(varc / _count);
            s.MedianMs = Percentile(tmp, 0.50);
            s.P95Ms = Percentile(tmp, 0.95);
            s.P99Ms = Percentile(tmp, 0.99);
            s.Gc0Per1000 = Gc0Per1000Frames();
            s.Gc1Per1000 = Gc1Per1000Frames();
            return s;
        }

        /// <summary>Stddev / mean over the window - used by the ablation stability gate.</summary>
        internal static double RelativeNoise()
        {
            FrameStats s = Snapshot();
            return s.MeanMs > 0 ? s.StdDevMs / s.MeanMs : 1.0;
        }

        /// <summary>Allocation-free, sort-free stddev/mean over the ring. The ablation gate calls this EVERY frame
        /// while settling, so it must not allocate or sort (doing so would pollute the very frame-time it measures).</summary>
        internal static double RelativeNoiseCheap()
        {
            if (_count == 0) return 1.0;
            double sum = 0.0;
            for (int i = 0; i < _count; i++) sum += _ring[i];
            double mean = sum / _count;
            if (mean <= 0.0) return 1.0;
            double varc = 0.0;
            for (int i = 0; i < _count; i++) { double d = _ring[i] - mean; varc += d * d; }
            return Math.Sqrt(varc / _count) / mean;
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0.0;
            int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        // ----- GC pressure (build-independent allocation evidence) -----

        internal static void ResetGcWindow()
        {
            _gc0Base = GC.CollectionCount(0);
            _gc1Base = SafeCount(1);
            _gcFrames = 0;
            _gcInit = true;
        }

        internal static double Gc0Per1000Frames()
        {
            if (!_gcInit || _gcFrames <= 0) return 0.0;
            return (GC.CollectionCount(0) - _gc0Base) * 1000.0 / _gcFrames;
        }

        internal static double Gc1Per1000Frames()
        {
            if (!_gcInit || _gcFrames <= 0) return 0.0;
            return (SafeCount(1) - _gc1Base) * 1000.0 / _gcFrames;
        }

        private static int SafeCount(int gen) { try { return GC.CollectionCount(gen); } catch { return 0; } }

        // ----- framerate cap control (so a measurement run reflects true cost) -----

        internal static void UncapFramerate()
        {
            try
            {
                if (_savedVSync == -999) { _savedVSync = QualitySettings.vSyncCount; _savedTarget = Application.targetFrameRate; }
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            catch (Exception e) { Core.Log?.Warning("[Snitch] uncap failed: " + e.Message); }
        }

        internal static void RestoreFramerate()
        {
            try
            {
                if (_savedVSync != -999)
                {
                    QualitySettings.vSyncCount = _savedVSync;
                    Application.targetFrameRate = _savedTarget;
                    _savedVSync = -999; _savedTarget = -999;
                }
            }
            catch { /* best effort */ }
        }
    }
}
