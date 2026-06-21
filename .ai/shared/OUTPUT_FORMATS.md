# Output Formats

> Vendor-neutral. Standard shapes every agent produces, so output is comparable and
> machine-readable across Claude, Codex, OpenCode, and Pi.

Use these shapes. Where a fillable template exists under [`../templates/`](../templates/),
start from it rather than re-deriving the structure. Keep prose concise and
engineering-focused.

## Implementation plan

What you will build before you build it. See
[`../templates/implementation-plan-template.md`](../templates/implementation-plan-template.md).

```
## Goal
<the task, in one sentence, with the REQ-IDs it satisfies>

## Affected files
- <path> — <create | edit> — <why>

## Steps
1. <step> -> verify: <check>
2. <step> -> verify: <check>

## Risks / open questions
- <risk or question>
```

## Code review report

See [REVIEW_PROTOCOL.md](REVIEW_PROTOCOL.md) for the dimensions, and
[`../templates/review-report-template.md`](../templates/review-report-template.md).

```
## Summary
<what changed, what was reviewed against (which REQ-IDs)>

## Critical
## High
## Medium
## Low
## Suggestions
<findings under each header: file:line — dimension — why — concrete fix>

## Verdict
<approve | request changes — blocked while any Critical/High stands>
```

## Bug analysis

Root-cause-first. The defect/expected/unchanged shape lives in the bugfix spec.

```
## Symptom
WHEN <repro: page / viewport / command / input> THEN <defective behavior, measured>

## Root cause
<the actual cause, traced to the file/line — not a guess>

## Fix outline
<the minimal change that addresses the root cause>

## Regression surface
<existing behavior at risk -> becomes B-IDs in the bugfix spec>
```

## Refactor summary

```
## Intent
<what is being made better, and why now>

## Behavior change
none — refactor preserves behavior   (or: <the intended behavior change + its REQ-ID>)

## Files touched
- <path> — <what moved/changed>

## Verification
<tests that were green before AND after — proof behavior is preserved>
```

## Test report

See [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md) for the Evidence block.

```
## Scope
<which REQ-IDs / F-IDs / B-IDs these tests cover>

## Result
- test: `<exact command>` -> <N passed / M failed>
- viewports: 375 OK | 768 OK | 1440 OK   (browser tasks; else `n/a — logic-only`)
- coverage: <if measured, vs threshold>

## Gaps
<anything not covered, and why>
```

## Handoff note

The schema is owned by [AGENT_HANDOFF_PROTOCOL.md](AGENT_HANDOFF_PROTOCOL.md); fill
[`../templates/handoff-note-template.md`](../templates/handoff-note-template.md).

## ADR (Architecture Decision Record)

One decision per record, immutable once accepted.

```
# ADR-<n>: <decision title>
> Status: proposed | accepted | superseded by ADR-<m>
> Date: <YYYY-MM-DD>

## Context
<the forces and constraints>

## Decision
<what we will do>

## Consequences
<trade-offs accepted, what becomes easier/harder>

## Alternatives considered
<options rejected, and why>
```

## Risk report

```
## Risk
<what could go wrong, stated concretely>

## Severity / likelihood
<high | medium | low> / <high | medium | low>

## Impact
<what breaks, who is affected>

## Mitigation / follow-up
<the action that reduces or accepts the risk; owner if known>
```

## Changelog entry

See [`../templates/changelog-entry-template.md`](../templates/changelog-entry-template.md).
Group entries under Added / Changed / Fixed / Removed; reference the version tag.
