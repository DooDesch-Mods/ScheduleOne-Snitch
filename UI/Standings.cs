using System;
using System.Collections.Generic;
using System.Text;
using Snitch.Engine;
using Snitch.Panels;
using Snitch.Sections;

namespace Snitch.UI
{
    /// <summary>
    /// Who a mod is on every Snitch surface: a bib number and a team colour that depend only on the set of known mod
    /// names, so the in-game overlay, the web dashboard and the phone remote show the same mod the same way.
    /// Bib 0 is the engine's "(unattributed)" row; every other bib is the 1-based position of the lower-cased name
    /// in the code-unit order of the known set. The set only grows during a session, so a mod that stops reporting
    /// keeps its number. The colour is a hash of the lower-cased name, so it does not move when another mod joins.
    /// </summary>
    internal static class Identity
    {
        internal const string UnattributedColor = "#5B6573";

        private static readonly string[] Team =
        {
            "#0F8B8D", "#6A4C93", "#E07A1F", "#2F6FDE", "#B5179E", "#8C5A3C", "#3A86A8", "#6B8E23",
        };

        private static readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _bibs = new Dictionary<string, int>(16);
        private static bool _stale;

        internal static bool IsUnattributed(string name)
            => string.IsNullOrWhiteSpace(name)
               || string.Equals(name.Trim(), Attribution.RowLabel, StringComparison.OrdinalIgnoreCase);

        /// <summary>Adds a mod name to the known set. The unattributed row is not part of the set.</summary>
        internal static void Learn(string name)
        {
            if (IsUnattributed(name)) return;
            if (_known.Add(name.ToLowerInvariant())) _stale = true;
        }

        /// <summary>Adds every name to the known set and renumbers when the set grew.</summary>
        internal static void Refresh(IEnumerable<string> names)
        {
            foreach (string n in names) Learn(n);
            Renumber();
        }

        private static void Renumber()
        {
            if (!_stale) return;
            _stale = false;
            var list = new List<string>(_known);
            list.Sort(string.CompareOrdinal);
            _bibs.Clear();
            for (int i = 0; i < list.Count; i++) _bibs[list[i]] = i + 1;
        }

        /// <summary>The bib of a mod; a name not seen before joins the known set first, so only the unattributed
        /// row ever gets 0.</summary>
        internal static int Bib(string name)
        {
            if (IsUnattributed(name)) return 0;
            Learn(name);
            Renumber();
            // Learn put the name in the map; the count + 1 fallback is the dashboard's rule for a name missing from it.
            return _bibs.TryGetValue(name.ToLowerInvariant(), out int bib) ? bib : _bibs.Count + 1;
        }

        internal static string Color(string name)
        {
            if (IsUnattributed(name)) return UnattributedColor;
            return Team[(int)(Fnv1a32(Encoding.UTF8.GetBytes(name.ToLowerInvariant())) % (uint)Team.Length)];
        }

        internal static uint Fnv1a32(byte[] bytes)
        {
            uint hash = 2166136261;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash = unchecked(hash * 16777619);
            }
            return hash;
        }
    }

    /// <summary>One line of the standings: a section, ranked by its mean cost over the chosen window.</summary>
    internal sealed class Rider
    {
        public string Key;
        public string Group;
        public string Label;
        public double Ms;
        public double Gap;
        public double MaxMs;
        public double Calls;
        public int Rank;
        public bool Dnf;
        public double SilentFor;
    }

    /// <summary>Frame figures averaged over the samples inside the chosen window.</summary>
    internal struct FrameWindow
    {
        public double MeanMs, P95Ms, Gc0Per1000, MinFps;
    }

    /// <summary>
    /// The race table behind the overlay. The engine keeps one fixed 120-frame window, so the 10 s / 30 s / 2 min
    /// choice is made here, from a history of every poll. Each section is a rider keyed by group and label. Riders
    /// are ranked by mean ms per frame over the window. A rider that is missing from the newest poll for a few
    /// seconds stays on the table as DNF with the time since its last report, however long ago that was: the
    /// last-seen table is kept for the whole session while the sample history is trimmed to two minutes. The clock
    /// is the time of the newest sample, so stopping the sampler freezes the table instead of retiring everyone.
    /// </summary>
    internal static class Standings
    {
        internal const double HistorySeconds = 125.0;
        internal const double DnfAfterSeconds = 3.0;

        // The bar scale steps of the web dashboard, so both surfaces draw the same bar length for the same ms.
        private static readonly double[] ScaleSteps =
            { 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25, 33, 50, 100, 250, 500, 1000 };

        private sealed class Sample
        {
            public double T;
            public double FrameMs;
            public double P95Ms;
            public double Gc0;
            public double MinFps;
            public SectionRow[] Rows;
        }

        private sealed class Seen
        {
            public SectionRow Row;
            public double LastT;
        }

        private static readonly List<Sample> _history = new List<Sample>(600);
        private static readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>(64);
        private static object _lastSource;

        /// <summary>The newest sample's time; 0 until the first poll arrived.</summary>
        internal static double Clock => _history.Count == 0 ? 0.0 : _history[_history.Count - 1].T;
        internal static int Count => _history.Count;

        private static string KeyOf(SectionRow r) => (r.Group ?? "") + "\u0001" + (r.Label ?? "");

        /// <summary>Take a sample when the engine published a new poll. Returns true when one was added.</summary>
        internal static bool Pull(double now)
        {
            List<SectionRow> src = SnitchCore.LatestSections;
            if (src == null || src.Count == 0 || ReferenceEquals(src, _lastSource)) return false;
            _lastSource = src;

            FrameStats f = SnitchCore.LatestFrame;
            SectionRow[] rows = src.ToArray();
            _history.Add(new Sample
            {
                T = now, FrameMs = f.MeanMs, P95Ms = f.P95Ms, Gc0 = f.Gc0Per1000, MinFps = f.MinFps, Rows = rows,
            });
            for (int k = 0; k < rows.Length; k++)
            {
                Identity.Learn(rows[k].Group);
                string key = KeyOf(rows[k]);
                if (!_seen.TryGetValue(key, out Seen s)) _seen[key] = s = new Seen();
                s.Row = rows[k];
                s.LastT = now;
            }

            double cutoff = now - HistorySeconds;
            int drop = 0;
            while (drop < _history.Count - 1 && _history[drop].T < cutoff) drop++;
            if (drop > 0) _history.RemoveRange(0, drop);
            return true;
        }

        /// <summary>Frame figures over the window; zeros with no history. With no sample inside the window the
        /// newest one stands.</summary>
        internal static FrameWindow Frame(double windowSeconds)
        {
            var w = new FrameWindow();
            if (_history.Count == 0) return w;
            double from = Clock - windowSeconds;
            int n = 0;
            double minFps = double.MaxValue;
            for (int i = 0; i < _history.Count; i++)
            {
                Sample s = _history[i];
                if (s.T < from) continue;
                w.MeanMs += s.FrameMs;
                w.P95Ms += s.P95Ms;
                w.Gc0Per1000 += s.Gc0;
                if (s.MinFps < minFps) minFps = s.MinFps;
                n++;
            }
            if (n == 0)
            {
                Sample s = _history[_history.Count - 1];
                return new FrameWindow { MeanMs = s.FrameMs, P95Ms = s.P95Ms, Gc0Per1000 = s.Gc0, MinFps = s.MinFps };
            }
            w.MeanMs /= n;
            w.P95Ms /= n;
            w.Gc0Per1000 /= n;
            w.MinFps = minFps;
            return w;
        }

        /// <summary>Mean frame time over the window, 0 with no history.</summary>
        internal static double FrameMs(double windowSeconds) => Frame(windowSeconds).MeanMs;

        /// <summary>Ranked riders for a window. Riders that reported in the window are ranked by mean ms; DNF riders
        /// follow, the most recently silent first.</summary>
        internal static List<Rider> Rank(double windowSeconds)
        {
            var result = new List<Rider>(32);
            if (_history.Count == 0) return result;

            double clock = Clock;
            double from = clock - windowSeconds;
            var acc = new Dictionary<string, Acc>(32);

            for (int i = 0; i < _history.Count; i++)
            {
                Sample s = _history[i];
                if (s.T < from) continue;
                for (int k = 0; k < s.Rows.Length; k++)
                {
                    SectionRow r = s.Rows[k];
                    string key = KeyOf(r);
                    if (!acc.TryGetValue(key, out Acc a)) acc[key] = a = new Acc();
                    a.Sum += r.MsPerFrame;
                    a.N++;
                    if (r.MaxMs > a.Max) a.Max = r.MaxMs;
                    a.CallSum += r.Calls;
                }
            }

            foreach (KeyValuePair<string, Seen> kv in _seen)
            {
                Seen seen = kv.Value;
                double silent = Math.Max(0.0, clock - seen.LastT);
                bool dnf = seen.LastT < clock && silent > DnfAfterSeconds;
                acc.TryGetValue(kv.Key, out Acc a);
                // Neither reporting in the window nor retired yet: not on the table for this window.
                if ((a == null || a.N == 0) && !dnf) continue;
                bool live = !dnf && a != null && a.N > 0;
                result.Add(new Rider
                {
                    Key = kv.Key,
                    Group = seen.Row.Group,
                    Label = seen.Row.Label,
                    Ms = live ? a.Sum / a.N : 0.0,
                    MaxMs = live ? a.Max : 0.0,
                    Calls = live ? a.CallSum / a.N : 0.0,
                    Dnf = dnf,
                    SilentFor = silent,
                });
            }

            result.Sort((x, y) =>
            {
                if (x.Dnf != y.Dnf) return x.Dnf ? 1 : -1;
                if (x.Dnf) return x.SilentFor.CompareTo(y.SilentFor) != 0
                    ? x.SilentFor.CompareTo(y.SilentFor) : string.CompareOrdinal(x.Key, y.Key);
                int c = y.Ms.CompareTo(x.Ms);
                return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
            });

            double leader = result.Count > 0 && !result[0].Dnf ? result[0].Ms : 0.0;
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Rank = i + 1;
                result[i].Gap = result[i].Dnf ? 0.0 : Math.Max(0.0, leader - result[i].Ms);
            }
            return result;
        }

        /// <summary>The shared bar scale: the first step of the dashboard's ladder that is at least v, else v rounded
        /// up; 1 for zero, negative or non-finite values.</summary>
        internal static double ScaleFor(double v)
        {
            if (!(v > 0.0) || double.IsInfinity(v)) return 1.0;
            for (int i = 0; i < ScaleSteps.Length; i++)
                if (ScaleSteps[i] >= v) return ScaleSteps[i];
            return Math.Ceiling(v);
        }

        private sealed class Acc
        {
            public double Sum, Max, CallSum;
            public int N;
        }
    }
}
