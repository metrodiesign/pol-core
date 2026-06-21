# Pi Coding Agent (earendil-works) — Agent Adapter

You are the **Pi Coding Agent** working in this repo, on top of the vendor-neutral
`.ai/` operating layer. This file maps the shared workflow and standards onto Pi's
real mechanisms. Behavior, standards, and the spec-driven flow are defined once in
`.ai/shared/` and reused by every agent; this adapter only wires them to Pi.

## Role and best use cases

Pi is a coding agent that adopts an Agent-Skills standard and rich system-prompt
layering, but has **no pre-tool hook in its core** (interception is extension-only)
and deliberately no built-in subagents. Best fit here:

- Focused, single-context implementation of a cohesive task, with tests.
- Spec-driven phases driven from `../../workflows/*` exposed as Pi skills.
- Work where you are disciplined about running the shared guard yourself before any
  risky shell command, since the core will not block it for you.

## What to read first

Read these before doing any work, in this order:

1. `../../shared/PROJECT_CONTEXT.md` — what the product is and why
2. `../../shared/ARCHITECTURE.md` — folder layout, naming, organization
3. `../../shared/CODING_STANDARDS.md` — the stack you MUST prefer, hard constraints
4. `../../shared/TASK_PROTOCOL.md` — how a task flows end-to-end, the prohibitions
5. `../../shared/EARS.md` — the mandatory requirement notation
6. `../../shared/REVIEW_PROTOCOL.md` — how review/audit is run
7. `../../shared/TESTING_PROTOCOL.md` — what gets tested and where
8. `../../shared/SECURITY_RULES.md` — secrets, destructive ops, branch protection
9. `../../shared/OUTPUT_FORMATS.md` — the shape of every artifact you emit
10. `../../shared/CONTEXT_MANAGEMENT.md` — when to persist state
11. `../../shared/AGENT_HANDOFF_PROTOCOL.md` — how to hand off to the next session
12. `../../shared/LESSONS.md` — promoted process lessons (read every session)
13. `../../shared/stack/nextjs.md` — stack-specific lessons (when touching matching files)

Also read `../../README.md` for the system map. **The repo-root `AGENTS.md` is
auto-loaded by Pi**, together with its `SYSTEM.md` / `APPEND_SYSTEM.md` system-prompt
layers. The root `AGENTS.md` is the neutral front door: read `.ai/shared/*`, adopt
`.ai/roles/*`, and enforce through git + CI + `.ai/bin/*`. Treat it as already in
context.

## Mechanism wiring

These point to the live config that the project commits (created and maintained by
the team — not by this adapter doc). Verify each is present and current before
relying on it.

- **Entry / auto-load** — Pi reads `AGENTS.md` plus `SYSTEM.md` / `APPEND_SYSTEM.md`.
  The root `AGENTS.md` is your entry point; do not duplicate its content here.
- **Pre-tool guard** — Pi's **core has no pre-tool hook**. There is no automatic gate
  on shell commands. You therefore depend on two things:
  1. The Tier 1 floor: committed git hooks (`core.hooksPath`) + CI gate every commit
     and PR for every agent. This is the real, unbypassable enforcement for Pi.
  2. Self-discipline: **before any potentially destructive bash command, run
     `../../bin/check-destructive.sh '<cmd>'` yourself** (and `../../bin/check-bypass.sh`
     when relevant) and obey a non-zero exit. The check engine accepts the command
     as `$1` or on stdin and exits 2 to signal "block". It blocks `rm` recursive+force
     (every spelling, incl. inside `sh -c`/`eval`), `git reset --hard`, `git clean -f`,
     `find -delete`, force pushes, direct push/commit on `main`/`develop`, and the SQL
     Destructive-Ops set (`DROP TABLE`/`DROP DATABASE`, `TRUNCATE`, `dropdb`, `DELETE
     FROM` with no `WHERE`). Do not run the risky command if the check blocks it.
- **spec-* skills** — Pi auto-reads `.agents/skills/` (Agent Skills standard), so the
  same `.agents/skills/spec-*/SKILL.md` set that serves Codex and OpenCode works in Pi
  too — no Pi-specific copy. The skill bodies route to the single source
  (`../../workflows/*` + `.claude/skills/spec-*/SKILL.md`); invoke `/skills`,
  `$spec-design`, or rely on implicit triggering. Use these instead of improvising the
  phase structure.
- **Subagents / personas** — Pi has no built-in subagents. Adopt the personas from
  `../../roles/*` (`spec-architect`, `bug-investigator`, `pbt-runner`) inline via
  `APPEND_SYSTEM.md` or a dedicated persona skill when you need a fresh-context
  reviewer or investigator stance. This is the floor-only equivalent of Codex's
  `.codex/agents/*.toml` and OpenCode's `.opencode/agents/*` native subagents.
- **Task-gate** — Pi has no PostToolUse / `file.edited` hook, so there is no native,
  in-session task-gate. The gate is enforced by the Tier 1 floor only: the committed
  `pre-commit` git hook + CI run the same typecheck / test / `Evidence:` checks that
  `../../bin/gate-task.sh` carries, blocking a `[x]` flip that is not green at commit
  and PR. Optionally run `../../bin/gate-task.sh` yourself before marking a task done.
- **MCP / browser-verify** — not applicable: Pi does not host MCP servers in this
  setup, so the chrome-devtools browser-verify recipe is not available natively.
  Verify UI changes manually or defer browser-verify to a Codex/OpenCode session.

## How you work a task

1. **Receive a task brief** shaped by `../../templates/task-brief-template.md`.
2. **Produce an implementation plan** via `../../templates/implementation-plan-template.md`
   before editing, for any non-trivial task. Confirm assumptions; do not pick
   silently among interpretations.
3. **Implement** a whole cohesive task end-to-end (it may span many files),
   including its tests. Run the shared guard yourself before risky shell steps. Mark
   `- [x]` in `tasks.md` and state which REQ IDs are now satisfied. Pause at task
   boundaries for review.
4. **Report changes** via `../../templates/review-report-template.md` (for review)
   and record a changelog entry via `../../templates/changelog-entry-template.md`.
5. **Create a handoff note** via `../../templates/handoff-note-template.md` before
   ending the session, capturing the active task ID, decisions and rationale,
   modified files, and exact build/test/run commands.

## What NOT to do

(From `../../shared/TASK_PROTOCOL.md` and `SECURITY_RULES.md`.)

- Do not jump to implementation for a non-trivial feature — specs come first, in
  order, with approval gates after each artifact (unless `/spec-quick`).
- Do not push directly to `main` or `develop`; every change goes through a PR.
- Do not force push.
- Do not commit any secret (API key, token, password, private key, connection
  string, credential file); do not hardcode credentials; do not log sensitive data.
- Do not run destructive commands (`rm -rf`, `git reset --hard`, `git clean -fd`,
  `DROP TABLE`/`DROP DATABASE`, `TRUNCATE`, `dropdb`, or `DELETE FROM` without a
  `WHERE`). With no core hook, this is on you — run `../../bin/check-destructive.sh
  '<cmd>'` first and obey it. That engine blocks exactly the set above (a `DELETE ...
  WHERE ...` passes); `git checkout`/`restore` and `git branch -D` are an
  intentionally-unblocked gap, so be extra careful with those yourself.
- Do not add a new dependency without reviewing license + maintenance and getting
  approval; always commit the lock file; never pin floating (`*`/`latest`) on prod.
- Do not edit `app/` outside your assigned task, do not change `scripts/` logic,
  and do not touch the `.ai/bin/` guard regex (security-critical).
- Do not commit `.only` / `.skip` left in tests; do not let coverage drop below
  threshold; do not merge across a failing CI check.

## Capabilities and limitations (honest, generic)

- **Capabilities** — code generation and editing; shell tool use; spec-* skills
  auto-read from `.agents/skills/` (Agent-Skills standard, same set as Codex/OpenCode);
  layered system prompt (`SYSTEM.md` / `APPEND_SYSTEM.md`) for personas and standing
  instructions; auto-loaded `AGENTS.md`.
- **Limitations** — **no core pre-tool hook**, so destructive-command interception
  is advisory (you run `../../bin/check-*` by hand); a hard gate would require a Pi
  extension (`pi.on("tool_call")`) — a follow-up, not in place today. **No native
  task-gate** either (floor-only via git + CI). No built-in subagents (personas are
  adopted inline). **No MCP**, so browser-verify is not available natively. Output may
  be buffered, so a quiet run is not necessarily stuck — check disk state, not the
  terminal; untracked files are invisible to `git diff --stat`, so cross-check
  `git status`; no persistent memory beyond what is written to disk.
- The durable enforcement floor is Tier 1 (committed git hooks via `core.hooksPath`
  + CI), which gates every agent and human at commit and PR. For Pi this is the
  primary safety net, with the manual `.ai/bin/check-*` run as the in-session layer.

> Verify the exact version/feature-flags of this agent before relying on hook/MCP
> support.
