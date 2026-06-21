---
name: spec-analyze
description: Audit the requirements.md of the active spec for logical issues before moving to design. Use for complex features or compliance-sensitive domains.
argument-hint: <feature folder name (optional)>
---

# Analyze Requirements

Resolve the target spec: use $ARGUMENTS if given; if `.ai/specs/` holds more
than one feature and none was named, list them and ask — never guess.

Read the spec's requirements.md and report issues in FIVE categories:

  1. Logical inconsistencies — requirements that contradict each other
  2. Ambiguities — statements open to more than one interpretation
  3. Conflicting constraints — requirements that cannot all hold at once
  4. Gaps — missing scenarios, unhandled edge cases, undefined error behavior,
     missing concurrent/interaction scenarios
  5. Unstated assumptions — references to concepts or behavior never defined

Reason across the requirement SET (pairs/groups, functional vs non-functional),
not one requirement at a time.

Incremental re-run: if a findings log with a commit anchor already exists under
"Edge Cases & Open Questions", run `git diff <anchor> -- <requirements.md>`
(working tree included) and focus on the changed REQs plus their interactions
with the rest; do not re-flag findings already logged with a decision. No anchor
= full audit.

For each issue: cite the REQ ID and phrase it as a QUESTION with 2-3 concrete
fix options plus the standing options "ตอบเอง" and "ข้าม — ambiguity ตั้งใจ".
Batch ALL questions in ONE Thai message.

Do NOT silently edit the file — edit ONLY after I decide. Then:

- Apply the approved fixes to requirements.md, keeping existing REQ IDs stable.
  If it was already approved, re-stamp its header: `> Status: approved
  <original date>, amended <YYYY-MM-DD>`.
- Log EVERY finding with its decision — dismissed ones included, with a one-line
  reason — under "Edge Cases & Open Questions", anchored with the current commit
  hash of requirements.md (`git log -1 --format=%h -- <path>`), so finding codes
  never dangle into a lost conversation and re-runs can skip them.
- If design.md or tasks.md already exist, flag which sections must sync (see the
  sync-mode paragraphs in /spec-design and /spec-tasks).
