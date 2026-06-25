using System;
using System.Collections.Generic;

namespace Snitch.Logging
{
    /// <summary>A single captured log line. <see cref="Seq"/> is a monotonic id (ordering + de-dupe on the web side);
    /// <see cref="Time"/> is a short clock stamp for display; <see cref="Lvl"/> is 0=info, 1=warning, 2=error.</summary>
    internal struct LogEntry
    {
        public long Seq;
        public string Ch;
        public int Lvl;
        public string Msg;
        public string Time;
    }

    /// <summary>
    /// Central log capture for the Snitch overlay + dashboard. Holds a per-channel ring buffer (one channel per mod)
    /// AND a single combined timeline (all channels in chronological order) so a dev can read one mod in isolation
    /// or correlate everything on one timeline. Sources: explicit <c>Snitch.Api</c> log calls from mods (the primary
    /// per-mod channel), Snitch's own messages ("Snitch") and console activity ("Console"). The IL2CPP UnityEngine
    /// surface exposes no log-received event here, so engine/third-party output is out of scope - the timeline is the
    /// combined output of the instrumented (DooDesch) mods, which is the goal. Thread-safe (callers may be off-thread).
    /// </summary>
    internal static class LogHub
    {
        private const int PerChannelMax = 400;
        private const int TimelineMax = 1200;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Queue<LogEntry>> _channels = new Dictionary<string, Queue<LogEntry>>();
        private static readonly Queue<LogEntry> _timeline = new Queue<LogEntry>(TimelineMax);
        private static long _seq;

        internal static void Install() { }
        internal static void Uninstall() { }

        /// <summary>Append a line to a channel and the combined timeline. Channel = mod id (e.g. "Siesta").</summary>
        internal static void Write(string channel, int lvl, string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            if (string.IsNullOrEmpty(channel)) channel = "misc";
            if (lvl < 0) lvl = 0; else if (lvl > 2) lvl = 2;

            lock (_lock)
            {
                var e = new LogEntry { Seq = ++_seq, Ch = channel, Lvl = lvl, Msg = msg, Time = DateTime.Now.ToString("HH:mm:ss") };
                if (!_channels.TryGetValue(channel, out Queue<LogEntry> q))
                {
                    q = new Queue<LogEntry>(64);
                    _channels[channel] = q;
                }
                q.Enqueue(e);
                while (q.Count > PerChannelMax) q.Dequeue();

                _timeline.Enqueue(e);
                while (_timeline.Count > TimelineMax) _timeline.Dequeue();
            }
        }

        internal static List<LogEntry> Timeline(int n)
        {
            lock (_lock) { return TakeLast(_timeline, n); }
        }

        internal static List<LogEntry> Channel(string channel, int n)
        {
            lock (_lock)
            {
                return _channels.TryGetValue(channel, out Queue<LogEntry> q) ? TakeLast(q, n) : new List<LogEntry>();
            }
        }

        internal static List<string> Channels()
        {
            lock (_lock) { return new List<string>(_channels.Keys); }
        }

        internal static void Clear()
        {
            lock (_lock) { _channels.Clear(); _timeline.Clear(); }
        }

        private static List<LogEntry> TakeLast(Queue<LogEntry> q, int n)
        {
            if (n <= 0 || n >= q.Count) return new List<LogEntry>(q);
            var all = new List<LogEntry>(q);
            return all.GetRange(all.Count - n, n);
        }
    }
}
