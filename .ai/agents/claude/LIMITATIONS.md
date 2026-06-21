# Claude Code — Limitations

Honest limitations and failure modes, distilled from `../../shared/LESSONS.md`
(promoted from real retrospectives in this repo). These are the traps that have
actually cost time here. Read them before trusting an in-session signal.

> Verify the exact version/feature-flags of this agent before relying on hook/MCP
> support — hook semantics and tool behavior change between releases.

## Hooks fire live, mid-session

- A new or edited PreToolUse hook entry takes effect **immediately**, not at the
  next session start. The first victim of a freshly added guard can be your own
  next command. When testing a guard against a destructive string, write the test
  to a `/tmp` file and run it — do not inline the destructive string as an argument
  (the live guard will block your test harness).
- A PreToolUse block (exit 2) kills the **entire compound command**, not just the
  offending part. Setup bundled in the same `&&` chain (chmod, mkdir, cp) does not
  run silently. Split setup out from anything that might be blocked, and after a
  block, re-check which parts of the chain actually ran (e.g. confirm file mode in
  the index with `git ls-files -s` before committing a script).
- A guard/regex hook is not trustworthy until it passes an adversarial test suite
  (bypass cases + false-positive cases, run as real stdin JSON). A regex that
  passes the author's own cases still gets defeated by fresh-context attacks
  (indented `rm`, `/bin/rm`, `xargs`, split `git clean` flags, combined short
  flags, `hooksPath` override) and false-positives across commands.

## Headless buffering is not "stuck"

- A headless `claude -p` run buffers its output until the work finishes. A blank
  pane and low CPU do **not** mean it is hung. Before killing it, check progress on
  disk (`git status`, new files, installed dependencies) — not the pane. Also,
  `--permission-mode acceptEdits` covers Edit only, not Bash; setup/scaffold steps
  stall silently on a permission prompt in headless mode unless you pass
  `--dangerously-skip-permissions` or an explicit `--allowedTools`.

## Untracked files are invisible to `git diff --stat`

- `git diff --stat` / `--name-only` do **not** show files in an untracked path
  (a whole new source folder can be `??`). A retro or handoff that gathers
  "Files Modified" from git stat alone will silently drop real code (you see only
  tracked spec/docs). Always cross-check `git status` (which shows `??`) and recall
  what you changed this session.

## Filesystem is ground truth, not the log or the checkboxes

- `tasks.md` checkboxes can contradict the git log (a commit claims a task is done
  while the box is still `[ ]`), and a retrospective-only commit (docs with no
  code) makes a branch look like progress when it is empty. Before starting, trust
  the **filesystem**: list the source folder and locate the project manifest to
  confirm artifacts exist. Do not skip or re-do work based on a report that the
  code does not back up.

## After a failure, read the whole output before retrying

- Read the full output before firing again. After editing a script, **Read it back
  to confirm the change** (especially `cd`/path lines) before running. The same
  error twice is a stop signal — go back to plan mode, do not improvise. Re-firing
  a background command blind, without reading the previous result, repeats the same
  mistake.

## Tool/output filtering can hide the real signal

- The `rtk` hook rewrites command output into a summary ("Errors: 1 | Warnings: 0")
  that can hide the real log. To see raw output of a long-running server (the
  project production-serve command), run it through `rtk proxy <production-serve cmd>`
  so you see the true "Ready"/error lines.

## Subagent / workflow verdicts are unreliable when a verifier dies

- A verifier that dies mid-run (spend-limit, terminal error) returns a null verdict;
  a partition like `f.verdict?.isReal` reads that as falsy and silently buckets it
  as "rejected", mixed with genuinely refuted findings. Before trusting
  rejected/confirmed counts, count `<failures>` against the number of verifiers that
  should have run, and re-verify any finding whose verifier died by hand against the
  real file. A missing verdict means "undecided", not "rejected".

## Cost: read the ledger, do not recompute

- Real cost is read from the ledger `.cost.total_cost_usd` only. Recomputing from
  the transcript overcounts (1.6–3.7x). The statusline already computes cost; do not
  re-derive it.

## Browser verification needs the project target runtime

- Dev-mode rendering can be unreliable. If the project ships a UI, verify it in the
  project target runtime (see the project UI-verify reference) rather than dev mode,
  and rebuild after edits before re-checking — otherwise you verify stale output.

## General

- No persistent state across sessions beyond what is written to disk. Anything in
  this conversation that is not in `.ai/specs/<feature>/` (active task ID,
  decisions and rationale, modified files, exact build/test/run commands) is lost
  on `/clear` or compaction. Persist it first; never clear or compact in the middle
  of an unfinished task whose state lives only in the conversation.
- Switching model/effort mid-session (and `/compact`) invalidates the prefix cache;
  set them once at session start.
- Knowledge has a training cutoff — for current library docs use the docs MCP, not
  memory.
