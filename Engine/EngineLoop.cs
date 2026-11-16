using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.LowLevel;

namespace Snitch.Engine
{
    /// <summary>
    /// Times the engine's own frame, step by step, by putting a timestamp marker in front of every system of
    /// Unity's PlayerLoop.
    ///
    /// The section timer only sees what something wraps. Everything else - physics, animation, rendering, the
    /// game's own scripts, coroutines, asset loading, the wait for the GPU - used to land in one "(unattributed)"
    /// row. A release player has no profiler markers (ProfilerRecorder is inert in this build), but the PlayerLoop
    /// itself is public: every engine step is one entry in it, and a custom entry placed in front of each one runs
    /// in between. The difference of two neighbouring timestamps is the cost of the engine step between them, with
    /// no blind spot: the steps add up to the whole frame.
    ///
    /// Cost: one native-to-managed call per engine step (about 130 a frame) that reads a timestamp and adds one
    /// number. Markers stay installed once placed; with sampling off each one returns after a bool read.
    /// </summary>
    internal static class EngineLoop
    {
        private const int Window = 120;
        private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

        internal static volatile bool Enabled;
        private static bool _installed;
        private static bool _installFailed;

        // Step names ("Phase.Step"); index _steps.Count is "between steps" (time from a phase's last marker to the
        // next phase's first one, which is normally zero).
        private static readonly List<string> _steps = new List<string>(160);
        private static int _between;
        private static int[] _markerStep;     // marker id -> step that starts after it
        private static int[] _stepCat;        // step -> EngineCategories index
        private static long[] _acc;           // ticks per step, current frame
        private static long _lastTs;
        private static int _curStep = -1;
        private static readonly List<UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction> _keepAlive =
            new List<UnityEngine.LowLevel.PlayerLoopSystem.UpdateFunction>(160);

        // Rolling window of finished frames.
        private static double[][] _ring;      // [frame][step] ms
        private static readonly double[] _frameMs = new double[Window];
        private static readonly int[] _gcFrame = new int[Window];   // collections that happened in that frame
        private static int _head, _count;
        private static int _gcLast = -1;

        internal static bool Installed => _installed;
        internal static int StepCount => _steps.Count;
        internal static string StepName(int i) => i == _between ? "(between steps)" : _steps[i];

        /// <summary>Place the markers. Runs once; later calls only switch timing on.</summary>
        internal static bool Enable()
        {
            if (!_installed && !_installFailed) Install();
            Enabled = _installed;
            return _installed;
        }

        internal static void Disable() => Enabled = false;

        internal static void Reset()
        {
            _head = 0;
            _count = 0;
            _curStep = -1;
            if (_acc != null) Array.Clear(_acc, 0, _acc.Length);
        }

        private static void Install()
        {
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                Il2CppReferenceArray<PlayerLoopSystem> phases = root.subSystemList;
                if (phases == null || phases.Length == 0)
                {
                    _installFailed = true;
                    Core.Log?.Error("[snitch] engine timing not installed (guard: the PlayerLoop has no phases).");
                    return;
                }

                var markerStep = new List<int>(200);
                var newPhases = new Il2CppReferenceArray<PlayerLoopSystem>(phases.Length);
                for (int p = 0; p < phases.Length; p++)
                {
                    PlayerLoopSystem phase = phases[p];
                    string phaseName = TypeName(phase.type, "Phase" + p);
                    Il2CppReferenceArray<PlayerLoopSystem> subs = phase.subSystemList;
                    int n = subs?.Length ?? 0;

                    // marker, step, marker, step, ..., marker: every step has a marker in front, the phase one at the end
                    var list = new Il2CppReferenceArray<PlayerLoopSystem>(n * 2 + 1);
                    for (int s = 0; s < n; s++)
                    {
                        PlayerLoopSystem step = subs[s];
                        int stepId = _steps.Count;
                        _steps.Add(phaseName + "." + TypeName(step.type, "Step" + s));
                        list[s * 2] = Marker(markerStep.Count);
                        markerStep.Add(stepId);
                        list[s * 2 + 1] = step;
                    }
                    list[n * 2] = Marker(markerStep.Count);
                    markerStep.Add(-1);   // resolved to _between below
                    phase.subSystemList = list;
                    newPhases[p] = phase;
                }

                _between = _steps.Count;
                _stepCat = new int[_steps.Count + 1];
                for (int i = 0; i < _steps.Count; i++) _stepCat[i] = EngineCategories.Of(_steps[i]);
                _stepCat[_between] = EngineCategories.Other;
                _markerStep = markerStep.ToArray();
                for (int i = 0; i < _markerStep.Length; i++) if (_markerStep[i] < 0) _markerStep[i] = _between;
                _acc = new long[_steps.Count + 1];
                _ring = new double[Window][];
                for (int i = 0; i < Window; i++) _ring[i] = new double[_steps.Count + 1];

                root.subSystemList = newPhases;
                PlayerLoop.SetPlayerLoop(root);
                _installed = true;
                Core.Log?.Msg($"[snitch] engine timing installed: {_steps.Count} engine steps in {phases.Length} phases.");
            }
            catch (Exception e)
            {
                _installFailed = true;
                Core.Log?.Error("[snitch] engine timing not installed: " + e);
            }
        }

        private static PlayerLoopSystem Marker(int id)
        {
            System.Action a = () => Mark(id);
            PlayerLoopSystem.UpdateFunction fn = a;
            _keepAlive.Add(fn);
            var m = new PlayerLoopSystem();
            m.type = Il2CppType.Of<Il2CppSystem.Object>();
            m.updateDelegate = fn;
            return m;
        }

        private static string TypeName(Il2CppSystem.Type t, string fallback)
        {
            try { string n = t?.Name; return string.IsNullOrEmpty(n) ? fallback : n; }
            catch (Exception e)
            {
                Core.Log?.Warning("[snitch] engine timing: a PlayerLoop entry would not give its name - " + e.Message);
                return fallback;
            }
        }

        // ----- hot path -----

        private static void Mark(int id)
        {
            long now = Stopwatch.GetTimestamp();
            if (!Enabled) { _curStep = -1; return; }
            if (id == 0 && _curStep >= 0) FinishFrame(now);
            if (_curStep >= 0) _acc[_curStep] += now - _lastTs;
            _curStep = _markerStep[id];
            _lastTs = now;
        }

        /// <summary>Marker 0 sits in front of the first step of the first phase, so it is the frame boundary.</summary>
        private static void FinishFrame(long now)
        {
            _acc[_curStep] += now - _lastTs;
            _curStep = -1;

            double[] row = _ring[_head];
            double total = 0.0;
            for (int i = 0; i < _acc.Length; i++)
            {
                double ms = _acc[i] * TickToMs;
                row[i] = ms;
                total += ms;
                _acc[i] = 0;
            }
            // Sections are flushed here, on the same boundary, so a section's frame and the engine's frame are the
            // same interval and a spike can name the section that grew in it.
            double attributedMs = Sections.SectionProfiler.Flush();
            Attribution.Push(total, attributedMs);

            int gc = Il2CppGcCount();
            _gcFrame[_head] = _gcLast >= 0 && gc > _gcLast ? gc - _gcLast : 0;
            _gcLast = gc;
            int netGc = GC.CollectionCount(0);
            int netGcFrame = _netGcLast >= 0 && netGc > _netGcLast ? netGc - _netGcLast : 0;
            _netGcLast = netGc;
            // A collection ends a cycle of garbage; who made that garbage is read now, before the next cycle starts.
            if (_gcFrame[_head] > 0) _gameCycle = Sections.SectionProfiler.EndCycle(true, out _gameCycleBytes);
            if (netGcFrame > 0) _netCycle = Sections.SectionProfiler.EndCycle(false, out _netCycleBytes);
            _frameMs[_head] = total;
            if (_spikeAtMs > 0 && total > _spikeAtMs) RecordStutter(row, total, _gcFrame[_head], netGcFrame);
            _head = (_head + 1) % Window;
            if (_count < Window) _count++;
        }

        // Live spike line: the window forgets a spike after two seconds, the log keeps it.
        private static double _spikeAtMs;
        private static double _medianMs;
        private static double[] _stepMedian;
        private static float _lastSpikeLog;
        private static int _spikesSuppressed;

        /// <summary>Called on the poll tick: refresh the step medians and the threshold a live spike is judged by.</summary>
        internal static void Refresh(double factor)
        {
            if (_count < 30) { _spikeAtMs = 0; return; }
            List<StepStat> stats = Stats();
            if (_stepMedian == null) _stepMedian = new double[_acc.Length];
            foreach (StepStat st in stats) _stepMedian[st.Step] = st.Median;
            double[] sorted = new double[_count];
            Array.Copy(_frameMs, sorted, _count);
            Array.Sort(sorted);
            _medianMs = sorted[_count / 2];
            _spikeAtMs = Math.Max(_medianMs * factor, _medianMs + 4.0);
        }

        internal enum CauseKind { Gc, NetGc, Section, Engine }

        private static int _netGcLast = -1;
        private static List<Sections.SectionProfiler.AllocShare> _gameCycle = new List<Sections.SectionProfiler.AllocShare>();
        private static List<Sections.SectionProfiler.AllocShare> _netCycle = new List<Sections.SectionProfiler.AllocShare>();
        private static long _gameCycleBytes, _netCycleBytes;

        /// <summary>A mod is named as the maker of a collection's garbage when it made at least this share of it.</summary>
        private const double GarbageOwnerPct = 20.0;

        /// <summary>One stutter frame and the single cause it is blamed on.</summary>
        internal struct Stutter
        {
            public float Time;          // realtimeSinceStartup
            public double FrameMs, MedianMs;
            public CauseKind Kind;
            public string Cause;        // "Garbage collection", ".NET garbage collection", a section label, or an engine category label
            public string Detail;       // the engine step that grew most
            public double CauseMs;      // ms the cause grew over its usual cost
            public string Contributors; // the sections that grew most in that frame, "A +x, B +y"; empty when none did
            public string Mod;          // the mod the cause belongs to, empty when no mod is to blame or none is known
            public string Class;        // the game class whose code ran long, empty when unknown
            public string Garbage;      // for a collection: who made its garbage, "A 12.3 MB (78%), B ..."; else empty

            /// <summary>The cause in one line for a player: what, and whose.</summary>
            public string Display
            {
                get
                {
                    switch (Kind)
                    {
                        case CauseKind.Gc:
                        case CauseKind.NetGc:
                            if (!string.IsNullOrEmpty(Mod)) return Cause + ", most garbage from " + Mod;
                            return string.IsNullOrEmpty(Garbage) ? Cause : Cause + ", garbage mostly from the game";
                        case CauseKind.Section:
                            return string.IsNullOrEmpty(Class) ? Cause : "game script " + Class;
                        default:
                            string s = Cause;
                            if (!string.IsNullOrEmpty(Class)) s += " - " + Class;
                            if (!string.IsNullOrEmpty(Mod)) s += " (" + Mod + ")";
                            return s;
                    }
                }
            }
        }

        private const int StutterKeep = 64;
        private static readonly Stutter[] _stutters = new Stutter[StutterKeep];
        private static int _stutterHead, _stutterCount;
        private static readonly double[] _catExcess = new double[EngineCategories.Count];

        /// <summary>Newest first.</summary>
        internal static List<Stutter> RecentStutters(float withinSeconds)
        {
            var list = new List<Stutter>(_stutterCount);
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = 0; i < _stutterCount; i++)
            {
                Stutter s = _stutters[(_stutterHead - 1 - i + StutterKeep) % StutterKeep];
                if (now - s.Time > withinSeconds) break;
                list.Add(s);
            }
            return list;
        }

        /// <summary>Stutters of the last <paramref name="withinSeconds"/> counted per cause, most frequent first.</summary>
        internal static List<KeyValuePair<string, int>> StutterCounts(float withinSeconds)
        {
            var counts = new Dictionary<string, int>();
            foreach (Stutter s in RecentStutters(withinSeconds))
                counts[s.Cause] = counts.TryGetValue(s.Cause, out int n) ? n + 1 : 1;
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>Blames the frame on one cause: a section (mod or wrapped game script) that grew by at least half
        /// the frame's excess, else a game garbage collection that ran in it, else a .NET one (only mods and the
        /// loader allocate there), else the engine category that grew most.</summary>
        private static void RecordStutter(double[] row, double total, int gc, int netGc)
        {
            double excess = total - _medianMs;
            Array.Clear(_catExcess, 0, _catExcess.Length);
            int topStep = -1;
            double topStepMs = 0;
            for (int s = 0; s < row.Length; s++)
            {
                double d = row[s] - _stepMedian[s];
                if (d <= 0) continue;
                _catExcess[_stepCat[s]] += d;
                if (d > topStepMs) { topStepMs = d; topStep = s; }
            }
            int topCat = EngineCategories.Other;
            for (int c = 0; c < _catExcess.Length; c++) if (_catExcess[c] > _catExcess[topCat]) topCat = c;

            var grew = Sections.SectionProfiler.NewestFrameGrowth(3, 0.5);
            var st = new Stutter
            {
                Time = UnityEngine.Time.realtimeSinceStartup,
                FrameMs = total,
                MedianMs = _medianMs,
                Detail = topStep >= 0 ? StepName(topStep) : "",
                Contributors = "",
                Mod = "",
                Class = "",
                Garbage = "",
            };
            if (grew.Count > 0)
            {
                var cb = new System.Text.StringBuilder(80);
                foreach (var kv in grew)
                {
                    if (cb.Length > 0) cb.Append(", ");
                    cb.Append(Sections.SectionProfiler.LabelOf(kv.Key)).Append(" +").Append(kv.Value.ToString("F1"));
                }
                st.Contributors = cb.ToString();
            }
            if (grew.Count > 0 && grew[0].Value >= excess * 0.5)
            {
                st.Kind = CauseKind.Section;
                st.Cause = Sections.SectionProfiler.LabelOf(grew[0].Key);
                st.CauseMs = grew[0].Value;
                SplitLabel(st.Cause, out st.Mod, out st.Class);
            }
            else if (gc > 0 || netGc > 0)
            {
                bool game = gc > 0;
                st.Kind = game ? CauseKind.Gc : CauseKind.NetGc;
                st.Cause = game ? "Garbage collection" : ".NET garbage collection";
                st.CauseMs = excess;
                List<Sections.SectionProfiler.AllocShare> shares = game ? _gameCycle : _netCycle;
                st.Garbage = Sections.SectionProfiler.Describe(shares, 3);
                foreach (Sections.SectionProfiler.AllocShare s in shares)
                {
                    if (s.Group == Sections.SectionProfiler.OutsideGroup) continue;
                    if (s.Pct >= GarbageOwnerPct) st.Mod = s.Group;
                    break;
                }
            }
            else
            {
                st.Kind = CauseKind.Engine;
                st.Cause = EngineCategories.Label(topCat);
                st.CauseMs = _catExcess[topCat];
                // A section that grew by a quarter of the excess is named next to the engine kind: not the whole
                // cause, but the biggest known part of it.
                foreach (var kv in grew)
                {
                    if (kv.Value < excess * 0.25) break;
                    SplitLabel(Sections.SectionProfiler.LabelOf(kv.Key), out string mod, out string cls);
                    if (st.Mod.Length == 0 && mod.Length > 0) st.Mod = mod;
                    if (st.Class.Length == 0 && cls.Length > 0) st.Class = cls;
                }
            }
            _stutters[_stutterHead] = st;
            _stutterHead = (_stutterHead + 1) % StutterKeep;
            if (_stutterCount < StutterKeep) _stutterCount++;
            LogSpike(row, total, gc, st, grew);
        }

        /// <summary>"Chassis.OnLateUpdate" -> mod "Chassis"; "Game.NPCMovement.Update" (a deep probe) -> class
        /// "NPCMovement.Update" and no mod; "SnitchTest.Burn" -> mod "SnitchTest".</summary>
        private static void SplitLabel(string label, out string mod, out string cls)
        {
            mod = ""; cls = "";
            if (string.IsNullOrEmpty(label)) return;
            int dot = label.IndexOf('.');
            string group = dot > 0 ? label.Substring(0, dot) : label;
            if (group == "Game") cls = dot > 0 ? label.Substring(dot + 1) : "";
            else mod = group;
        }

        /// <summary>The game's own scripts ran long and no class is known. The deep probes time those scripts per
        /// class, so the stutter line points to them while they are off.</summary>
        private static bool NeedsDeepHint(Stutter st)
        {
            if (st.Kind != CauseKind.Engine || st.Class.Length > 0 || Snitch.Vanilla.DeepProbes.Enabled) return false;
            return st.Cause == EngineCategories.Label(EngineCategories.Scripts)
                || st.Cause == EngineCategories.Label(EngineCategories.Coroutines);
        }

        private static void LogSpike(double[] row, double total, int gc, Stutter st, List<KeyValuePair<int, double>> grew)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            // Small spikes are rate-limited; one at twice the threshold or more is always written.
            if (now - _lastSpikeLog < 0.25f && total < _spikeAtMs * 2.0) { _spikesSuppressed++; return; }
            _lastSpikeLog = now;
            int a = -1, b = -1, c = -1;
            double da = 0, db = 0, dc = 0;
            for (int s = 0; s < row.Length; s++)
            {
                double d = row[s] - _stepMedian[s];
                if (d > da) { c = b; dc = db; b = a; db = da; a = s; da = d; }
                else if (d > db) { c = b; dc = db; b = s; db = d; }
                else if (d > dc) { c = s; dc = d; }
            }
            var sb = new System.Text.StringBuilder(200);
            sb.Append("[snitch] stutter ").Append(total.ToString("F1")).Append(" ms (median ")
              .Append(_medianMs.ToString("F1")).Append(")");
            if (gc > 0) sb.Append(" GC x").Append(gc);
            if (st.Kind == CauseKind.NetGc) sb.Append(" .NET GC");
            sb.Append(" cause: ").Append(st.Cause).Append(" +").Append(st.CauseMs.ToString("F1"));
            if (st.Mod.Length > 0) sb.Append(" | mod: ").Append(st.Mod);
            if (st.Class.Length > 0) sb.Append(" | class: ").Append(st.Class);
            else if (NeedsDeepHint(st)) sb.Append(" | 'snitch deep on' names the class");
            if (st.Garbage.Length > 0)
            {
                long bytes = st.Kind == CauseKind.Gc ? _gameCycleBytes : _netCycleBytes;
                sb.Append(" | garbage since the last collection ").Append(Sections.SectionProfiler.Bytes(bytes)).Append(": ").Append(st.Garbage);
            }
            sb.Append(" | steps:");
            if (a >= 0) sb.Append(' ').Append(StepName(a)).Append(" +").Append(da.ToString("F1"));
            if (b >= 0) sb.Append(", ").Append(StepName(b)).Append(" +").Append(db.ToString("F1"));
            if (c >= 0) sb.Append(", ").Append(StepName(c)).Append(" +").Append(dc.ToString("F1"));
            if (grew.Count > 0)
            {
                sb.Append(" || sections:");
                foreach (var kv in grew) sb.Append(' ').Append(Sections.SectionProfiler.LabelOf(kv.Key)).Append(" +").Append(kv.Value.ToString("F1"));
            }
            if (_spikesSuppressed > 0) { sb.Append(" (+").Append(_spikesSuppressed).Append(" more not logged)"); _spikesSuppressed = 0; }
            Core.Log?.Msg(sb.ToString());
        }

        private static bool _gcWarned;

        private static int Il2CppGcCount()
        {
            try { return Il2CppSystem.GC.CollectionCount(0); }
            catch (Exception e)
            {
                if (!_gcWarned)
                {
                    _gcWarned = true;
                    Core.Log?.Warning("[snitch] engine timing: the game's GC count is unreadable, GC frames are not marked - " + e.Message);
                }
                return -1;
            }
        }

        // ----- reading -----

        internal struct StepStat { public int Step; public double Mean, Median, Max; }

        internal static int Count => _count;

        /// <summary>Per step mean/median/max over the window, sorted by mean.</summary>
        internal static List<StepStat> Stats()
        {
            var list = new List<StepStat>(_acc?.Length ?? 0);
            if (_count == 0) return list;
            double[] tmp = new double[_count];
            for (int s = 0; s < _acc.Length; s++)
            {
                double sum = 0, max = 0;
                for (int f = 0; f < _count; f++) { double v = _ring[f][s]; tmp[f] = v; sum += v; if (v > max) max = v; }
                Array.Sort(tmp);
                list.Add(new StepStat { Step = s, Mean = sum / _count, Median = tmp[_count / 2], Max = max });
            }
            list.Sort((a, b) => b.Mean.CompareTo(a.Mean));
            return list;
        }

        internal struct CategoryStat { public int Category; public double Mean, Max; }

        internal static int CategoryOfStep(int step) => _stepCat != null && (uint)step < (uint)_stepCat.Length ? _stepCat[step] : EngineCategories.Other;

        /// <summary>Per category mean and worst frame over the window (each frame's steps summed by category), sorted by mean.</summary>
        internal static List<CategoryStat> CategoryStats()
        {
            var list = new List<CategoryStat>(EngineCategories.Count);
            if (_count == 0 || _stepCat == null) return list;
            var sum = new double[EngineCategories.Count];
            var max = new double[EngineCategories.Count];
            var frame = new double[EngineCategories.Count];
            for (int f = 0; f < _count; f++)
            {
                Array.Clear(frame, 0, frame.Length);
                double[] row = _ring[f];
                for (int s = 0; s < row.Length; s++) frame[_stepCat[s]] += row[s];
                for (int c = 0; c < frame.Length; c++) { sum[c] += frame[c]; if (frame[c] > max[c]) max[c] = frame[c]; }
            }
            for (int c = 0; c < sum.Length; c++) list.Add(new CategoryStat { Category = c, Mean = sum[c] / _count, Max = max[c] });
            list.Sort((a, b) => b.Mean.CompareTo(a.Mean));
            return list;
        }

        internal struct SpikeFrame
        {
            public double FrameMs, MedianMs;
            public int Gc;
            public List<KeyValuePair<int, double>> Excess;   // step -> ms over its median, largest first
        }

        /// <summary>The frames in the window above factor x the median frame, each with the steps that grew.</summary>
        internal static List<SpikeFrame> Spikes(double factor, int topSteps)
        {
            var result = new List<SpikeFrame>();
            if (_count < 10) return result;
            double[] sorted = new double[_count];
            Array.Copy(_frameMs, sorted, _count);
            Array.Sort(sorted);
            double median = sorted[_count / 2];
            List<StepStat> stats = Stats();
            var medianOf = new double[_acc.Length];
            foreach (StepStat st in stats) medianOf[st.Step] = st.Median;

            for (int f = 0; f < _count; f++)
            {
                if (_frameMs[f] <= median * factor) continue;
                var ex = new List<KeyValuePair<int, double>>();
                for (int s = 0; s < _acc.Length; s++)
                {
                    double d = _ring[f][s] - medianOf[s];
                    if (d > 0.05) ex.Add(new KeyValuePair<int, double>(s, d));
                }
                ex.Sort((a, b) => b.Value.CompareTo(a.Value));
                if (ex.Count > topSteps) ex.RemoveRange(topSteps, ex.Count - topSteps);
                result.Add(new SpikeFrame { FrameMs = _frameMs[f], MedianMs = median, Gc = _gcFrame[f], Excess = ex });
            }
            return result;
        }
    }
}
