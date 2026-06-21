# EARS Notation

> Vendor-neutral. Mandatory for every functional requirement in `requirements.md`.
> Canonical source for the requirement-writing rules referenced by
> [TASK_PROTOCOL.md](TASK_PROTOCOL.md).

EARS (Easy Approach to Requirements Syntax) constrains each requirement to one of a
small set of patterns so that it is unambiguous and directly testable.

## The 5 patterns

Write every functional requirement using exactly one of these patterns:

| Pattern | Template | Use for |
|---|---|---|
| Ubiquitous | `THE SYSTEM SHALL <behavior>` | always-true behavior |
| Event-driven | `WHEN <trigger> THE SYSTEM SHALL <behavior>` | response to an event |
| State-driven | `WHILE <state> THE SYSTEM SHALL <behavior>` | behavior during a state |
| Optional | `WHERE <feature included> THE SYSTEM SHALL <behavior>` | behavior gated on a feature being present |
| Error handling | `IF <unwanted condition> THEN THE SYSTEM SHALL <response>` | handling an undesired condition |

## Stable REQ-ID rule

Every requirement carries a **stable, hierarchical ID** of the form `REQ-<n>.<m>`
(for example `REQ-1.2`): a capability number and a criterion number. IDs are stable —
once assigned they do not get renumbered, because design, tasks, tests, and traceability
all cite them. A bugfix spec uses `F-<n>` (the fix behavior) and `B-<n>` (unchanged
behavior that must be preserved) on the same principle.

Typical shape inside `requirements.md`:

```
## REQ-1: <Capability, e.g. Premium Calculation>
**User Story:** As a <role>, I want <goal>, so that <benefit>.
**Acceptance Criteria (EARS):**
- 1.1  THE SYSTEM SHALL <behavior>                               (ubiquitous)
- 1.2  WHEN <event> THE SYSTEM SHALL <behavior>                  (event-driven)
- 1.3  WHILE <state> THE SYSTEM SHALL <behavior>                 (state-driven)
- 1.4  WHERE <feature is included> THE SYSTEM SHALL <behavior>   (optional)
- 1.5  IF <error condition> THEN THE SYSTEM SHALL <response>     (error handling)
```

## Writing atomic, testable requirements

- **Atomic** — one observable behavior per criterion. Split compound criteria joined by
  "and" into separate IDs.
- **Unambiguous** — exactly one reading. Reject subjective wording ("fast",
  "user-friendly", "looks good") unless quantified with a measurable threshold
  ("renders within 200ms", "contrast ratio >= 4.5:1").
- **Testable** — each criterion must map to a test that can pass or fail
  deterministically. If you cannot describe the test, the requirement is not yet
  testable — rewrite it.
- **Complete** — cover the happy path AND error/edge cases. Use `IF ... THEN` for every
  error condition; do not leave failure behavior implicit.
- **Traceable** — every REQ-ID is later cited by a design element, a task's
  `Satisfies:` line, and at least one test. An uncovered REQ at the end of
  implementation is a blocker.

For the audit step that hunts gaps, conflicts, and untestable wording BEFORE design,
see the analyze phase in [TASK_PROTOCOL.md](TASK_PROTOCOL.md).
