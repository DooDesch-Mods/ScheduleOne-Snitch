using System;
using Il2CppScheduleOne.Trash;     // TrashManager, TrashItem
using Il2CppScheduleOne.Quests;    // Quest, EQuestState
using Snitch.Registries;

namespace Snitch.Providers
{
    /// <summary>
    /// Built-in state providers for vanilla systems. They are ordinary <see cref="IStateProvider"/>s registered
    /// the same way a modder registers one (dogfooding the API). All are READ-ONLY, defensive (per-element
    /// try/catch), and reuse a cached snapshot so polling allocates nothing in steady state. Each self-disables
    /// gracefully (empty/annotated snapshot) when its system is absent. Member names verified against the
    /// decompiled IL2CPP source.
    /// </summary>
    internal sealed class NpcStateProvider : IStateProvider
    {
        public string Id => "Vanilla.NPCs";
        private readonly StateSnapshot _snap = new StateSnapshot();

        public StateSnapshot Poll()
        {
            _snap.Clear();
            _snap.Title = "NPCs";
            int total = 0, moving = 0, idle = 0, paused = 0, hidden = 0, unconscious = 0;
            try
            {
                var reg = NPCManager.NPCRegistry;
                if (reg != null)
                {
                    int n = reg.Count;
                    total = n;
                    for (int i = 0; i < n; i++)
                    {
                        NPC npc;
                        try { npc = reg[i]; } catch { continue; }
                        if (npc == null) continue;
                        try { if (!npc.IsConscious) unconscious++; } catch { }
                        try { if (!npc.isVisible) hidden++; } catch { }
                        bool p = false, m = false;
                        try { var mv = npc.Movement; if (mv != null) { p = mv.IsPaused; m = mv.IsMoving; } } catch { }
                        if (p) paused++; else if (m) moving++; else idle++;
                    }
                }
            }
            catch { }
            _snap.Total = total;
            _snap.Add("moving", moving).Add("idle", idle).Add("paused", paused)
                 .Add("hidden", hidden).Add("unconscious", unconscious);
            return _snap;
        }
    }

    internal sealed class TrashStateProvider : IStateProvider
    {
        public string Id => "Vanilla.Trash";
        private const int Cap = 8000;
        private readonly StateSnapshot _snap = new StateSnapshot();

        public StateSnapshot Poll()
        {
            _snap.Clear();
            _snap.Title = "Trash";
            int total = 0, awake = 0, sleeping = 0, kinematic = 0;
            try
            {
                TrashManager tm = NetworkSingleton<TrashManager>.Instance;
                if (tm == null) { _snap.Title = "Trash (no manager)"; return _snap; }
                var list = tm.trashItems;
                if (list != null)
                {
                    int n = list.Count;
                    total = n;
                    int cap = n < Cap ? n : Cap;
                    for (int i = 0; i < cap; i++)
                    {
                        TrashItem it;
                        try { it = list[i]; } catch { continue; }
                        if (it == null) continue;
                        try
                        {
                            var rb = it.Rigidbody;
                            if (rb == null || rb.isKinematic) kinematic++;
                            else if (rb.IsSleeping()) sleeping++;
                            else awake++;
                        }
                        catch { }
                    }
                    if (n > Cap) _snap.Title = "Trash (states sampled, first 8000)";
                }
            }
            catch { }
            _snap.Total = total;
            _snap.Add("awake", awake).Add("sleeping", sleeping).Add("kinematic", kinematic);
            return _snap;
        }
    }

    internal sealed class QuestStateProvider : IStateProvider
    {
        public string Id => "Vanilla.Quests";
        private readonly StateSnapshot _snap = new StateSnapshot();

        public StateSnapshot Poll()
        {
            _snap.Clear();
            _snap.Title = "Quests";
            int total = 0, inactive = 0, active = 0, completed = 0, failed = 0, expired = 0, cancelled = 0;
            try
            {
                var quests = Quest.Quests;
                if (quests != null)
                {
                    int n = quests.Count;
                    total = n;
                    for (int i = 0; i < n; i++)
                    {
                        Quest q;
                        try { q = quests[i]; } catch { continue; }
                        if (q == null) continue;
                        EQuestState st;
                        try { st = q.State; } catch { continue; }
                        switch (st)
                        {
                            case EQuestState.Inactive: inactive++; break;
                            case EQuestState.Active: active++; break;
                            case EQuestState.Completed: completed++; break;
                            case EQuestState.Failed: failed++; break;
                            case EQuestState.Expired: expired++; break;
                            case EQuestState.Cancelled: cancelled++; break;
                        }
                    }
                }
            }
            catch { }
            _snap.Total = total;
            _snap.Add("active", active).Add("inactive", inactive).Add("completed", completed)
                 .Add("failed", failed).Add("expired", expired).Add("cancelled", cancelled);
            return _snap;
        }
    }
}
