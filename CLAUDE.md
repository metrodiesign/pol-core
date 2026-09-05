# Spec-Driven Development Constitution (Claude adapter)

Spec-driven development in this project is OPT-IN, not automatic. Run it only when
I explicitly invoke a `/spec-*` command (or ask for a spec in so many words). For
every other request, do the work directly — do not open a spec, do not propose the
spec workflow, do not treat a missing spec as a blocker.

The behavior, project standards, and the full workflow are defined ONCE in the
vendor-neutral `.ai/` operating layer and reused by every agent. This file is the
thin Claude Code adapter on top of it — it bootstraps the shared layer and maps it
to real Claude mechanisms. It does NOT restate shared prose.

## Read first, every session (the always-on canon)

Before doing any work, read the canonical shared sources — they are the durable
source of truth; this conversation is temporary working memory:

- `.ai/shared/PROJECT_CONTEXT.md` — what the product is and why
- `.ai/shared/CODING_STANDARDS.md` — the standards you MUST follow + hard constraints
- `.ai/shared/ARCHITECTURE.md` — folder layout, naming, file organization
- `.ai/shared/LESSONS.md` — promoted process lessons (read every session)

`.ai/shared/TASK_PROTOCOL.md` (phases, task sizing, Definition of Done,
prohibitions) applies only inside an invoked `/spec-*` run — read it then, not by
default. EARS notation lives in `.ai/shared/EARS.md`; review, testing, security, output, context, and handoff
protocols are the other `.ai/shared/*.md` files — open the one the task needs.

`.claude/rules/*.md` are still auto-loaded every turn by the rules loader, but they
are now pointer stubs routing to these canonical `.ai/shared/*` files. Do not
@-import them here, and do not edit the stubs — read the canonical sources directly
and change knowledge there, once.

## Apply your Claude agent profile

Adopt the Claude-specific adapter and honest self-knowledge in:

- `.ai/agents/claude/AGENT.md` — which real Claude mechanisms map to the shared layer
- `.ai/agents/claude/CAPABILITIES.md`
- `.ai/agents/claude/LIMITATIONS.md`

## The workflow gates (inside an invoked `/spec-*` run only)

Once a `/spec-*` command is running, these gates are non-negotiable for that run.
Outside such a run they do not apply.

Every feature flows through three artifacts under `.ai/specs/<feature-name>/`,
IN ORDER, with an APPROVAL GATE after each (Design-First swaps 1 and 2 — same gates):

  1. requirements.md  — WHAT the system must do (EARS notation)
  2. design.md        — HOW it will be built (architecture)
  3. tasks.md         — discrete, trackable implementation steps

After producing each artifact, STOP and ask me to review before generating the
next. Wait for explicit approval ("approved" / "continue"). The only exception is
`/spec-quick`, which runs all phases without gates. (Full phase detail and the
"approval lives in the file, not the conversation" rule: `.ai/shared/TASK_PROTOCOL.md`.)

## How to run each phase (project slash commands — do not improvise the structure)

  /spec-new <idea>        choose a workflow and ask clarifying questions
  /spec-requirements      generate requirements.md (EARS)
  /spec-analyze           audit requirements for gaps/conflicts before design
  /spec-design            generate design.md
  /spec-tasks             generate tasks.md
  /spec-implement <id|range|all>  implement one or more cohesive tasks, end-to-end
  /spec-bugfix <bug>      root-cause-first bug workflow
  /spec-pbt               extract properties and write property-based tests
  /spec-retro             session retrospective — run at END of session, BEFORE /clear
  /spec-sync-github <feature>  mirror tasks to GitHub Issues (Epic + sub-issues), idempotent

Each `SKILL.md` owns its phase structure and references the canonical `.ai/shared/*`
standards — do not re-derive it.

## Claude-specific mechanisms (live, committed config — see AGENT.md for the full map)

- Subagents (`.claude/agents/*` wrapping `.ai/roles/*`): spawn a fresh-context
  specialist for review, audit, or isolated investigation — `spec-architect`,
  `bug-investigator`, `pbt-runner`.
- Hooks (`.claude/hooks/*` -> `.ai/bin/*`, wired in `.claude/settings.json`):
  enforcement guards. A blocked command exits non-zero with the rule it violated.
  Do NOT try to bypass a guard (the bypass guard catches exactly that). When a hook
  blocks a compound command, the whole command is killed — re-check which parts ran.
- Command (`.claude/commands/pane-loop.md`): drives /spec-implement -> /spec-retro
  -> /clear across iTerm panes.

The durable enforcement floor is Tier 1 — committed git hooks (`.githooks/`) + CI,
which gate every agent and human at commit and PR. The Claude hooks are an early,
in-session convenience on top of that floor.

## Context discipline (Claude-specific — full rules in `.ai/shared/CONTEXT_MANAGEMENT.md`)

- The spec files in `.ai/specs/<feature>/` are the durable source of truth; this
  conversation is temporary working memory. Before I run /clear, or before compaction
  triggers, write the current state — active task ID, decisions + rationale, what's
  done, the next step — into tasks.md / design.md. NEVER clear or compact in the
  middle of an unfinished task whose state lives only in this conversation.
- Prefer a fresh session per cohesive task (reload context by reading the spec with
  `@`) over one long session — a clean, focused context is also more accurate.
- Keep this file lean, but NEVER remove a rule that prevents a real mistake.
  Correctness outranks token savings: if economizing would risk a wrong result, do
  not economize — tell me instead.
