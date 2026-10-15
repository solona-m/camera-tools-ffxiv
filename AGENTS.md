# Camera Tools for FFXIV

Guidance for AI coding agents working in this repository. This file
(`AGENTS.md`) is the canonical copy; `CLAUDE.md` imports it via `@AGENTS.md` so
Claude Code (and its sub-agents) load it, while other tools that read
`AGENTS.md` directly still see one source of truth. Keep it lean — as the
instruction count grows, instruction-following quality degrades across *all*
instructions. Deep detail belongs in `docs/` (including `docs/adr/`) and the
code — this file is the map, not the territory.

## Rules — non-negotiable

These apply to **every** agent — the main agent and any sub-agents it
invokes. Nothing enforces them automatically; following them is your
responsibility.

1. **Use the project's canonical commands.** Build, run, stop, and test only
   through the documented commands (see *Commands*). `build/build.ps1` builds
   the native shim before the plugin and fails if the shim is missing from the
   output — do not invoke `msbuild`/`dotnet build` directly. If a needed
   capability has no command, STOP and report the gap (Rule 2) rather than
   substituting raw shell.

2. **If a command or tool fails — or succeeds without doing what it should —
   STOP and report it; never silently work around it.** Give the user the exact
   step (command or tool call) and its full output, then wait. Do **not** retry
   with different flags/tools or skip the step. Surfacing the problem is
   expected, not a failure on your part.

3. **Stay on the branch you were started on.** Commit directly onto the current
   branch. Never create, switch, or check out branches, and never
   `git worktree add` (restoring a file with `git checkout -- <path>` is fine),
   except for the final local merge into `test` described in Rule 4. If you
   believe the branch must change, STOP and ask first. If you start on `main`
   or `test`, STOP and ask before planning.

4. **Finish by merging into `test` locally; never push.** Feature work is
   reviewed and diffed against `test`, not `main`, and ends with a local merge
   into `test`. Never `git push` or open a PR unless the user asks.

5. **Never let an exception escape a game hook.** Detours run inside the game's
   own update path; an unhandled exception there crashes the game. Catch, and
   log at a throttled rate — see `CameraController.UpdateDetour`.

6. **One clean command per step — use the right tool.** Use the `Read` tool for
   file contents (never `cat`/`head`/`tail`/`sed`); search with a single plain
   `grep`/`rg`/`find`. This applies to every command, reading or acting: no
   leading `cd` (pass a path instead), and no `;`/`&&` chaining, `for`-loops, or
   `$(…)` glue. Need several things? Issue separate (parallel) tool calls, not
   one compound command.

## Project Overview

A Dalamud plugin (C#, .NET 10) plus a small native C++ shim that presents FFXIV
to ReShade add-ons as an IGCS camera tool, so accumulation depth-of-field
add-ons — chiefly Marty McFly's iMMERSE Parallax DoF, also Otis_Inf's
IgcsConnector — can drive the camera. The plugin takes the camera over only for
the duration of an add-on screenshot session, publishes camera data into the
add-on's buffer every frame, and optionally freezes bone physics and pauses the
game clock while a stack renders. See `README.md` for the interface and design.

**Before building features, read the product docs** in `docs/product-context/`.
Architecture decision records are in `docs/adr/`.

## Commands

- `./build/build.ps1 -SkipTests` — build (native shim, then plugin)
- `./build/build.ps1` — run all tests (build + IGCS ABI harness)
- `./build/build.ps1` — all checks incl. tests
- `./build/build.ps1 -Deploy` — run: copies the build to Dalamud's devPlugins;
  reload the plugin from Dalamud's dev plugin list (no URL — it runs in-game)
- stop: unload the plugin from Dalamud's plugin list (in-game, manual)
- single test: none — the harness is a single program with no filter; run
  `./build/build.ps1`
- default branch: `main`. Feature reviews diff against `test` and merge into it

## Architecture

```
src/CameraToolsXIV/          Dalamud plugin
  Plugin.cs                  entry point; wires services, framework tick, commands
  Configuration.cs           persisted user settings
  Camera/                    camera read/override via the CameraBase::Update hook; view-matrix basis
  Igcs/                      IGCS session semantics, native shim bridge, add-on buffer publishing
  World/                     session-scoped physics freeze and world (frame-delta) pause
  Ui/                        ImGui main window
src/native/IgcsBridge/       C++ shim exporting the IGCS_* entry points add-ons discover
tests/IgcsBridgeHarness/     console harness exercising the native ABI without the game
build/build.ps1              canonical build/test/deploy script
.github/workflows/           release and testing-release automation
```

Load-bearing constraints: IGCS session calls arrive on ReShade's render thread
and only record intent under a lock; all game writes happen on the game thread.
Add-on steps are applied 1:1 in world units — scaling them breaks the shader's
reprojection (see commit `9faf370`). Constructors do field assignment only;
startup work happens in an explicit initialize step.

## Testing

The only automated test is `tests/IgcsBridgeHarness`, a console program that
loads `IgcsBridge.dll`, registers callbacks like the plugin does, and calls the
exports like an add-on does. It also links in Dalamud-free plugin sources
(`Igcs/ApertureWatch.cs`, `Igcs/ParallaxDofGuidance.cs`) and checks them; keep
such files free of Dalamud usings. `build/build.ps1` runs it after building. Game
behaviour (hooks, camera, freeze) can only be verified in-game via
`./build/build.ps1 -Deploy`.

## Sensitive Areas — the security surface

Changes touching these get extra scrutiny: a mistake here crashes or hangs the
game rather than failing visibly.

- Game hooks: `src/CameraToolsXIV/Camera/CameraController.cs` (CameraBase::Update
  detour, vtable hook), `src/CameraToolsXIV/World/PhysicsFreeze.cs` (signature
  hook), `src/CameraToolsXIV/World/WorldPause.cs` (frame-delta pause)

## Review & Planning Guidance

- Code review guidance: `docs/agent-rules/code-critic.md`
- Planning guidance: `docs/agent-rules/plan-critic.md`
