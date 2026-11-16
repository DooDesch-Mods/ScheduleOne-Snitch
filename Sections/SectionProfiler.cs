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
            public long GameHeapBytes;   // game-heap growth while this label was the innermost open one, since the last game collection
            public long NetBytes;        // .NET allocations while innermost, since the last .NET collection
            public long GameHeapTotal, NetTotal;   // the same since sampling started

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

        // ----- root-level (non-overlapping) accounting -----
        // Summing every label would double-count nesting: "MyMod.OnUpdate" already contains "MyMod.Pathfinding".
        // These three track only the OUTERMOST open span, so the total is wall time spent inside any section with
        // each slice counted once. That total is the attributed half of the unattributed line.
        private static int _globalDepth;
        private static long _rootStartTs;
        private static long _rootTicksThisFrame;

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
            if (AllocTracking && Environment.CurrentManagedThreadId == _mainThread)
            {
                ChargeAllocations();
                if (_sp < _stack.Length) _stack[_sp] = id;
                _sp++;
            }
            long now = 0L;
            bool haveNow = false;
            if (a.Depth++ == 0)                    // only the outermost entry starts the label clock
            {
                now = Stopwatch.GetTimestamp();
                haveNow = true;
                a.StartTs = now;
            }
            if (_globalDepth++ == 0)               // ... and the outermost section of all starts the root clock
            {
                if (!haveNow) now = Stopwatch.GetTimestamp();
                _rootStartTs = now;
            }
        }

        internal static void End(int id)
        {
            if ((uint)id >= (uint)_all.Count) return;
            Accumulator a = _all[id];
            if (AllocTracking && Environment.CurrentManagedThreadId == _mainThread && _sp > 0)
            {
                ChargeAllocations();
                _sp--;
            }
            bool closesLabel = --a.Depth == 0;
            bool closesRoot = _globalDepth > 0 && --_globalDepth == 0;   // an unbalanced End must not open a negative root span
            if (!closesLabel && !closesRoot)
            {
                if (a.Depth < 0) a.Depth = 0;   // defensive: unbalanced End
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (closesLabel)
            {
                a.TicksThisFrame += now - a.StartTs;
                a.CallsThisFrame++;
            }
            else if (a.Depth < 0) a.Depth = 0;
            if (closesRoot) _rootTicksThisFrame += now - _rootStartTs;
        }

        internal static void Begin(string label) => Begin(GetId(label));
        internal static void End(string label) => End(GetId(label));

        internal static Scope Sample(string label) { int id = GetId(label); Begin(id); return new Scope(id); }

        // ----- frame boundary -----

        /// <summary>Push each label's per-frame totals into its rolling window, then reset. Self-healing: a leaked
        /// Depth (e.g. an original that threw past a non-finalizer patch) is reset every frame. Returns the frame's
        /// root-level total in ms - wall time spent inside ANY section, nesting counted once - which is the
        /// attributed half of the unattributed line.</summary>
        internal static double Flush()
        {
            if (AllocTracking)
            {
                _sp = 0;                 // sections never span frames; a leaked span must not keep collecting bytes
                ChargeAllocations();     // what was allocated since the last section closed belongs to no mod
            }
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
            double rootMs = _rootTicksThisFrame * TickToMs;
            _rootTicksThisFrame = 0;
            _globalDepth = 0;
            return rootMs;
        }

        internal static void Reset()
        {
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                a.Head = 0; a.Count = 0; a.TicksThisFrame = 0; a.CallsThisFrame = 0; a.Depth = 0;
                a.GameHeapBytes = 0; a.NetBytes = 0; a.GameHeapTotal = 0; a.NetTotal = 0;
            }
            _globalDepth = 0;
            _rootTicksThisFrame = 0;
            _sp = 0;
            _outsideGameHeap = 0; _outsideNet = 0; _outsideGameHeapTotal = 0; _outsideNetTotal = 0;
            _haveBaseline = false;
        }

        // ----- allocation accounting -----
        //
        // Which mod makes the garbage that a collection then has to clear. Each Begin and End reads two counters and
        // charges what was allocated since the previous read to the label that was innermost in between, so nested
        // sections of different mods each get their own bytes. Bytes allocated outside every section go to "outside".
        //  - .NET heap: GC.GetAllocatedBytesForCurrentThread, exact to the allocation context (a few KB). Only mods
        //    and the loader run .NET code, so this heap is theirs alone.
        //  - game heap: the growth of il2cpp_gc_get_used_size. The game's collector counts used memory per heap
        //    block, so a small allocation that reuses a swept block shows as zero and the one that takes a new block
        //    carries it. Over a whole cycle between two collections that evens out into each label's share.
        // Main thread only: the counters are per thread, and the label stack belongs to the main thread.

        internal static bool AllocTracking;
        private static int _mainThread = -1;
        private static readonly int[] _stack = new int[64];
        private static int _sp;
        private static long _lastNet, _lastGameHeap;
        private static bool _haveBaseline;
        private static bool _gameHeapReadable = true;
        private static long _outsideGameHeap, _outsideNet, _outsideGameHeapTotal, _outsideNetTotal;

        /// <summary>Switch allocation accounting on for the calling thread, which has to be the main thread.</summary>
        internal static void StartAllocTracking()
        {
            _mainThread = Environment.CurrentManagedThreadId;
            _sp = 0;
            _haveBaseline = false;
            AllocTracking = true;
        }

        internal static void StopAllocTracking() => AllocTracking = false;

        private static void ChargeAllocations()
        {
            long net = GC.GetAllocatedBytesForCurrentThread();
            long heap = _gameHeapReadable ? GameHeapUsed() : 0;
            if (!_haveBaseline)
            {
                _lastNet = net; _lastGameHeap = heap; _haveBaseline = true;
                return;
            }
            long dNet = net - _lastNet, dHeap = heap - _lastGameHeap;
            _lastNet = net; _lastGameHeap = heap;
            if (dNet < 0) dNet = 0;
            if (dHeap < 0) dHeap = 0;      // a collection ran in between and shrank the heap
            if (dNet == 0 && dHeap == 0) return;
            int top = _sp > 0 ? _stack[Math.Min(_sp, _stack.Length) - 1] : -1;
            if (top >= 0 && top < _all.Count)
            {
                Accumulator a = _all[top];
                a.GameHeapBytes += dHeap; a.GameHeapTotal += dHeap;
                a.NetBytes += dNet; a.NetTotal += dNet;
            }
            else
            {
                _outsideGameHeap += dHeap; _outsideGameHeapTotal += dHeap;
                _outsideNet += dNet; _outsideNetTotal += dNet;
            }
        }

        [System.Runtime.InteropServices.DllImport("GameAssembly", EntryPoint = "il2cpp_gc_get_used_size",
            CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern long Il2CppGcUsedSize();

        private static long GameHeapUsed()
        {
            try { return Il2CppGcUsedSize(); }
            catch (Exception e)
            {
                _gameHeapReadable = false;
                Core.Log?.Warning("[snitch] allocations: the game heap size is unreadable, only .NET garbage is attributed - " + e.Message);
                return 0;
            }
        }

        /// <summary>One mod's (or the outside's) share of the garbage of one collection cycle.</summary>
        internal struct AllocShare { public string Group; public long Bytes; public double Pct; }

        /// <summary>The garbage of the cycle that a collection just ended, grouped by mod, largest first, and the
        /// cycle reset. <paramref name="gameHeap"/> picks the heap: the game's own collector or the .NET one.
        /// Allocations outside every section are listed as <see cref="OutsideGroup"/>.</summary>
        internal static List<AllocShare> EndCycle(bool gameHeap, out long totalBytes)
        {
            if (AllocTracking && Environment.CurrentManagedThreadId == _mainThread) ChargeAllocations();
            var byGroup = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                long b = gameHeap ? a.GameHeapBytes : a.NetBytes;
                if (gameHeap) a.GameHeapBytes = 0; else a.NetBytes = 0;
                if (b <= 0) continue;
                byGroup[a.Group] = byGroup.TryGetValue(a.Group, out long n) ? n + b : b;
            }
            long outside = gameHeap ? _outsideGameHeap : _outsideNet;
            if (gameHeap) _outsideGameHeap = 0; else _outsideNet = 0;
            if (outside > 0) byGroup[OutsideGroup] = outside;
            return Shares(byGroup, out totalBytes);
        }

        /// <summary>Allocations since sampling started, grouped by mod, largest first.</summary>
        internal static List<AllocShare> TotalByGroup(bool gameHeap, out long totalBytes)
        {
            var byGroup = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                long b = gameHeap ? a.GameHeapTotal : a.NetTotal;
                if (b <= 0) continue;
                byGroup[a.Group] = byGroup.TryGetValue(a.Group, out long n) ? n + b : b;
            }
            long outside = gameHeap ? _outsideGameHeapTotal : _outsideNetTotal;
            if (outside > 0) byGroup[OutsideGroup] = outside;
            return Shares(byGroup, out totalBytes);
        }

        /// <summary>The group name of allocations made outside every section: the game itself and any code no label covers.</summary>
        internal const string OutsideGroup = "(outside mod code)";

        private static List<AllocShare> Shares(Dictionary<string, long> byGroup, out long total)
        {
            total = 0;
            foreach (long b in byGroup.Values) total += b;
            var list = new List<AllocShare>(byGroup.Count);
            foreach (KeyValuePair<string, long> kv in byGroup)
                list.Add(new AllocShare { Group = kv.Key, Bytes = kv.Value, Pct = total > 0 ? kv.Value * 100.0 / total : 0 });
            list.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
            return list;
        }

        /// <summary>"SnitchTest 12.3 MB (78%), (outside mod code) 3.1 MB (20%)" for the first <paramref name="n"/> shares.</summary>
        internal static string Describe(List<AllocShare> shares, int n)
        {
            var sb = new System.Text.StringBuilder(96);
            for (int i = 0; i < shares.Count && i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(shares[i].Group).Append(' ').Append(Bytes(shares[i].Bytes)).Append(" (").Append(shares[i].Pct.ToString("F0")).Append("%)");
            }
            return sb.ToString();
        }

        internal static string Bytes(long b) =>
            b >= 1L << 20 ? (b / 1048576.0).ToString("F1") + " MB" : b >= 1024 ? (b / 1024.0).ToString("F0") + " KB" : b + " B";

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

        internal static string LabelOf(int id) => (uint)id < (uint)_all.Count ? _all[id].Label : "(unknown)";

        /// <summary>The labels whose newest frame is furthest above their own median in the window, largest first.
        /// A label with under 10 frames of history counts against zero: a section that first runs in the stutter
        /// frame (a first load, a one-off rebuild) is the likeliest cause of it. Only spike reporting calls this, so
        /// the per-label sort is affordable.</summary>
        internal static List<KeyValuePair<int, double>> NewestFrameGrowth(int n, double minMs)
        {
            var result = new List<KeyValuePair<int, double>>(n + 1);
            double[] tmp = new double[Window];
            for (int i = 0; i < _all.Count; i++)
            {
                Accumulator a = _all[i];
                if (a.Count == 0) continue;
                int newest = (a.Head - 1 + Window) % Window;
                double last = a.MsRing[newest];
                if (last < minMs) continue;
                double median = 0.0;
                if (a.Count >= 10)
                {
                    Array.Copy(a.MsRing, tmp, a.Count);
                    Array.Sort(tmp, 0, a.Count);
                    median = tmp[a.Count / 2];
                }
                double d = last - median;
                if (d < minMs) continue;
                result.Add(new KeyValuePair<int, double>(i, d));
            }
            result.Sort((x, y) => y.Value.CompareTo(x.Value));
            if (result.Count > n) result.RemoveRange(n, result.Count - n);
            return result;
        }
    }

    /// <summary>Zero-heap-alloc timing scope: <c>using (SectionProfiler.Sample("X")) { ... }</c>.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly int _id;
        internal Scope(int id) { _id = id; }
        public void Dispose() => SectionProfiler.End(_id);
    }
}
