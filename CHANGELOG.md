# Changelog

All notable changes to Snitch are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

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
