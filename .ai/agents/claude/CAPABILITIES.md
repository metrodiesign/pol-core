# Claude Code — Capabilities

What Claude Code can do in this repo, and which capability backs each part of the
shared workflow. Honest and specific; nothing here is speculative.

> Verify the exact version/feature-flags of this agent before relying on hook/MCP
> support — capabilities below are the ones this project actively uses, but flags
> and availability shift between releases.

## Context window

- Large context (this project runs the 1M-context configuration). You can hold a
  whole feature — requirements, design, tasks, the files it touches — in context
  and implement a cohesive task end-to-end in one pass. Task sizing in this repo
  assumes that (about 5–10 tasks per feature, vertical slices, not micro-steps).
- Because the window is large, prefer a fresh session per cohesive task (reload
  with `@`) over one long session; a clean, focused context is also more accurate.

## Skills and Workflow tools

- **Skill tool** — invokes the project's `spec-*` skills (one per workflow phase)
  and the bundled skills available in the harness. Each spec skill owns its phase
  structure and references `../../shared/*` for standards.
- **Workflow / Task tools** — orchestrate multi-step and multi-task runs (e.g. the
  `pane-loop` orchestrator across panes), and can fan out independent work.

## Subagents

- Spawn fresh-context specialists with their own tool allow-list and model. This
  repo ships three (`spec-architect`, `bug-investigator`, `pbt-runner`) whose
  personas live in `../../roles/*`. A subagent's output returns to the main
  thread, so it is the right tool for adversarial review, audits, and isolated
  investigation without polluting the main context.

## MCP (Model Context Protocol)

- Connect external tool servers over MCP. Available servers in this environment
  include a GitHub server (issues, PRs, sub-issues — used by `spec-sync-github`),
  browser/devtools servers (Chrome DevTools, Playwright) for live UI verification,
  and a docs server (context7) for current library documentation.

## Hooks (in-session enforcement)

- **PreToolUse** — runs before a tool executes and can block it. This repo gates
  Bash with `destructive-guard.sh` and `hook-bypass-guard.sh` (→ `../../bin/check-*`),
  and gates Edit with `spec-edit-guard.sh`. A non-zero exit blocks the call.
- **PostToolUse** — runs after a tool. This repo gates Edit|Write with
  `task-gate.sh` (→ `../../bin/gate-task.sh`) to enforce typecheck/test/Evidence.
- **SessionStart / PreCompact** — inject context (branch, active specs) at session
  start and persist state before compaction. New or edited hook entries take
  effect immediately, mid-session — no restart.

## Browser verification (via MCP)

- Verify UI behavior against a real browser through the Chrome DevTools / Playwright
  MCP servers: navigate, snapshot the accessibility tree, click/type, read console
  and network, run a Lighthouse/a11y audit, resize to the required breakpoints. If
  the project ships a UI, verify it in the project target runtime (see the project
  UI-verify reference), not a dev server, because dev hydration can be unreliable;
  rebuild after edits before re-checking.

## Built-in tooling

- File read/write/edit, Bash (sandboxed, gated by the PreToolUse hooks), Grep/Glob
  search, and web search/fetch. Logic-only checks in `../../bin/` are runnable
  directly when you need to confirm a guard's verdict.
