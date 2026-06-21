# Claude Code — Agent Adapter

You are **Claude Code** working in this repo. This file is the Claude-specific
adapter on top of the vendor-neutral `.ai/` operating layer. The behavior, the
project standards, and the spec-driven workflow are defined once in `.ai/shared/`
and reused by every agent; this file only tells you which **real Claude
mechanisms** map to that shared layer.

## What to read first (in this order)

Read the shared canonical sources before doing any work. They are the durable
source of truth; this conversation is temporary working memory.

1. `../../shared/PROJECT_CONTEXT.md` — what the product is and why (Thai verbatim)
2. `../../shared/ARCHITECTURE.md` — folder layout, naming, file organization
3. `../../shared/CODING_STANDARDS.md` — the stack you MUST prefer, hard constraints
4. `../../shared/TASK_PROTOCOL.md` — how a task flows end-to-end, the prohibitions
5. `../../shared/EARS.md` — the mandatory requirement notation
6. `../../shared/REVIEW_PROTOCOL.md` — how review/audit is run
7. `../../shared/TESTING_PROTOCOL.md` — what gets tested and where
8. `../../shared/SECURITY_RULES.md` — secrets, destructive ops, branch protection
9. `../../shared/OUTPUT_FORMATS.md` — the shape of every artifact you emit
10. `../../shared/CONTEXT_MANAGEMENT.md` — when to persist state, when to clear
11. `../../shared/AGENT_HANDOFF_PROTOCOL.md` — how a session hands off to the next
12. `../../shared/LESSONS.md` — promoted process lessons (read every session)
13. `../../shared/stack/` — optional stack-specific profiles; read a `<stack>.md` here when one is present (none bundled by default — see its README)

Then read `../../README.md` for the system map and the per-agent entry points.

Note: Claude additionally auto-loads `CLAUDE.md` (the project constitution, now a
slim adapter pointing here) and `.claude/rules/*` (pointer stubs into
`.ai/shared/`) on every turn. You do not need to open the stubs — read the
canonical `../../shared/*` directly.

## Use the real Claude mechanisms

These are not documentation — they are live, committed Claude configuration. Each
one wraps a neutral source so the same behavior runs under any agent.

### Skills (`.claude/skills/spec-*`)

The spec-driven workflow is exposed as Claude skills, one per phase. Invoke them
with the Skill tool (or the matching `/spec-*` slash command):

- `spec-new`, `spec-requirements`, `spec-analyze`, `spec-design`, `spec-tasks`
- `spec-implement`, `spec-bugfix`, `spec-pbt`, `spec-quick`
- `spec-sync-github`, `spec-retro`

Each `SKILL.md` references the canonical standards in `../../shared/*`. Do not
re-derive the phase structure — the skill owns it. Respect the approval gates in
the constitution: stop after each artifact and wait for explicit approval, except
under `/spec-quick`.

### Subagents (`.claude/agents/*` wrapping `../../roles/*`)

The fresh-context specialists are Claude subagents. Each `.claude/agents/<name>.md`
keeps only the Claude-specific frontmatter (`tools`, `model`, `description`) and
adopts the vendor-neutral persona body from `../../roles/<name>.md`:

- `spec-architect` → `../../roles/spec-architect.md` (adversarial design/requirements review)
- `bug-investigator` → `../../roles/bug-investigator.md` (root-cause only, never fixes)
- `pbt-runner` → `../../roles/pbt-runner.md` (property-based tests from EARS)

Spawn a subagent for any task that benefits from an independent, fresh context —
review, audit, or isolated investigation.

### Hooks (`.claude/hooks/*` → `../../bin/*`)

Enforcement is committed as Claude hooks in `.claude/settings.json`. The hook
scripts are thin adapters that extract the command/file from the hook payload and
delegate to the single-source check engine in `../../bin/`:

- PreToolUse(Bash) → `destructive-guard.sh` → `../../bin/check-destructive.sh`
- PreToolUse(Bash) → `hook-bypass-guard.sh` → `../../bin/check-bypass.sh`
- PostToolUse(Edit|Write) → `task-gate.sh` → `../../bin/gate-task.sh`
- PreToolUse(Edit) → `spec-edit-guard.sh` (advisory, Claude-only)
- PreCompact → `precompact-persist.sh` (advisory, Claude-only)

A blocked command exits non-zero with the rule it violated. Do not try to bypass a
guard (the bypass guard exists precisely to catch that). When a hook blocks a
compound command, the whole command is killed — re-check which parts already ran.

### Command (`.claude/commands/pane-loop.md`)

The `pane-loop` orchestrator drives `/spec-implement → /spec-retro → /clear`
across iTerm panes. Default is all-in-one (every task in one session) for coupled
features; split per pane only for independent work or to isolate accuracy.

## How you produce work

- Receive a task brief shaped by `../../templates/task-brief-template.md`.
- Produce an implementation plan via `../../templates/implementation-plan-template.md`
  before editing, for any non-trivial task.
- Implement a whole cohesive task end-to-end (it may span many files), including
  its tests; then mark `- [x]` in `tasks.md` and state which REQ IDs are satisfied.
  Pause for review at task boundaries.
- Report changes via `../../templates/review-report-template.md` when reviewing,
  and record a changelog entry via `../../templates/changelog-entry-template.md`.
- Produce handoffs via `../../templates/handoff-note-template.md` before `/clear`
  or compaction, so the next session can resume with full state.

## Enforcement floor (shared with every agent)

Even with Claude's harness hooks, the durable gates are Tier 1: committed git
hooks (`.githooks/` via `core.hooksPath`) and CI. They catch secrets, branch/
force-push violations, typecheck/test failures, and spec REQ-coverage on every
commit/push (git hooks) and every develop-targeted PR (CI, per `ci.yml` `branches:
[develop]`) — for every agent and every human. Treat git + CI as the real floor;
the Claude hooks are an early, in-session convenience on top of it.

## Capabilities and limitations

See `CAPABILITIES.md` and `LIMITATIONS.md` in this directory.
