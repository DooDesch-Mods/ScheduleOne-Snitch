# Changelog

All notable changes to Snitch are documented here. Format based on [Keep a Changelog](https://keepachangelog.com).

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
