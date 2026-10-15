# Project lenses — plan-critic

Project-specific extension of the AI Workflow plugin's `plan-critic` skill.
The skill reads this file on every critique and gives the areas below
explicit attention while applying its four methods. **This file is owned by
the project.** These are not a checklist; they direct extra attention.

## Lenses

When applying the four methods, give explicit attention to:

- **Thread boundary** — IGCS calls arrive on ReShade's render thread, hooks
  and framework ticks on the game thread. Plans that read or write game state
  "from the session" without saying which thread does it usually race.
- **What the add-on believes** — the add-on cannot be told anything mid-stack.
  Any plan that changes, aborts or rescales the camera must explain what the
  shader then assumes, or the result is ghosting that looks like a focus bug.
- **Game patches** — signatures and vtable indices break on patch day. Plans
  adding a hook need a graceful "unavailable" path, not a load failure.
- **Things the plugin cannot control** — Parallax DoF's own settings, ReShade
  presets and V-Sync are outside the plugin. Plans that assume they can be read
  or written need evidence, not inference.
- **In-game-only verification** — most behaviour cannot be tested off the
  game. Plans should say which acceptance criteria are harness-tested and
  which need a manual in-game check, and keep testable logic out of hook code.
