using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Snitch.Vanilla
{
    /// <summary>
    /// Installs Harmony wrappers a few per frame instead of all at once. One Harmony patch costs a few milliseconds
    /// (the target is re-emitted and compiled again), so wrapping a few hundred methods in one go is a stutter of its
    /// own, the very thing the profiler is there to find. Each frame spends at most <see cref="BudgetMs"/> on the
    /// queue, and always at least one job so a slow patch cannot stall the queue. A batch logs once when its last
    /// job ran. The work runs inside Snitch.Self, so its cost stays visible.
    /// </summary>
    internal static class PatchQueue
    {
        internal const double BudgetMs = 2.0;

        private sealed class Batch
        {
            internal string Name;
            internal readonly Queue<Func<bool>> Jobs = new Queue<Func<bool>>();
            internal Action Done;
            internal int Ok, Failed, Frames, LastFrame = -1;
            internal double Ms;
        }

        private static readonly List<Batch> _batches = new List<Batch>();

        internal static int Pending
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _batches.Count; i++) n += _batches[i].Jobs.Count;
                return n;
            }
        }

        /// <summary>Queue one batch. Each job installs one wrapper, logs its own failure and returns whether it
        /// worked; <paramref name="done"/> runs after the last job.</summary>
        internal static void Add(string name, List<Func<bool>> jobs, Action done = null)
        {
            if (jobs == null || jobs.Count == 0)
            {
                Core.Log?.Msg($"[snitch] {name}: nothing new to wrap.");
                done?.Invoke();
                return;
            }
            var b = new Batch { Name = name, Done = done };
            for (int i = 0; i < jobs.Count; i++) b.Jobs.Enqueue(jobs[i]);
            _batches.Add(b);
            Core.Log?.Msg($"[snitch] {name}: {jobs.Count} wrapper(s) queued, installed at up to {BudgetMs:F0} ms per frame.");
        }

        /// <summary>Run queued jobs until this frame's budget is spent. Called once per sampled frame.</summary>
        internal static void Step()
        {
            if (_batches.Count == 0) return;
            int frame = Time.frameCount;
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            int ran = 0;

            while (_batches.Count > 0)
            {
                Batch b = _batches[0];
                if (b.LastFrame != frame) { b.LastFrame = frame; b.Frames++; }
                if (b.Jobs.Count == 0)
                {
                    _batches.RemoveAt(0);
                    Finish(b);
                    continue;
                }
                if (ran > 0 && Stopwatch.GetTimestamp() - start >= budget) break;

                long t0 = Stopwatch.GetTimestamp();
                bool ok;
                try { ok = b.Jobs.Dequeue()(); }
                catch (Exception e)
                {
                    ok = false;
                    Core.Log?.Error($"[snitch] {b.Name}: a wrapper job threw - {e}");
                }
                b.Ms += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ok) b.Ok++; else b.Failed++;
                ran++;
            }
        }

        private static void Finish(Batch b)
        {
            Core.Log?.Msg($"[snitch] {b.Name}: {b.Ok} wrapped, {b.Failed} failed, over {b.Frames} frame(s) "
                        + $"({b.Ms:F0} ms of patching).");
            try { b.Done?.Invoke(); }
            catch (Exception e) { Core.Log?.Error($"[snitch] {b.Name}: the completion step threw - {e}"); }
        }
    }
}
