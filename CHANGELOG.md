# Changelog

All notable changes to Snitch are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

## [1.6.2] - 2026-08-09

### Fixed

- A mod's debug build no longer hunts for Snitch on every API call when Snitch is absent. It searched
  five folders per call, hundreds of times during one world load.
- Log lines no longer repeat the mod's own name. MelonLoader already puts it in front of every line.

## [1.6.1] - 2026-08-04

### Changed

- `snitch` shows up in the game's command list, so a console autocomplete or a terminal can offer it and
  complete it. It was answered by a patch and registered nowhere, which made it invisible to every tool that
  lists commands. Nothing about running it changes.

## [1.6.0] - 2026-08-04

### Added

- `snitch open` and `snitch close` show and hide the profiler panel without touching the
  keyboard. `snitch open all` and `snitch close all` do the same for the whole overlay.
  Needs Hotline 1.3.0 or newer.

## [1.5.1] - 2026-08-01

### Changed
- Runs on Schedule I 0.4.6f11.
- Needs S1API 3.1.1, up from 3.0.5. Update it along with the mod.

## [1.5.0] - 2026-07-31

Tune a mod's values by dragging them, and open the dashboard without leaving the game.

### Added
- **Sliders in mod panels.** A mod can expose a value as a slider (`Profiler.RegisterSlider`,
  `Panel.Slider`) instead of a number you type into the console. Snitch clamps it to the declared range and
  snaps it to the step before the mod's setter runs, so every write path - dragging in game, the web
  dashboard, the phone remote, `snitch slider` - is bounded identically.
- Sliders render everywhere panels already do: the in-game overlay, the web dashboard and the phone remote.
- `snitch slider <sliderId> [value]` reads or writes one from the console. Omit the value to read it back.
  A slider is a mouse control and cannot be driven by a test harness; this is the scriptable equivalent.
- **Open dashboard** button in the Snitch panel, and `snitch dashboard`. It serves the bundled copy from
  `Mods/Snitch/wwwroot` when one is present, otherwise the hosted build, which connects back to the same
  loopback socket. With the data server off it says so rather than opening a page that cannot connect.

### Fixed
- `snitch hud` and `snitch panel <id>` were still documented after 1.3.0 moved the overlay to Hotline, so
  following the docs printed "unknown". The wiki now matches the commands that exist.

### Requirements
- In-game sliders need [Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline) 1.2.0 or newer, which
  draws them. Without Hotline, Snitch still runs and the sliders remain usable from the console and the
  dashboard.

### Notes
- Control ids are slugified from the label and punctuation is dropped, so two labels differing only in
  punctuation collide and the second replaces the first. Give each control distinct words.

## [1.4.0] - 2026-07-08

Use your phone as a remote for the profiler - scan a QR and start/stop sampling or trigger any mod's actions
without switching back to the browser window.

### Added
- **Phone remote.** A "Connect a phone" QR - both in the web dashboard and in the in-game Snitch panel. Scan it
  and you get a compact remote: the live FPS, Start/Stop/Reset, and every mod's panel actions and toggles.
- Works on the **same Wi-Fi** (the phone connects directly to the game) and **across networks** (the game
  relays through a small pairing service, so it works even over mobile data). On the same network the phone
  switches to the direct local connection on its own.
- **End-to-end encrypted** with a one-time key carried in the QR, so the relay only ever forwards ciphertext -
  your telemetry stays private even over the internet.
- Turn it on from the Snitch panel toggle or with `snitch lan on` (off by default).
- The dashboard now reads a capability list from the connected instance, so it only offers features the running
  build actually supports.

### Requirements
- The in-game QR uses [Hotline](https://github.com/DooDesch-Mods/ScheduleOne-Hotline) 1.1.0 or newer (its panel
  image support). Everything else works without it.

## [1.3.0] - 2026-06-26

The in-game overlay moves to the new Hotline framework, so every mod shares one overlay and one hotkey. The
web dashboard and the profiling itself are unchanged.

### Changed
- The in-game overlay is now provided by Hotline (a dependency). Snitch contributes its profiler panel (frame
  stats, sections, states, Start/Stop/Reset) to the unified Hotline overlay, opened with Hotline's master key
  (default F6).

### Removed
- Snitch's own in-game windowing and its overlay settings (`ShowHud`, `HudFontSize`, `HudX`, `HudY`,
  `WindowLayouts`) and the `snitch hud` / `snitch panel <id>` console verbs - the overlay lives in Hotline now.

### Note
- Without Hotline, Snitch still profiles and serves the web dashboard, but has no in-game overlay. Hotline is
  pulled in automatically as a dependency.

## [1.2.0] - 2026-06-25

The on-screen overlay becomes a small windowing layer, every mod gets its own panel, and there is a combined
log timeline. The modder bridge ABI is unchanged (additive), so existing integrations keep working.

### Added
- Multi-window overlay: the profiler HUD is now an "Overview" window alongside per-mod panels and a log
  "Timeline". Each window is independently shown/hidden, dragged (title bar) and resized (bottom-right grip),
  with a scrollable body and a layout that persists across restarts.
- Per-mod panels: any mod that reports data gets its own toggleable panel of counters, state distributions,
  free text, action buttons, toggles and a log channel - in the overlay and the web dashboard. New modder API:
  the `Profiler.RegisterPanel(...)` fluent builder (`Counter`/`State`/`Text`/`Action`/`Toggle`/`Log`) and
  `Profiler.Log(...)`.
- Log timeline: a combined, chronological view of all channels (each mod plus Snitch and the console), with
  per-mod filtering, both in-game and on the dashboard.
- Arm sampling from the overlay: Start / Stop / Reset buttons in the Overview (no console needed). `F6` toggles
  the overlay and always summons the Overview, so it can't get stuck closed.
- New console commands: `snitch panels`, `snitch panel <id> [on|off|move|size|reset]`, `snitch act <id>`,
  `snitch toggle <id>`, `snitch log [<channel>|all]`.

### Changed
- The on-screen "resize" is now a real window resize (width/height with scrolling); font size is a separate
  setting. Button labels no longer clip descenders.

## [1.1.0] - 2026-06-24

The on-screen HUD is now movable and resizable, and remembers where you put it.

### Added
- HUD position and font size are adjustable and persist across restarts: `snitch hud move <x> <y>`,
  `snitch hud font <n>`, and `snitch hud reset` (back to defaults), plus matching `HudX`, `HudY`, and
  `HudFontSize` settings (sliders in the Mod Manager UI). The overlay auto-fits, so a bigger font makes a
  bigger window.
- Drag the HUD with the mouse: grab the body to move it, or its bottom-right corner to change the font size
  (works while the cursor is free, e.g. with the phone or pause menu open).

### Fixed
- The `F6` hotkey now actually toggles the HUD (it was documented but never wired up).

## [1.0.2] - 2026-06-22

Less code in mods, more profiling for free. No change to the host data/wire protocol or the bridge ABI, so
existing integrations keep working.

### Added
- Auto-instrumentation: while sampling, every other loaded mod's per-frame methods (`OnUpdate`, `OnFixedUpdate`,
  `OnLateUpdate`, `OnGUI`) are timed automatically and shown as `<Mod>.OnUpdate` - per-mod frame cost with zero
  code on the mod's side. Toggle with the `AutoInstrument` preference.
- Zero-wiring registration: a mod's `SnitchProbe.Register()` is now discovered and called by the host
  automatically when sampling starts, so a mod no longer wires a registration call into its `OnInitializeMelon`.

### Changed
- Modder API shim class renamed `Snitch.Api.Snitch` -> `Snitch.Api.Profiler` (drops the `using Prof = ...`
  alias; just `using Snitch.Api;` then `Profiler.Sample(...)`). The bridge contract is unchanged, so previously
  shipped shims still bind and report.
- Cheaper no-op path: the shim's pre-bind host lookup no longer scans every loaded assembly each call.

## [1.0.1] - 2026-06-22

Internal cleanup. No functional or behavioural changes.

### Changed
- Removed leftover development scaffolding: the dev-only verification probes and all internal
  "phase"-process references in code comments, console help, and docs.

## [1.0.0] - 2026-06-22

Initial release. Feature-complete and verified in-game.

### Added
- Frame-time + GC sampler (the load-bearing measurement layer).
- Stopwatch section timer (`Snitch.Sample`), backing both the modder API and vanilla probes.
- Built-in state providers: NPCs (movement/visibility), trash (physics), quests (state).
- Vanilla CPU cost attribution via Harmony accumulators (`snitch vanilla on`) - e.g. `NPCMovement.Update/FixedUpdate`.
- Ablation A/B harness with a stability gate + lever registry (`snitch ablate <lever>`), built-in `npc` lever.
- Local HTTP + WebSocket data server (loopback `:6140`) with CORS/PNA/Origin/token.
- SnitchWeb live dashboard (React + uPlot), bundled offline at `http://localhost:6140/`.
- Zero-overhead modder API (`Snitch.Api`) - copy-in source or referenced DLL, no hard dependency.
- In-game HUD, periodic telemetry, and Markdown/CSV report export.
