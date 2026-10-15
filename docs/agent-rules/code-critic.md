# Project rules — code-critic

Project-specific extension of the AI Workflow plugin's `code-critic` skill.
The skill reads this file on every review — ad-hoc and in the `/feature`
workflow — and applies each rule at the severity it states, alongside its
base standards. **This file is owned by the project**; add a rule here every
time an agent produces bad output the base standards didn't catch — or,
better, encode it as a build-enforced test.

Guidance for good rules:

- One bullet per rule, each stating the rule AND its severity
  (`FAIL` / `NEEDS_DECISION`).
- Name the exact symbol/file/pattern, so the reviewer can grep for it.
- State the failure mode, not just the prohibition.
- Say whether the rule is diff-scoped or repo-wide.

**Build-enforced rules.** The IGCS ABI (export names, return types, bool width,
calling convention) is enforced by `tests/IgcsBridgeHarness`. A diff that
weakens or removes a harness check is `FAIL`.

## Rules

- **No work in constructors** — constructors assign fields only; hooks,
  signature scans, file I/O and service calls belong in an explicit
  initialize step. Otherwise plugin load can deadlock or fail half-initialised
  (see commit `571684a`). `FAIL`, diff-scoped.
- **No exception escapes a game hook detour** — every detour body is wrapped
  in try/catch with throttled logging, as in `CameraController.UpdateDetour`.
  Otherwise a fault crashes the game. `FAIL`, diff-scoped.
- **Cross-thread state goes through a lock** — IGCS callbacks run on ReShade's
  render thread; game-struct writes happen only on the game thread. Writing
  game memory from an IGCS callback, or sharing fields without the owning
  class's lock, is `FAIL`.
- **Add-on steps stay 1:1** — `ScreenshotSession.MoveMultishot` must not scale
  or mirror the add-on's step; the shader reprojects assuming the camera moved
  exactly as asked. Any scale factor is `FAIL`.
- **No logging under the camera lock** — log writes can stall the game thread's
  camera update; log after releasing locks. `FAIL`, diff-scoped.
- **Comments explain why, not what** — match the existing density; a new
  non-obvious decision without a reason comment is `NEEDS_DECISION`.

## Privacy anchors

- **Sensitive categories**: none — the plugin handles no personal data.
- **Public surfaces**: none — no network surface; the only outward interface
  is the IGCS export table and the add-on buffer, both camera data only.
- **Identifier exemptions**: n/a.
- **Privacy tests**: none needed.

## Checklist

- [ ] Constructors only assign fields (if applicable)
- [ ] Every new or changed detour catches all exceptions and throttles logging (if applicable)
- [ ] Render-thread code records intent only; game writes stay on the game thread (if applicable)
- [ ] Add-on steps applied 1:1 with no scale or mirror (if applicable)
- [ ] No log calls while holding the camera lock (if applicable)
- [ ] Non-obvious decisions carry a why-comment
