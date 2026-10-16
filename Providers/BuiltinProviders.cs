using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Trash;     // TrashManager, TrashItem
using Il2CppScheduleOne.Quests;    // Quest, EQuestState
using Snitch.Registries;

namespace Snitch.Providers
{
    /// <summary>
    /// Providers poll a few times a second, so a member that breaks would warn on every poll. Each failure site
    /// reports its first exception once instead; the provider keeps counting what it can still read.
    /// </summary>
    internal static class ProviderLog
    {
        private static readonly HashSet<string> _reported = new HashSet<string>();

        internal static void Once(string site, Exception e)
        {
            if (!_reported.Add(site)) return;
            Core.Log?.Warning($"[snitch] {site} failed ({e.GetType().Name}: {e.Message}); later failures here are not logged again.");
        }
    }

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
            int total = 0, moving = 0, idle = 0, cantMove = 0, hidden = 0, unconscious = 0;
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
                        try { npc = reg[i]; } catch (Exception e) { ProviderLog.Once("Vanilla.NPCs registry read", e); continue; }
                        if (npc == null) continue;
                        try { if (!npc.IsConscious) unconscious++; } catch (Exception e) { ProviderLog.Once("Vanilla.NPCs IsConscious", e); }
                        try { if (!npc.isVisible) hidden++; } catch (Exception e) { ProviderLog.Once("Vanilla.NPCs isVisible", e); }
                        // NPCMovement has no pause flag since 0.4.7; CanMove is false while ragdolled, in a building
                        // or in a vehicle, which is the state "paused" used to cover.
                        bool blocked = false, m = false;
                        try { var mv = npc.Movement; if (mv != null) { blocked = !mv.CanMove; m = mv.IsMoving; } }
                        catch (Exception e) { ProviderLog.Once("Vanilla.NPCs movement", e); }
                        if (blocked) cantMove++; else if (m) moving++; else idle++;
                    }
                }
            }
            catch (Exception e) { ProviderLog.Once("Vanilla.NPCs", e); }
            _snap.Total = total;
            _snap.Add("moving", moving).Add("idle", idle).Add("can't move", cantMove)
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
                var list = tm._trashItems;   // 0.4.7 turned the public trashItems into this private field
                if (list != null)
                {
                    int n = list.Count;
                    total = n;
                    int cap = n < Cap ? n : Cap;
                    for (int i = 0; i < cap; i++)
                    {
                        TrashItem it;
                        try { it = list[i]; } catch (Exception e) { ProviderLog.Once("Vanilla.Trash list read", e); continue; }
                        if (it == null) continue;
                        try
                        {
                            var rb = it._rigidbody;   // 0.4.7 dropped the public Rigidbody property
                            if (rb == null || rb.isKinematic) kinematic++;
                            else if (rb.IsSleeping()) sleeping++;
                            else awake++;
                        }
                        catch (Exception e) { ProviderLog.Once("Vanilla.Trash rigidbody", e); }
                    }
                    if (n > Cap) _snap.Title = "Trash (states sampled, first 8000)";
                }
            }
            catch (Exception e) { ProviderLog.Once("Vanilla.Trash", e); }
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
                        try { q = quests[i]; } catch (Exception e) { ProviderLog.Once("Vanilla.Quests list read", e); continue; }
                        if (q == null) continue;
                        EQuestState st;
                        try { st = q.State; } catch (Exception e) { ProviderLog.Once("Vanilla.Quests State", e); continue; }
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
            catch (Exception e) { ProviderLog.Once("Vanilla.Quests", e); }
            _snap.Total = total;
            _snap.Add("active", active).Add("inactive", inactive).Add("completed", completed)
                 .Add("failed", failed).Add("expired", expired).Add("cancelled", cancelled);
            return _snap;
        }
    }
}
