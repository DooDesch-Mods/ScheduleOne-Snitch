# Snitch

**A performance profiler for Schedule I** - measure what's actually slow, then make it fast.

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> Snitch measures the **cost** and **state** of NPCs, trash, quests, and - through a tiny no-op API built on
> [S1API](https://github.com/ifBars/S1API) - any other mod's systems. It ships with an in-game HUD and a live
> **web dashboard** so you can see frame times, section costs, and entity-state distributions in real time.

![Version](https://img.shields.io/badge/version-1.0.1-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-orange)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.x-green)
![S1API](https://img.shields.io/badge/S1API-required-purple)
![Status](https://img.shields.io/badge/status-stable-brightgreen)

## Features

- **Frame time** distribution + fps + GC pressure - the load-bearing, build-independent truth.
- **Section costs** - time named code sections (yours via the API, or vanilla hot paths like `NPCMovement.Update`).
- **State distributions** - NPCs by movement/visibility, trash by physics state, quests by state, and your own.
- **Ablation A/B** - toggle a subsystem off and measure the real frame-time delta (the causal "total cost").
- **Live web dashboard** - opens straight to your local game over WebSocket; your telemetry never leaves your PC.
- **Honest** - every number self-certifies; Snitch even reports its own overhead (`Snitch.Self`).
- **Modder API** - a zero-overhead no-op when Snitch isn't installed, so you can ship it with no hard dependency.

## Requirements

| Requirement | Version / Notes |
|---|---|
| Schedule I | IL2CPP build |
| MelonLoader | 0.7.x |
| S1API | `ifBars-S1API_Forked` |

## Installation

**Mod manager (Thunderstore / r2modman):** install Snitch; the dependencies pull in automatically.

**Manual:** install MelonLoader 0.7.x and S1API, then drop `Snitch.dll` into the game's `Mods/` folder.

## Configuration

Settings live in `UserData/MelonPreferences.cfg` under `Snitch_01_Main` (or the in-game Mod Manager UI). The
profiler is idle until you run `snitch start`.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Master switch. OFF = Snitch does nothing. |
| `EnableInMultiplayer` | `true` | Profiling runs locally on every peer; state-mutating levers stay host-only. |
| `AutoStart` | `false` | Begin sampling automatically on world load. |
| `ShowHud` | `false` | Show the on-screen overlay (toggle with `snitch hud` or F6). |
| `PollHz` | `4` | How often state providers + counters are sampled (frame-time is every frame). |
| `ServerEnabled` | `true` | Run the loopback data server for the web dashboard. |
| `ServerPort` | `6140` | The loopback port (127.0.0.1 only). |
| `ServerToken` | `(empty)` | Optional pairing token for the dashboard. |
| `AllowedOrigins` | `https://snitch.doodesch.de` | Web origins allowed to connect from the browser (localhost is always allowed). |

## Usage

Open the in-game console:

- `snitch start` / `snitch stop` - arm / disarm sampling.
- `snitch hud [on|off]` - the on-screen overlay.
- `snitch top` / `snitch sections` / `snitch states` / `snitch counters` - log the current numbers.
- `snitch vanilla on` - attribute CPU cost to vanilla hot paths (e.g. `NPCMovement.Update/FixedUpdate`).
- `snitch ablate <lever>` - measure a subsystem's causal frame cost (built-in `npc` lever; `snitch levers` lists them).
- `snitch report [md|csv|all]` - export to `Mods/Snitch/runs/`.

Or open the **web dashboard** (bundled offline at `http://localhost:6140/`) - it auto-connects and shows
frame times, section costs, and state distributions live.

## For modders

Drop in `Snitch.cs` (or reference `Snitch.Api.dll`) - it's a zero-overhead no-op when Snitch isn't installed:

```csharp
using (Snitch.Api.Snitch.Sample("MyMod.Pathfinding")) { ... }              // time a section
Snitch.Api.Snitch.RegisterCounter("MyMod.QueueLength", () => _q.Count, "items");
Snitch.Api.Snitch.RegisterStateProvider("MyMod.Jobs", () => ...);          // a distribution
Snitch.Api.Snitch.RegisterAblationLever("mymod.fx", off, on);              // a causal A/B lever
```

See the `SnitchExample` mod for the full surface.

## How it works

ProfilerRecorder engine counters are inert in Schedule I's IL2CPP build, so Snitch relies on **frame-time +
GC** as the truth and **self-measured section timing** (Harmony accumulators) for attribution. The web
dashboard is served both from a hosted site and bundled inside the mod for offline use; either way the page
connects straight to `ws://127.0.0.1:6140` so your data stays on your machine.

## Compatibility

Profiling is read-only and safe alongside other mods (it even observes their effects). State-mutating features
(the ablation levers) run host-only in multiplayer. The profiler stays idle until you run `snitch start`.

## Credits

Built by DooDesch on [S1API](https://github.com/ifBars/S1API) by ifBars.

## License

MIT - see [LICENSE.md](LICENSE.md).
