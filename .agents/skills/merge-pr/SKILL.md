---
name: merge-pr
description: Merge a reviewed, CI-green PR (squash, this repo's convention) and run the sync-branch cleanup in one shot. Use when the user says "merge PR #N", "merge เข้า develop", or wants merge+cleanup together instead of merging via GitHub UI and telling Codex afterward.
argument-hint: <pr-number>
---

# Merge PR + sync

Combines merging a PR with the [sync-branch](../sync-branch/SKILL.md) post-merge
cleanup — composition, not duplication: this skill only does the merge; step 3 hands
off to sync-branch's existing steps for everything after.

## Steps

1. **Verify CI is green** (AGENTS.md CI Gate rules: ห้าม merge ข้าม failing check):
   ```sh
   env -u GH_TOKEN gh pr checks <n>
   env -u GH_TOKEN gh pr view <n> --json reviewDecision,mergeable
   ```
   Stop and report if any required check is failing/pending, `mergeable` is not
   `MERGEABLE`, or there's an unresolved `CHANGES_REQUESTED` review — do not merge.

1.5. **Pre-merge review gate** (`.ai/shared/REVIEW_PROTOCOL.md` § Pre-merge multi-angle
     review, sdd-premerge-review-standard — full trigger criteria, record format, and
     override path live there; not restated here):
   ```sh
   env -u GH_TOKEN gh pr view <n> --json additions,deletions,labels,headRefOid,headRefName,files
   ```
   - **Evaluate the trigger**: phase-close (a `phase-close` label, OR the diff touches a
     `.ai/specs/*/tasks.md` whose post-merge content has zero `- [ ]` lines left) OR size
     (`additions + deletions > diffThreshold` from `.ai/policies/review-standard.json` —
     default 400 if that file is missing/unparseable; still evaluate the gate, never
     fail-open to "no gate"). Neither → skip straight to step 1 (unchanged fast path).
   - **If triggered**, check for `docs/reviews/PR-<n>-<sha7>.md` where `<sha7>` is the
     first 7 chars of the CURRENT `headRefOid` (a record for an earlier head counts as
     missing — staleness rule, with the override-commit exception in
     `REVIEW_PROTOCOL.md` so committing the record doesn't invalidate itself).
     - Record exists for the current head → proceed to step 1.
     - Missing or stale → **STOP** and ask the operator to either run `/review-fanout`
       on this PR's diff (its own output contract now includes writing the record), or
       override with a one-line reason.
       - **Override**: fill in [review-record.md](../../.ai/templates/review-record.md)
         (`kind: override`, date, `finders/verifiers: —`, the reason) as
         `docs/reviews/PR-<n>-<sha7>.md`, then commit it ONTO the PR branch itself
         (`git fetch origin <headRefName>`, checkout/commit/push that branch — NOT a
         separate follow-up commit after merging) before proceeding to step 1. An
         override with no reason, or committed anywhere other than the PR branch before
         the merge, is not a valid path.
   - This step never runs `/review-fanout` itself — it is expensive and human-priced;
     the operator decides to spend it.

2. **Merge (squash), no branch deletion here:**
   ```sh
   env -u GH_TOKEN gh pr merge <n> --squash
   ```
   Do **not** add `--delete-branch` — per `gh pr merge --help` it deletes **both** the
   local and remote branch, which would leave nothing for sync-branch's local-delete
   step to find, making it report/fail after the PR already merged successfully. Let
   sync-branch own all cleanup (local + remote), unchanged from its standalone flow.

3. **Hand off to sync-branch**: run `.agents/skills/sync-branch/SKILL.md` steps 1-5 for
   this same PR number. Its own checks degrade to a safe no-op only for the merge check
   in step 1 (trivially passes — just merged in step 2 above); steps 3 and 4 do the
   real local + remote branch deletion exactly as they would standalone.

4. **Report**: merge sha, base branch synced sha, local branch deleted (y/n).

## Guardrails

- Never merge with a failing or pending required check, or an unresolved
  changes-requested review — that CI/review state is the actual safety gate here, not
  anything git-side.
- This performs a real merge to `develop`/`main` — only run for a PR number the user
  explicitly named; invoking this skill by name is the authorization for that merge.
