# Review Protocol

> Vendor-neutral. How any agent (or human) reviews a change before merge.
> Canonical source for the review-report shape in [OUTPUT_FORMATS.md](OUTPUT_FORMATS.md).

A review answers one question: **is this change correct, safe, and consistent with the
project, and is that claim backed by evidence?** Review the diff against the spec it
implements (its REQ-IDs / F-IDs / B-IDs), not against your own taste.

## Review dimensions

Walk every dimension that applies to the change. Skipping a dimension is a decision —
state it ("no API surface touched") rather than omitting it silently.

1. **Correctness** — does it do what the cited REQ-IDs require? Check happy path AND
   the `IF ... THEN` error/edge cases. Re-derive any tricky computation by hand.
2. **Security** — secrets, injection, unsafe shell/SQL, destructive ops, authz. Cross-
   check against [SECURITY_RULES.md](SECURITY_RULES.md). A security finding is at least
   High.
3. **Performance** — obvious hot-path regressions, N+1, unbounded loops/allocations,
   needless re-renders. Flag with a measurement or a concrete reason, not a hunch.
4. **Maintainability** — clarity, naming, dead code, over-abstraction, duplication.
   Simplicity-first: 200 lines that could be 50 is a finding.
5. **Architecture consistency** — does it match [ARCHITECTURE.md](ARCHITECTURE.md) and
   the patterns in [CODING_STANDARDS.md](CODING_STANDARDS.md)? Logic in the right
   layer, no banned anti-patterns, tokens/conventions respected.
6. **Type safety** — `strict` honored, no stray `any`, props/data typed, exhaustive
   handling where the type system can enforce it.
7. **Error handling** — every failure path handled per spec; no swallowed errors; no
   error handling for impossible scenarios.
8. **Tests** — do tests actually prove the cited IDs? Do they assert the OBSERVABLE
   behavior (output, computed value, layout) and not implementation detail? Are
   happy + error paths covered? No `.only` / `.skip` committed. See
   [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md).
9. **Backward compatibility** — does it break existing callers, data, saved state, or
   contracts? Any migration needed?
10. **UX (frontend changes)** — responsive at the project breakpoints, interaction
    states (default/hover/focus/active/disabled), accessibility (semantic HTML,
    keyboard, contrast), no horizontal overflow, no empty/placeholder visuals.
11. **API compatibility (backend changes)** — request/response contract, status codes,
    versioning, idempotency. (This project ships no real backend; apply only when a
    change introduces a contract.)

## Evidence over assertion

Trust the diff and the `Evidence:` block, not the summary prose. If a claimed pass has
no recorded command + observed result, treat it as unverified and re-run it. A review
that finds nothing because nothing was checked is not a pass.

## Severity format

Group findings under these headers, highest severity first. Each finding cites the file
and line, the dimension, why it matters, and a concrete fix.

```
## Critical
<must fix before merge — data loss, security hole, broken core behavior, failing tests>

## High
<should fix before merge — wrong behavior on a real path, missing error handling, regression risk>

## Medium
<fix soon — maintainability, smaller correctness gaps, weak tests>

## Low
<minor — style within conventions, naming, small cleanups>

## Suggestions
<optional improvements, non-blocking ideas, follow-ups>
```

A merge is blocked while any **Critical** or **High** finding stands. Medium and below
may be tracked as follow-ups by agreement.
