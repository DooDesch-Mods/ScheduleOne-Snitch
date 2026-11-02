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
    /// Bib 0 is the engine's "(unattributed)" row; every other bib is the 1-based position of the name in the
    /// ordinal, case-insensitive sort of the known set. The colour is a hash of the lower-cased name, so it does not
    /// move when another mod joins.
    /// </summary>
    internal static class Identity
    {
        internal const string UnattributedColor = "#5B6573";

        private static readonly string[] Team =
        {
            "#0F8B8D", "#6A4C93", "#E07A1F", "#2F6FDE", "#B5179E", "#8C5A3C", "#3A86A8", "#6B8E23",
        };

        private static readonly Dictionary<string, int> _bibs = new Dictionary<string, int>(16);
        private static string _setKey = "";

        internal static bool IsUnattributed(string name)
            => string.Equals(name, Attribution.RowLabel, StringComparison.OrdinalIgnoreCase);

        /// <summary>Rebuild the bib table from the known names when the set changed. Names are the panel ids plus the
        /// groups of the sections seen so far; the unattributed row is not part of the set.</summary>
        internal static void Refresh(IEnumerable<string> names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>(16);
            foreach (string n in names)
            {
                if (string.IsNullOrEmpty(n) || IsUnattributed(n)) continue;
                if (seen.Add(n)) list.Add(n.ToLowerInvariant());
            }
            list.Sort(StringComparer.OrdinalIgnoreCase.Compare);

            string key = string.Join("\u0001", list);
            if (key == _setKey) return;
            _setKey = key;
            _bibs.Clear();
            for (int i = 0; i < list.Count; i++) _bibs[list[i]] = i + 1;
        }

        internal static int Bib(string name)
        {
            if (IsUnattributed(name)) return 0;
            return !string.IsNullOrEmpty(name) && _bibs.TryGetValue(name.ToLowerInvariant(), out int b) ? b : 0;
        }

        internal static string Color(string name)
        {
            if (IsUnattributed(name) || string.IsNullOrEmpty(name)) return UnattributedColor;
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

    /// <summary>
    /// The race table behind the overlay. The engine keeps one fixed 120-frame window, so the 10 s / 30 s / 2 min
    /// choice is made here, from a history of every poll. Each section is a rider keyed by group and label. Riders
    /// are ranked by mean ms per frame over the window, and a rider that has not reported for a few seconds stays on
    /// the table as DNF with the time since its last report. The clock is the time of the newest sample, so stopping
    /// the sampler freezes the table instead of retiring everyone.
    /// </summary>
    internal static class Standings
    {
        internal const double HistorySeconds = 125.0;
        internal const double DnfAfterSeconds = 3.0;

        private sealed class Sample
        {
            public double T;
            public double FrameMs;
            public SectionRow[] Rows;
        }

        private static readonly List<Sample> _history = new List<Sample>(600);
        private static object _lastSource;

        /// <summary>The newest sample's time; 0 until the first poll arrived.</summary>
        internal static double Clock => _history.Count == 0 ? 0.0 : _history[_history.Count - 1].T;
        internal static int Count => _history.Count;

        /// <summary>Take a sample when the engine published a new poll. Returns true when one was added.</summary>
        internal static bool Pull(double now)
        {
            List<SectionRow> src = SnitchCore.LatestSections;
            if (src == null || src.Count == 0 || ReferenceEquals(src, _lastSource)) return false;
            _lastSource = src;

            _history.Add(new Sample { T = now, FrameMs = SnitchCore.LatestFrame.MeanMs, Rows = src.ToArray() });
            double cutoff = now - HistorySeconds;
            int drop = 0;
            while (drop < _history.Count - 1 && _history[drop].T < cutoff) drop++;
            if (drop > 0) _history.RemoveRange(0, drop);
            return true;
        }

        /// <summary>Every group name seen in the kept history.</summary>
        internal static IEnumerable<string> Groups()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _history.Count; i++)
            {
                SectionRow[] rows = _history[i].Rows;
                for (int k = 0; k < rows.Length; k++)
                    if (seen.Add(rows[k].Group ?? "")) yield return rows[k].Group ?? "";
            }
        }

        /// <summary>Mean frame time over the window, 0 with no history.</summary>
        internal static double FrameMs(double windowSeconds)
        {
            if (_history.Count == 0) return 0.0;
            double from = Clock - windowSeconds, sum = 0.0;
            int n = 0;
            for (int i = 0; i < _history.Count; i++)
                if (_history[i].T >= from) { sum += _history[i].FrameMs; n++; }
            return n == 0 ? _history[_history.Count - 1].FrameMs : sum / n;
        }

        /// <summary>Ranked riders for a window. Riders that reported at all are ranked by mean ms, active ones
        /// first; DNF riders follow, ordered by their last mean.</summary>
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
                bool inWindow = s.T >= from;
                for (int k = 0; k < s.Rows.Length; k++)
                {
                    SectionRow r = s.Rows[k];
                    string key = (r.Group ?? "") + "\u0001" + (r.Label ?? "");
                    if (!acc.TryGetValue(key, out Acc a)) { a = new Acc { Row = r }; acc[key] = a; }
                    a.LastT = s.T;
                    a.LastRow = r;
                    if (inWindow)
                    {
                        a.Sum += r.MsPerFrame;
                        a.N++;
                        if (r.MaxMs > a.Max) a.Max = r.MaxMs;
                        a.CallSum += r.Calls;
                    }
                }
            }

            foreach (KeyValuePair<string, Acc> kv in acc)
            {
                Acc a = kv.Value;
                double silent = clock - a.LastT;
                bool dnf = silent > DnfAfterSeconds;
                // A rider that last reported before the window opened has no sample in it; its last value stands.
                result.Add(new Rider
                {
                    Key = kv.Key,
                    Group = a.LastRow.Group,
                    Label = a.LastRow.Label,
                    Ms = a.N > 0 ? a.Sum / a.N : a.LastRow.MsPerFrame,
                    MaxMs = a.N > 0 ? a.Max : a.LastRow.MaxMs,
                    Calls = a.N > 0 ? a.CallSum / a.N : a.LastRow.Calls,
                    Dnf = dnf,
                    SilentFor = silent,
                });
            }

            result.Sort((x, y) =>
            {
                if (x.Dnf != y.Dnf) return x.Dnf ? 1 : -1;
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

        /// <summary>The shared bar scale: the leader's ms rounded up to 1, 2, 5 or 10 times a power of ten.</summary>
        internal static double NiceCeil(double v)
        {
            if (v <= 0.0 || double.IsNaN(v) || double.IsInfinity(v)) return 1.0;
            double e = Math.Pow(10.0, Math.Floor(Math.Log10(v)));
            double f = v / e;
            double nice = f <= 1.0 ? 1.0 : f <= 2.0 ? 2.0 : f <= 5.0 ? 5.0 : 10.0;
            return nice * e;
        }

        private sealed class Acc
        {
            public SectionRow Row;
            public SectionRow LastRow;
            public double Sum, Max, CallSum, LastT;
            public int N;
        }
    }
}
