# Agent Handoff Protocol

> Vendor-neutral. The contract for passing work between agents (Claude -> Codex ->
> OpenCode -> Pi, or one session to the next). Any agent can WRITE a handoff note and
> any agent can RESUME from one.

A handoff note compacts the volatile conversation (Tier 4 in
[CONTEXT_MANAGEMENT.md](CONTEXT_MANAGEMENT.md)) into a durable record so the next agent
starts with everything load-bearing and nothing else. Write one before `/clear`, before
compaction, or whenever you hand off — and **never hand off in the middle of an
unfinished task whose state lives only in the conversation** without writing it down
first.

Fill the template at
[`../templates/handoff-note-template.md`](../templates/handoff-note-template.md). The
schema below is canonical; the template mirrors it.

## Schema

```
# Handoff: <feature / task short title>
> From: <agent/session>   To: <next agent or "any">   Date: <YYYY-MM-DD>

## Task Summary
<what this work is, in 1-3 sentences, with the active spec and the REQ-IDs / F-IDs / B-IDs in scope>

## Current Status
<what is done, what is in progress, what is not started — be specific about the active task ID and how far it got>

## Files Changed
- <path> — <created | edited> — <what changed>
(Include UNTRACKED files: `git diff --stat` does not show them — list from your own session memory + `git status`.)

## Important Decisions
<each architectural/implementation decision made, WITH its rationale — not just what, but why; reference ADRs if any>

## Constraints
<hard limits the next agent must respect: do-not-modify files, scope boundaries, approved-only behaviors, stack rules that bit you>

## Tests Run
- <exact command> -> <observed result>
(Copy the Evidence blocks. State viewport results for browser work. Say what could NOT be run and why.)

## Known Issues
<bugs, flaky checks, deferred items, anything risky or assumed — link to a risk report if one exists>

## Next Recommended Agent
<which agent should pick this up and why — e.g. "spec-architect for a design critique", "any builder for the next task", or a specific harness if a mechanism is needed>

## Next Steps
1. <the very next action, concretely>
2. <then this>
(Start with the exact command to reload context — e.g. read the spec files and run the state script.)
```

## Resuming from a handoff

The receiving agent:

1. Reads the handoff note, then the cited spec files (Tier 3) and the project rules
   (Tier 2) — the note points; the files are the source of truth.
2. **Reconciles against the filesystem before trusting any status.** Checkboxes and git
   log can lie; untracked files do not appear in `git diff --stat`. Confirm what
   actually exists before continuing.
3. Re-runs the recorded test/build commands to establish a known-good baseline.
4. Continues from "Next Steps", honoring every "Constraint", and proceeds per
   [TASK_PROTOCOL.md](TASK_PROTOCOL.md).
