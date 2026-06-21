# AGENTS.md

The neutral front door for every coding agent on this repo (Codex, OpenCode, Pi all
auto-load this file. Claude Code's equivalent front door is `CLAUDE.md`, which bootstraps
the same `.ai/shared/*` read order — Claude does not auto-load this file). Read this, then your adapter.

## What this repo is

A spec-driven development framework. One line of truth, full context here:
`.ai/shared/PROJECT_CONTEXT.md`.

## Read order (do this before you act)

Read `.ai/shared/` in this order — it is the single source of truth, shared by all agents:

1. `PROJECT_CONTEXT.md` — what this product is and why
2. `ARCHITECTURE.md` — file organization and patterns
3. `CODING_STANDARDS.md` — the stack you MUST prefer
4. `TASK_PROTOCOL.md` — how work flows (spec-first, the approval gates)
5. `EARS.md` — requirement notation (mandatory for requirements)
6. `REVIEW_PROTOCOL.md`, `TESTING_PROTOCOL.md`, `SECURITY_RULES.md`
7. `LESSONS.md` — hard-won process lessons; do not repeat them

## Find your adapter

You are one of these agents — open your adapter next:

- `.ai/agents/codex/AGENT.md`
- `.ai/agents/opencode/AGENT.md`
- `.ai/agents/pi/AGENT.md`
- `.ai/agents/claude/AGENT.md`

Your adapter tells you how your harness wires up roles (`.ai/roles/`), workflows
(`.ai/workflows/`), and the guard hooks for your tool.

The spec-* workflow is also available as Agent Skills under `.agents/skills/spec-*`
(auto-read by Codex, OpenCode and Pi); invoke `/skills` or `$spec-design`. The skill
bodies route to the same single source — do not improvise the phase structure.

## Enforcement floor (you cannot opt out)

Two tiers apply to every agent and human, regardless of harness:

- **Git hooks** — enable once per clone: `git config core.hooksPath .githooks`
  (`pre-commit` runs the secret scan + Evidence check; `pre-push` blocks direct
  pushes to `main`/`develop` and force pushes).
- **CI** — `.github/workflows/ci.yml` runs typecheck, tests, a full-tree secret scan,
  and spec-trace (every REQ must be covered) on every PR targeting `develop` (and pushes
  to `develop`). A failing check blocks merge.

If your harness lacks a pre-tool hook (e.g. Pi), run the checks yourself before any
risky bash: `.ai/bin/check-destructive.sh '<cmd>'` and `.ai/bin/check-bypass.sh '<cmd>'`
(exit 2 = blocked).

## Golden rules

- **Spec first.** No code before requirements -> design -> tasks. Honor the approval gates.
- **Minimal change.** Touch only what the task needs; match existing conventions.
- **Tests are part of the task.** Implement a task end-to-end with its tests, green
  before you mark it done, with an `Evidence:` block.
- **Hand off cleanly.** Leave durable state in the spec files; fill the handoff note
  (`.ai/templates/handoff-note-template.md`) before you stop.
