# Snitch

**A performance profiler for Schedule I** - measure what's actually slow, then make it fast.

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/snitch](https://support.doodesch.de/snitch).

> Snitch measures the **cost** and **state** of NPCs, trash, quests, and - through a tiny no-op API built on
> [S1API](https://github.com/ifBars/S1API) - any other mod's systems. Its in-game panel lives in the
> **[Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline)** overlay (each mod gets its own panel),
> alongside a combined log timeline and a live **[web dashboard](https://snitch.doodesch.de)** so you can see
> frame times, section costs, and entity-state distributions in real time.

![Version](https://img.shields.io/badge/version-1.6.1-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-orange)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.x-green)
![S1API](https://img.shields.io/badge/S1API-required-purple)
![Status](https://img.shields.io/badge/status-stable-brightgreen)

**[Live dashboard](https://snitch.doodesch.de)** · **[Documentation](https://docs.doodesch.de/mods/snitch/)** · **[Modder example](https://github.com/DooDesch-Mods/ScheduleOne-SnitchExample)** · **[Dashboard source](https://github.com/DooDesch-Mods/ScheduleOne-SnitchWeb)** · **[Support](https://support.doodesch.de/snitch)**

## Features

- **Frame time** distribution + fps + GC pressure - the load-bearing, build-independent truth.
- **Section costs** - time named code sections (yours via the API, or vanilla hot paths like `NPCMovement.Update`).
- **Unattributed time** - the part of a frame that ran inside no section at all, reported as its own line. When a frame is far worse than the median and the sections did not grow with it, Snitch says so instead of implying the rest is fine.
- **Harmony patch timing** - opt-in wrapping of every other mod's prefixes, postfixes and finalizers, so per-frame cost that lives in a patch on a vanilla method shows up under the mod that owns it.
- **State distributions** - NPCs by movement/visibility, trash by physics state, quests by state, and your own.
- **Per-mod panels** - any mod that reports data gets its own panel (counters, state, text, action buttons, toggles) in the [Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline) overlay and the web dashboard.
- **Log timeline** - a combined, chronological view of every mod's log output, with per-mod filtering.
- **Ablation A/B** - toggle a subsystem off and measure the real frame-time delta (the causal "total cost").
- **[Live web dashboard](https://snitch.doodesch.de)** - opens straight to your local game over WebSocket; your telemetry never leaves your PC.
- **Phone remote** - scan a QR (in the dashboard or the in-game Snitch panel) to drive the profiler from your phone: live FPS, Start/Stop/Reset, and every mod's actions and toggles. Works on the same Wi-Fi or across networks, end-to-end encrypted.
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
| `WrapModPatches` | `false` | Also time other mods' Harmony patches. Off by default: wrapping hundreds of patch methods costs, and a failed patch breaks its target for later patchers. |
| `SpikeFactor` | `1.5` | How much worse than the window median a frame has to be to count as a bad frame in the unattributed report. |
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
- `snitch unattributed` - how much of the frame no section explains, and how much of the bad frames it accounts for.
- `snitch patches on` / `off` / `list` - time every other mod's Harmony prefixes, postfixes and finalizers.
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
surface, and the **[Modder API documentation page](https://docs.doodesch.de/mods/snitch/guides/modder-api/)**.

## When the numbers do not add up

Snitch used to report only what it happened to wrap, and silence read exactly like "this mod is cheap".

It is not. A mod's most expensive per-frame work does not have to live in `OnUpdate`. It can sit in a Harmony
postfix on a vanilla method the engine calls every frame, in a coroutine, in an event handler, or in a Unity
message on a `MonoBehaviour` the mod added - none of which any section covers.

So every frame Snitch now also records what the frame cost against what the sections accounted for, and reports
the difference as **`(unattributed)`** - a row in the section list, a line in the report, a field on the
dashboard. Most of it is the game itself and always will be. The number that points at a mod is the bad-frame
split:

```
snitch unattributed
unattributed: 4.12 ms/frame of a 6.53 ms frame (63%) - inside no section at all.
  sections account for 2.41 ms/frame; the worst single frame left 18.90 ms unexplained.
  bad frames: 7 of 120 above 1.5x the 6.10 ms median; on those, 8.32 ms of the 9.10 ms excess (91%) is unexplained.
  -> run 'snitch patches on'.
```

When a frame is far worse than the median and the sections did not grow with it, the extra milliseconds are
running somewhere the profiler does not reach - and that is a fact worth acting on, not a gap to fill in later.

### `snitch patches on`

The usual hiding place is a Harmony patch, so `snitch patches on` wraps every prefix, postfix and finalizer that
belongs to a loaded mod and times it like a lifecycle method. `snitch patches list` shows what was wrapped, what
it costs and which methods it patches; `snitch patches status` names every patch that was left alone and why.

It is **off by default**, for three reasons that are all cost:

- Wrapping hundreds of patch methods is not free, and a profiler that changes what it measures is worse than
  useless. The unattributed line stays truthful with this off, and is what tells you to switch it on.
- A Harmony patch that fails leaves its target broken for every later patcher. That belongs to a deliberate act.
- It is a snapshot of Harmony's patch table. Run it again after a mod that patches late has loaded.

Wrapped patches carry the wrapper's own overhead, the same as the vanilla probes.

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
