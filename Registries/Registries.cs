using System;
using System.Collections.Generic;

namespace Snitch.Registries
{
    /// <summary>A small, ordered name->count distribution returned by a state provider. Reused per provider
    /// (Clear + Add) so polling allocates nothing in steady state.</summary>
    internal sealed class StateSnapshot
    {
        public string Id;       // provider id (set by the registry on poll), e.g. "Vanilla.NPCs"
        public string Title;
        public int Total;
        public readonly List<StateBucket> Buckets = new List<StateBucket>(16);

        public StateSnapshot Add(string name, int count) { Buckets.Add(new StateBucket(name, count)); return this; }
        public void Clear() { Total = 0; Buckets.Clear(); }

        /// <summary>If Total wasn't set explicitly, derive it from the buckets.</summary>
        public int EffectiveTotal()
        {
            if (Total != 0) return Total;
            int s = 0; for (int i = 0; i < Buckets.Count; i++) s += Buckets[i].Count; return s;
        }
    }

    internal readonly struct StateBucket
    {
        public readonly string Name;
        public readonly int Count;
        public StateBucket(string name, int count) { Name = name; Count = count; }
    }

    internal interface IStateProvider { string Id { get; } StateSnapshot Poll(); }
    internal interface ICounterSource { string Id { get; } string Unit { get; } double Read(); }

    internal struct CounterRow { public string Id; public string Unit; public double Value; public string State; }

    /// <summary>Registry of entity/state-distribution providers - built-ins and modder-registered alike. Polled
    /// at low Hz by SnitchCore. Re-registering an id replaces it (deterministic).</summary>
    internal static class StateRegistry
    {
        private static readonly List<IStateProvider> _providers = new List<IStateProvider>(16);

        internal static void Register(IStateProvider p)
        {
            if (p == null) return;
            Unregister(p.Id);
            _providers.Add(p);
        }

        internal static void RegisterDelegate(string id, Func<StateSnapshot> poll)
        {
            if (string.IsNullOrEmpty(id) || poll == null) return;
            Register(new DelegateStateProvider(id, poll));
        }

        internal static void Unregister(string id)
        {
            for (int i = _providers.Count - 1; i >= 0; i--)
                if (_providers[i].Id == id) _providers.RemoveAt(i);
        }

        internal static void Clear() => _providers.Clear();
        internal static int Count => _providers.Count;

        /// <summary>Poll every provider on the main thread. Each provider's failure is isolated.</summary>
        internal static List<StateSnapshot> PollAll()
        {
            var list = new List<StateSnapshot>(_providers.Count);
            for (int i = 0; i < _providers.Count; i++)
            {
                try
                {
                    StateSnapshot s = _providers[i].Poll();
                    if (s != null) { s.Id = _providers[i].Id; list.Add(s); }
                }
                catch (Exception e) { Core.Log?.Warning($"[Snitch] state provider '{_providers[i].Id}' threw: {e.Message}"); }
            }
            return list;
        }

        private sealed class DelegateStateProvider : IStateProvider
        {
            private readonly Func<StateSnapshot> _poll;
            public string Id { get; }
            public DelegateStateProvider(string id, Func<StateSnapshot> poll) { Id = id; _poll = poll; }
            public StateSnapshot Poll() => _poll();
        }
    }

    /// <summary>Registry of numeric gauges (pull model) - built-ins and modder-registered alike.</summary>
    internal static class CounterRegistry
    {
        private static readonly List<ICounterSource> _sources = new List<ICounterSource>(16);

        internal static void Register(ICounterSource c)
        {
            if (c == null) return;
            Unregister(c.Id);
            _sources.Add(c);
        }

        internal static void RegisterDelegate(string id, Func<double> read, string unit)
        {
            if (string.IsNullOrEmpty(id) || read == null) return;
            Register(new DelegateCounter(id, read, unit ?? ""));
        }

        internal static void Unregister(string id)
        {
            for (int i = _sources.Count - 1; i >= 0; i--)
                if (_sources[i].Id == id) _sources.RemoveAt(i);
        }

        internal static void Clear() => _sources.Clear();
        internal static int Count => _sources.Count;

        internal static List<CounterRow> ReadAll()
        {
            var list = new List<CounterRow>(_sources.Count);
            for (int i = 0; i < _sources.Count; i++)
            {
                var row = new CounterRow { Id = _sources[i].Id, Unit = _sources[i].Unit, State = "OK" };
                try { row.Value = _sources[i].Read(); }
                catch (Exception e) { row.State = "UNAVAILABLE"; Core.Log?.Warning($"[Snitch] counter '{_sources[i].Id}' threw: {e.Message}"); }
                list.Add(row);
            }
            return list;
        }

        private sealed class DelegateCounter : ICounterSource
        {
            private readonly Func<double> _read;
            public string Id { get; }
            public string Unit { get; }
            public DelegateCounter(string id, Func<double> read, string unit) { Id = id; _read = read; Unit = unit; }
            public double Read() => _read();
        }
    }
}
