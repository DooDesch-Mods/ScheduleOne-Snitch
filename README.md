# Snitch

**A performance profiler for Schedule I** - measure what's actually slow, then make it fast.

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> Snitch measures the **cost** and **state** of NPCs, trash, quests, and - through a tiny no-op API built on
> [S1API](https://github.com/ifBars/S1API) - any other mod's systems. Its in-game panel lives in the
> **[Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline)** overlay (each mod gets its own panel),
> alongside a combined log timeline and a live **[web dashboard](https://snitch.doodesch.de)** so you can see
> frame times, section costs, and entity-state distributions in real time.

![Version](https://img.shields.io/badge/version-1.3.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-orange)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.x-green)
![S1API](https://img.shields.io/badge/S1API-required-purple)
![Status](https://img.shields.io/badge/status-stable-brightgreen)

**[Live dashboard](https://snitch.doodesch.de)** · **[Wiki / docs](https://github.com/DooDesch-Mods/ScheduleOne-Snitch/wiki)** · **[Modder example](https://github.com/DooDesch-Mods/ScheduleOne-SnitchExample)** · **[Dashboard source](https://github.com/DooDesch-Mods/ScheduleOne-SnitchWeb)** · **[Support](https://support.doodesch.de)**

## Features

- **Frame time** distribution + fps + GC pressure - the load-bearing, build-independent truth.
- **Section costs** - time named code sections (yours via the API, or vanilla hot paths like `NPCMovement.Update`).
- **State distributions** - NPCs by movement/visibility, trash by physics state, quests by state, and your own.
- **Per-mod panels** - any mod that reports data gets its own panel (counters, state, text, action buttons, toggles) in the [Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline) overlay and the web dashboard.
- **Log timeline** - a combined, chronological view of every mod's log output, with per-mod filtering.
- **Ablation A/B** - toggle a subsystem off and measure the real frame-time delta (the causal "total cost").
- **[Live web dashboard](https://snitch.doodesch.de)** - opens straight to your local game over WebSocket; your telemetry never leaves your PC.
- **Honest** - every number self-certifies; Snitch even reports its own overhead (`Snitch.Self`).
- **Modder API** - a zero-overhead no-op when Snitch isn't installed, so you can ship it with no hard dependency.

## Requirements

| Requirement | Version / Notes |
|---|---|
| Schedule I | IL2CPP build |
| MelonLoader | 0.7.x |
| S1API | `ifBars-S1API_Forked` |
| [Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline) | The in-game overlay framework Snitch's panel renders in (auto-installed as a dependency). |

## Installation

**Mod manager (Thunderstore / r2modman):** install Snitch; the dependencies (MelonLoader, S1API, Hotline)
pull in automatically.

**Manual:** install MelonLoader 0.7.x, S1API and Hotline, then drop `Snitch.dll` into the game's `Mods/` folder.

## Configuration

Settings live in `UserData/MelonPreferences.cfg` under `Snitch_01_Main` (or the in-game Mod Manager UI). The
profiler is idle until you run `snitch start`.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Master switch. OFF = Snitch does nothing. |
| `EnableInMultiplayer` | `true` | Profiling runs locally on every peer; state-mutating levers stay host-only. |
| `AutoStart` | `false` | Begin sampling automatically on world load. |
| `PollHz` | `4` | How often state providers + counters are sampled (frame-time is every frame). |
| `ServerEnabled` | `true` | Run the loopback data server for the web dashboard. |
| `ServerPort` | `6140` | The loopback port (127.0.0.1 only). |
| `ServerToken` | `(empty)` | Optional pairing token for the dashboard. |
| `AllowedOrigins` | `https://snitch.doodesch.de` | Web origins allowed to connect from the browser (localhost is always allowed). |

## Usage

The in-game overlay is provided by **[Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline)** - press
**F6** to open it; Snitch's panel (Start/Stop/Reset plus live stats) is inside, alongside every other mod's.

Snitch's own console:

- `snitch start` / `snitch stop` - arm / disarm sampling (or the Start/Stop buttons on Snitch's panel in the Hotline overlay).
- `snitch panels` - list the per-mod panels (toggle their windows from the Hotline overlay).
- `snitch act <id>` / `snitch toggle <id>` / `snitch log [<channel>|all]` - run a panel action, flip a toggle, or read the logs.
- `snitch top` / `snitch sections` / `snitch states` / `snitch counters` - log the current numbers.
- `snitch vanilla on` - attribute CPU cost to vanilla hot paths (e.g. `NPCMovement.Update/FixedUpdate`).
- `snitch ablate <lever>` - measure a subsystem's causal frame cost (built-in `npc` lever; `snitch levers` lists them).
- `snitch report [md|csv|all]` - export to `Mods/Snitch/runs/`.

Or open the **web dashboard** at **[snitch.doodesch.de](https://snitch.doodesch.de)** (or the copy bundled
offline at `http://localhost:6140/`) - it auto-connects and shows frame times, section costs, and state
distributions live.

## For modders

Your mod's per-frame methods (`OnUpdate` etc.) are **auto-timed with zero code** - it just appears as
`<YourMod>.OnUpdate` once Snitch is sampling. To go further, drop in `Snitch.cs` (or reference
`Snitch.Api.dll`) - a zero-overhead no-op when Snitch isn't installed - and either name a class `SnitchProbe`
with a static `Register()` (auto-discovered, no wiring). There you can build your own **panel** - counters,
state, free text, action buttons, toggles and a log channel, shown in the Hotline overlay and the web dashboard:

```csharp
using Snitch.Api;   // Profiler, Panel, StateSnapshot
Panel p = Profiler.RegisterPanel("MyMod", "My Mod");
p.Counter("QueueLength", () => _q.Count, "items");                // a numeric gauge
p.State("Jobs", () => new StateSnapshot { Title = "Jobs" }.Add("running", _r)); // a distribution
p.Action("Flush", () => Flush());                                 // a button (replaces a debug hotkey)
p.Toggle("Verbose", () => _v, x => _v = x);                       // an on/off control
p.Log();                                                          // show this panel's log channel
using (Profiler.Sample("MyMod.Pathfinding")) { ... }              // hand-time a sub-section
```

See the **[SnitchExample](https://github.com/DooDesch-Mods/ScheduleOne-SnitchExample)** mod for the full
surface, and the **[Modder API wiki page](https://github.com/DooDesch-Mods/ScheduleOne-Snitch/wiki/Modder-API)**.

## How it works

ProfilerRecorder engine counters are inert in Schedule I's IL2CPP build, so Snitch relies on **frame-time +
GC** as the truth and **self-measured section timing** (Harmony accumulators) for attribution. The web
dashboard ([snitch.doodesch.de](https://snitch.doodesch.de), source at
[ScheduleOne-SnitchWeb](https://github.com/DooDesch-Mods/ScheduleOne-SnitchWeb)) is served both from that
hosted site and bundled inside the mod for offline use; either way the page connects straight to
`ws://127.0.0.1:6140` so your data stays on your machine.

## Compatibility

Profiling is read-only and safe alongside other mods (it even observes their effects). State-mutating features
(the ablation levers) run host-only in multiplayer. The profiler stays idle until you run `snitch start`.

## Credits

Built by DooDesch on [S1API](https://github.com/ifBars/S1API) by ifBars.

## License

MIT - see [LICENSE.md](LICENSE.md).
