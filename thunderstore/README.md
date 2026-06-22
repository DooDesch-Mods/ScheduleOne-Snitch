# Snitch

**A performance profiler for Schedule I** - find out what's actually slow.

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

Snitch measures the **cost** and **state** of NPCs, trash, quests, and - through a tiny no-op API - any other
mod's systems. It ships with an in-game HUD and a live **web dashboard** so you can see frame times, section
costs, and entity-state distributions in real time, and make your mod (or vanilla gameplay) faster.

![Version](https://img.shields.io/badge/version-1.0.1-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-orange)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.x-green)
![S1API](https://img.shields.io/badge/S1API-required-purple)

## Features

- **Frame time** distribution + fps + GC pressure - the load-bearing, build-independent truth.
- **Section costs** - time named code sections (yours, or vanilla hot paths like `NPCMovement.Update`).
- **State distributions** - NPCs by movement/visibility, trash by physics state, quests by state, + your own.
- **Ablation A/B** - toggle a subsystem off and measure the real frame-time delta (causal "total cost").
- **Live web dashboard** - opens straight to your local game over WebSocket; telemetry never leaves your PC.
- **Honest** - every number self-certifies; Snitch even reports its own overhead.
- **Modder API** - a zero-overhead no-op when Snitch isn't installed; no hard dependency.

## Requirements

| Requirement | Notes |
|---|---|
| MelonLoader | 0.7.x (IL2CPP) |
| S1API | `ifBars-S1API_Forked` |

## Use it

Open the in-game console: `snitch start`, then `snitch hud on`. Or open the web dashboard - it auto-connects
and shows everything live. Console verbs: `start, stop, status, frame, top, sections, states, counters, hud,
vanilla on|off, ablate <lever>, levers, report`. Reports are written to `Mods/Snitch/runs/`.

## For modders

Drop in `Snitch.cs` (or reference `Snitch.Api.dll`):

```csharp
using (Snitch.Api.Snitch.Sample("MyMod.Work")) { ... }                    // time a section
Snitch.Api.Snitch.RegisterCounter("MyMod.Queue", () => _q.Count, "items"); // a gauge
Snitch.Api.Snitch.RegisterStateProvider("MyMod.Jobs", () => ...);          // a distribution
```

## Notes

ProfilerRecorder engine counters are inert in Schedule I's IL2CPP build, so Snitch relies on frame-time + GC
and self-measured section timing. Profiling is local and safe in multiplayer; state-mutating features run
host-only. Everything is idle until you `snitch start`.

## Credits

Built by DooDesch on [S1API](https://github.com/ifBars/S1API).
