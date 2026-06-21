# Codex (OpenAI) — Agent Adapter

You are **Codex** (OpenAI's coding agent) working in this repo, on top of the
vendor-neutral `.ai/` operating layer. This file maps the shared workflow and
standards onto Codex's real mechanisms. Behavior, standards, and the spec-driven
flow are defined once in `.ai/shared/` and reused by every agent; this adapter only
wires them to Codex.

## Role and best use cases

Codex is a capable general coding agent with strong tool use and a project-local
trusted-config layer (`.codex/`). Best fit here:

- End-to-end implementation of a cohesive task or task range, with tests.
- Codebase-wide edits where Codex's project hooks give a real pre-tool safety gate.
- Spec-driven phases driven from the shared workflows, with subagent personas
  mapped through `.codex/config.toml`.

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
auto-loaded by Codex** (root-down concatenation of `AGENTS.md` files): it is the
neutral front door that tells you to read `.ai/shared/*`, adopt `.ai/roles/*`, and
enforce through git + CI + `.ai/bin/*`. Treat it as already in context.

## Mechanism wiring

These point to the live Codex config that the project commits (in `.codex/` and
`.agents/skills/`, created and maintained by the team — not by this adapter doc).
Every wire routes to a single source; nothing is duplicated. Verify each is present
and current before relying on it.

- **Entry / auto-load** — Codex concatenates `AGENTS.md` from the repo root down to
  the working directory. The root `AGENTS.md` is your entry point; do not duplicate
  its content here.
- **spec-* skills** — the spec workflow is exposed as skills under
  `.agents/skills/spec-*/SKILL.md` (Agent Skills standard: YAML frontmatter `name` +
  `description`, markdown body). Codex auto-reads `$REPO_ROOT/.agents/skills`, so
  `/skills`, `$spec-design`, or implicit triggering all work. The skill bodies do not
  re-state procedure — they route to the single source: `../../workflows/*` for the
  phase structure and `.claude/skills/spec-*/SKILL.md` for the detailed step text. One
  skill set serves Codex, OpenCode and Pi. Codex prompts are **deprecated and
  user-global only** (`~/.codex/prompts`); this repo deliberately ships no
  `.codex/prompts` — skills replace them.
- **Pre-tool guard** — registered in `.codex/config.toml` under `[hooks]` as
  `[[hooks.PreToolUse]]` with matcher `"^Bash$"`, running `.codex/hooks/guard.sh`.
  **Codex discovers hooks from `config.toml` `[hooks]`.** The legacy `.codex/hooks.json`
  was REMOVED (issue #26): contrary to the earlier "inert" assumption, Codex 0.139 loads
  it too — the live `/hooks` panel warned "loading hooks from both .codex/hooks.json and
  .codex/config.toml; prefer a single representation" and double-registered guard.sh /
  task-gate.sh. `config.toml` is now the single source. The guard reads the Codex hook input, extracts the
  command, and delegates to the single-source check engine: `../../bin/check-destructive.sh`
  and `../../bin/check-bypass.sh`. A blocked command stops with the rule it violated.
  The destructive engine now blocks (verified against the live engine, identical exit
  codes between `.ai/bin` and the Claude adapter): `rm` recursive+force in every
  spelling (`\rm`, `"rm"`, `'rm'`, and `rm` inside `sh -c '...'` / `eval '...'`), `git
  reset --hard`, `git clean -f`, `find -delete`, force pushes (incl. `+refspec`,
  `--mirror`, `--all --force`), direct push/commit on `main`/`develop` (incl.
  fully-qualified `HEAD:refs/heads/main`), **and the SQL Destructive-Ops set: `DROP
  TABLE`/`DROP DATABASE`, `TRUNCATE`, `dropdb`, and `DELETE FROM ...` with NO `WHERE`
  (a `DELETE ... WHERE ...` is allowed).** Known intentionally-unblocked gaps (high
  false-positive risk): `git checkout`/`restore`, `git branch -D`, `find -exec` — the
  Tier 1 git hooks + CI are the durable floor for those. The fail-safe trade-off
  holds: destructive-looking content inside a quoted string may over-block, by design.
  (Codex's PreToolUse(Bash) payload is doc-confirmed: `{"tool_name":"Bash","tool_input":
  {"command":"..."},...}` — `tool_input.command` is guard.sh's first jq path; the alt jq
  paths + argv stay as defensive fallbacks. Parsing is regression-tested in
  `.claude/hooks/tests/codex-adapters.test.sh`. See "Hook firing requires trust" below
  for when these hooks actually run.)
- **Task-gate** — registered in `.codex/config.toml` `[hooks]` as `[[hooks.PostToolUse]]`
  with matcher `"^(apply_patch|Bash|Write|Edit)$"`, running `.codex/hooks/task-gate.sh`
  (again `config.toml`; `.codex/hooks.json` removed — see Pre-tool guard). The script extracts the edited file + new
  content from the Codex hook payload and delegates to the single-source gate engine
  `../../bin/gate-task.sh` (`$GATE_FILE` / `$GATE_NEW`). The gate fires only when a
  `.ai/specs/*/tasks.md` checkbox is flipped to `[x]`: green = silent exit 0, red
  (typecheck/test fail or missing/placeholder `Evidence:`) = exit 2 so you fix before
  marking the task done. The Evidence requirement is **per flipped task** (scoped to
  each `[x]` region up to the next checkbox or EOF), not per-file, and rejects
  placeholder Evidence (`TODO`/`TBD`/bare `n/a`); an agent-authored `n/a (<reason>)`
  with a real reason is accepted. All three adapters (Claude Edit+Write, Codex,
  OpenCode) yield an IDENTICAL gate verdict for the same flip. No gate logic lives in
  the adapter — it is the same engine as Claude's via `gate-task.sh`.
- **spec-edit-guard (advisory).** `.codex/hooks/spec-edit-guard.sh`, wired as a second
  `[[hooks.PreToolUse]]` group (matcher `^(apply_patch|Edit)$`) in `.codex/config.toml`,
  WARNS on stderr when an already-approved `requirements.md` is edited while its sibling
  `tasks.md` still has open tasks. It delegates to the single source
  `.ai/bin/check-spec-edit.sh` (the same engine Claude/OpenCode use) and ALWAYS exits 0 —
  it informs, it never blocks. The Bash PreToolUse payload (`tool_input.command`) is
  doc-confirmed; the apply_patch/Edit file-path shape is probed (jq paths + patch-body
  recovery, identical to task-gate) with an argv fallback. Verified by
  `.claude/hooks/tests/spec-edit-guard.test.sh` (issue #29; payload parse, issue #26).
- **Hook firing requires per-machine trust — and does NOT fire in `codex exec` (issue #26).**
  Codex *discovers* project-local `<repo>/.codex/config.toml` hooks, but per the docs they
  load "only when the project `.codex/` layer is trusted," and each command hook must be
  reviewed + trusted (recorded against its hash) via the interactive `/hooks` command, or it
  is skipped. Live-tested here (Codex 0.135 and 0.139): headless `codex exec` did NOT run
  the guard even with a `[projects."<path>"].trust_level="trusted"` override AND
  `--dangerously-bypass-hook-trust` — a `SECRET_GUARD_SKIP=1 git status` ran unblocked all
  four probes. So: (1) **automation via `codex exec` relies on the Tier 1 floor** (git
  pre-commit + CI), not these in-loop hooks; (2) **for interactive Codex**, run `/hooks`
  ONCE in this repo to review + trust `guard.sh`, `task-gate.sh`, `spec-edit-guard.sh` —
  per-machine human setup, like `core.hooksPath`, that cannot be committed. The simulated
  payload tests above prove the adapters enforce correctly once Codex actually invokes them.
  VERIFIED LIVE (2026-06-14, Codex 0.139): after removing the duplicate `.codex/hooks.json`
  and trusting the hooks via `/hooks`, an interactive Codex session BLOCKED
  `SECRET_GUARD_SKIP=1 git status` with the guard's own message — confirming the project
  config.toml hooks register, parse, and fire end-to-end once trusted.
- **Subagents** — native Codex subagents under `.codex/agents/*.toml`
  (`spec-architect`, `bug-investigator`, `pbt-runner`), each a thin `.toml` whose
  `developer_instructions` adopt the persona body from `../../roles/*` (the single
  source — no persona text is copied into the `.toml`). Concurrency is capped by
  `[agents]` in `.codex/config.toml` (`max_threads`, `max_depth`). Invoke via
  `/agent` or "spawn agent". Use them for fresh-context review, root-cause analysis,
  and property-based testing.
- **MCP (browser-verify)** — external tool servers live under `[mcp_servers.*]` in
  `.codex/config.toml`. The browser-verify server (`chrome-devtools`, launched `npx -y
  chrome-devtools-mcp@latest` — confirm package/version) is already wired there and
  enables the browser-verify recipes in
  `.claude/skills/spec-implement/references/browser-verify.md` for Codex. Add other
  MCP servers the same way rather than improvising. (Note: `spec-retro` and
  `spec-sync-github` now ship as vendor-neutral skills in `.agents/skills/`, routing to
  the same authoritative `.claude/skills/*` steps as the other phases; but they lean on
  Claude-specific facilities — `spec-retro` reads Claude's own cost ledger, and
  `spec-sync-github` needs a GitHub MCP server. For Codex, wire a GitHub MCP server in
  `[mcp_servers]` to run the sync; the retro's cost section is Claude-only and should be
  recorded as "cost unavailable" off Claude.)

## How you work a task

1. **Receive a task brief** shaped by `../../templates/task-brief-template.md`.
2. **Produce an implementation plan** via `../../templates/implementation-plan-template.md`
   before editing, for any non-trivial task. Confirm assumptions; do not pick
   silently among interpretations.
3. **Implement** a whole cohesive task end-to-end (it may span many files),
   including its tests. Mark `- [x]` in `tasks.md` and state which REQ IDs are now
   satisfied. Pause at task boundaries for review.
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
  `WHERE`). The guard blocks exactly these (a `DELETE ... WHERE ...` is allowed); do
  not attempt to bypass it. `git checkout`/`restore` and `git branch -D` are an
  intentionally-unblocked gap — the Tier 1 git hooks + CI are the floor there.
- Do not add a new dependency without reviewing license + maintenance and getting
  approval; always commit the lock file; never pin floating (`*`/`latest`) on prod.
- Do not edit `app/` outside your assigned task, do not change `scripts/` logic,
  and do not touch `.codex/` / `.ai/bin/` guard regex (security-critical).
- Do not commit `.only` / `.skip` left in tests; do not let coverage drop below
  threshold; do not merge across a failing CI check.

## Capabilities and limitations (honest, generic)

- **Capabilities** — strong code generation and editing; spec-* skills auto-read from
  `.agents/skills/`; shell tool use gated by the `.codex/` pre-tool hook plus a
  PostToolUse task-gate; native project-local subagents (`.codex/agents/*.toml`)
  capped by `[agents]`; MCP tool servers including browser-verify (chrome-devtools);
  auto-loaded `AGENTS.md` chain for instructions. Good at holding a feature in context
  and implementing it end-to-end.
- **Limitations** — pre-tool hook coverage and input format depend on the Codex
  version; a guard is only as good as its adversarial test pass; output may be
  buffered, so a quiet run is not necessarily stuck — check disk state, not the
  terminal; untracked files are invisible to `git diff --stat`, so cross-check
  `git status`; no persistent memory beyond what is written to disk.
- The durable enforcement floor is Tier 1 (committed git hooks via `core.hooksPath`
  + CI), which gates every agent and human at commit and PR — the `.codex/` hook is
  an in-session convenience on top of it.

> Verify the exact version/feature-flags of this agent before relying on hook/MCP
> support.
