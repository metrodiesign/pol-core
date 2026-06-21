# .ai/ — the vendor-neutral operating layer

This directory is the single source of truth for the harness-agnostic KNOWLEDGE,
PROTOCOLS, ROLES and CHECK ENGINE: Claude Code, Codex, OpenCode and Pi all read the
same `shared/` knowledge, adopt the same `roles/`, and are gated by the same
`bin/check-*` engine. The per-harness `.claude/`, `.codex/`, `.opencode/` artifacts
are thin adapters that point back here for all of that.

One deliberate exception to "everything authoritative lives under `.ai/`": the
detailed, step-by-step procedure text for each spec phase currently lives under
`.claude/skills/spec-*/SKILL.md` and is treated as the AUTHORITATIVE phase steps,
SHARED across harnesses. The vendor-neutral `.agents/skills/spec-*/SKILL.md` and the
`.opencode/commands/*` are thin routers that defer to those `.claude/skills/*` steps
(plus `.ai/workflows/*` + `.ai/shared/*`); the step text is not duplicated per harness.
So "single source" holds — each piece of knowledge lives in exactly one place — but
the spec-phase step text is sourced from `.claude/skills/` rather than `.ai/`. Changing
a phase's procedure means editing `.claude/skills/<phase>/SKILL.md`, once.

## System map

```text
.ai/
  README.md            # this file — system map, entry points, golden rules, SETUP
  shared/              # SINGLE SOURCE OF TRUTH: knowledge + protocols (read in order)
  workflows/           # runnable procedures (neutral), reference scripts/ + shared/
  roles/               # vendor-neutral personas (subagent bodies, no frontmatter)
  bin/                 # harness-agnostic check engine (single source for all guards)
  agents/              # per-agent adapters (claude/ codex/ opencode/ pi/)
  templates/           # task-brief / plan / review / handoff / changelog templates
```

| Dir | One line |
|---|---|
| `shared/` | Project context, architecture, coding standards, task/review/testing/security protocols, EARS, lessons |
| `workflows/` | feature-development, bug-fix, code-review, test-generation, frontend-task — each cites `scripts/` |
| `roles/` | spec-architect, bug-investigator, pbt-runner — personas any harness can adopt |
| `bin/` | `check-destructive.sh`, `check-bypass.sh`, `check-secrets.sh`, `gate-task.sh` (exit 2 = block) + `install.sh` |
| `agents/` | One `AGENT.md` per harness explaining its read order and live hook/role wiring |
| `templates/` | Fill-in-the-blank artifacts for briefs, plans, reviews, handoffs, changelog |

**Skills standard** — the spec workflow ships once as Agent Skills under
`/.agents/skills/spec-*/SKILL.md` (frontmatter `name` + `description`, markdown body).
This one set is auto-read by **Codex**, **OpenCode** (which also reads `.claude/skills/`)
and **Pi**; the bodies route to the authoritative phase steps (`workflows/*` +
`.claude/skills/spec-*`) and are never duplicated per harness. Claude reads the same
procedure via `.claude/skills/`. `spec-retro` and `spec-sync-github` now ALSO ship as
vendor-neutral routers (`.agents/skills/spec-retro`, `.agents/skills/spec-sync-github`
and the matching `.opencode/commands/*`) that defer to the same authoritative
`.claude/skills/*` steps. They remain Claude-LEANING — `spec-retro` reads Claude's own
cost ledger (off Claude, record "cost unavailable"), and `spec-sync-github` needs a
GitHub MCP server (or the `gh` CLI) — but the procedure is now reachable from every
harness, not Claude-only.

## Per-agent entry points

| Agent | Auto-loads | spec-* skills | Then read |
|---|---|---|---|
| Claude | `.claude/` (`CLAUDE.md`, `rules/`) | `.claude/skills/spec-*` | `.ai/agents/claude/AGENT.md` |
| Codex | `AGENTS.md` (root) | `.agents/skills/spec-*` | `.ai/agents/codex/AGENT.md` |
| OpenCode | `AGENTS.md` (root) | `.agents/skills/` + `.claude/skills/` | `.ai/agents/opencode/AGENT.md` |
| Pi | `AGENTS.md` (+ `SYSTEM.md` if present) | `.agents/skills/spec-*` | `.ai/agents/pi/AGENT.md` |

All agents read `.ai/shared/*` in the order listed in the root `AGENTS.md` before acting.

## Parity matrix

How each spec-driven capability lands per harness. **native** = the harness provides
it as a first-class mechanism wired to the single source; **floor-only** = no native
mechanism, enforced by the Tier 1 git + CI floor (and self-discipline); **n/a** = not
applicable in this setup. Wiring detail is in each `agents/<harness>/AGENT.md`.

| Capability | Claude | Codex | OpenCode | Pi |
|---|---|---|---|---|
| spec-* workflow as skills | native (`.claude/skills/spec-*`) | native (`.agents/skills/spec-*`) | native (`.agents/skills/` + `.claude/skills/`) | native (`.agents/skills/spec-*`) |
| Slash commands | native (`.claude/commands/`) | via skills (prompts deprecated) | native (`.opencode/commands/spec-*`) | via skills |
| Subagents (fresh-context personas) | native (Task tool -> `.ai/roles/*`) | native (`.codex/agents/*.toml` + `[agents]`) | native (`.opencode/agents/*`) | floor-only (persona via skill / `APPEND_SYSTEM.md`) |
| Pre-tool guard (destructive/bypass) | native (`.claude/` hook -> `.ai/bin/check-*`) | native (`.codex/config.toml` `[hooks].PreToolUse` -> `guard.sh`; needs interactive `/hooks` trust, see issue #26) | native (`.opencode/plugins/ai-guard.js`) | floor-only (run `.ai/bin/check-*` by hand) |
| Task-gate (`[x]` flip = green + Evidence) | native (`.claude/` hook -> `gate-task.sh`) | native (`.codex/config.toml` `[hooks].PostToolUse` -> `task-gate.sh`) | native-ish (`.opencode/plugins/task-gate.js` on `file.edited`, no hard-block) | floor-only (git pre-commit + CI) |
| MCP browser-verify (chrome-devtools) | native (MCP) | native (`.codex/config.toml` `[mcp_servers]`) | native (`opencode.json` `mcp`) | n/a (no MCP host) |

All native task-gate, guard, subagent and skill wiring routes to the same single
source — `.ai/bin/{check-*,gate-task}.sh`, `.ai/roles/*`, `.ai/workflows/*` +
`.claude/skills/*` — so every harness enforces byte-for-byte identical rules.

## Golden rules

- **Spec first** — requirements -> design -> tasks, with approval gates. No code first.
- **Minimal change** — touch only what the task needs; match existing conventions.
- **Tests are part of the task** — green before done, with an `Evidence:` block.
- **Hand off cleanly** — durable state in the spec files; fill the handoff note.

## SETUP (one time per clone)

The framework is stack-agnostic — there is no `package.json`/`npm install` to hang a
`prepare` hook on, so a human wires the Tier 1 local floor once per clone by running:

```sh
./.ai/bin/install.sh        # sets core.hooksPath=.githooks + marks scripts executable
```

After it runs you should have:

```sh
git config core.hooksPath   # -> .githooks
```

An agent cannot do this from the CLI — the bypass guard blocks `core.hooksPath` edits;
the script (not an agent typing the command) performs the wiring. As a manual fallback a
human can also run it directly:

```sh
git config core.hooksPath .githooks
```

This enables `pre-commit` (secret scan + a per-task, scope-aware Evidence check — a
newly-marked `[x]` task must carry its own `Evidence:` line within its own block and
cannot borrow a sibling's) and `pre-push` (blocks direct pushes to `main`/`develop` and
force pushes). CI (`.github/workflows/ci.yml`) is the server-side floor that applies
regardless.

**Codex MCP (Codex users only)** — the browser-verify server is wired in
`.codex/config.toml` under `[mcp_servers.chrome-devtools]` (confirm package/version).
OpenCode reads its MCP straight from `opencode.json`; Pi has no MCP host.

## Related top-level docs (not moved)

- `../PROMPT.md` — the originating prompt / brief for this project.
- `../claude-code-spec-driven-workflow.md` — the long-form spec-driven workflow guide.
