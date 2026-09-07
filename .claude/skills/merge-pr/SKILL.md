---
name: merge-pr
description: Merge a reviewed, CI-green PR (squash, this repo's convention) and run the sync-branch cleanup in one shot. Use when the user says "merge PR #N", "merge เข้า develop", or wants merge+cleanup together instead of merging via GitHub UI and telling Claude afterward.
argument-hint: <pr-number>
---

# Merge PR + sync

Combines merging a PR with the [sync-branch](../sync-branch/SKILL.md) post-merge
cleanup — composition, not duplication: this skill only does the merge; step 3 hands
off to sync-branch's existing steps for everything after.

## Steps

1. **Verify CI is green** (CLAUDE.md CI Gate rules: ห้าม merge ข้าม failing check):
   ```sh
   env -u GH_TOKEN gh pr checks <n>
   env -u GH_TOKEN gh pr view <n> --json state,reviewDecision,mergeable
   ```
   Stop and report if any required check is failing/pending, `mergeable` is not
   `MERGEABLE`, or there's an unresolved `CHANGES_REQUESTED` review — do not merge.

   **PR ถูก merge ไปแล้วนอก flow นี้**: ถ้า `state` เป็น `MERGED` อยู่แล้ว แปลว่า PR นี้ถูก
   merge ด้วยทางอื่น (GitHub web UI, เครื่องมืออื่น) ไม่ใช่ผ่าน skill นี้ — ข้าม step 1.5
   และ step 2 ทั้งคู่ (ไม่มีอะไรให้ merge แล้ว) แล้วไปที่ step 3 ตรง ๆ รายงานตามจริงว่า
   remote branch ลบผ่าน flow นี้ไม่ได้ และตาม sync-branch step 4 ต้องให้มนุษย์ลบเองถ้ายังอยู่

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

2. **Merge (squash) พร้อมลบ branch:**
   ```sh
   env -u GH_TOKEN gh pr merge <n> --squash --delete-branch
   ```
   `--delete-branch` ตรงนี้จำเป็น ไม่ใช่ทางเลือก — เป็นทางเดียวที่ agent ลบ remote ref ได้ใน
   repo นี้ `git push --delete` ถูก block แบบ unconditional โดย Tier 1
   (`.githooks/pre-push:36` — "deleting remote ref ... confirm with a human first") และ
   `gh api -X DELETE .../git/refs/heads/<branch>` ถูก block แบบ unconditional โดย session
   hook เช่นกัน (`.ai/bin/check-destructive.sh:158-169` ซึ่งข้อความ block เองระบุ flag นี้
   เป็นทางที่ยอมรับ) `--delete-branch` ลบทั้ง local และ remote branch (ตาม
   `gh pr merge --help`) — ซึ่งตอนนี้คือผลลัพธ์ที่ต้องการ ไม่ใช่ปัญหาอีกต่อไป

3. **Hand off to sync-branch**: run `.claude/skills/sync-branch/SKILL.md` steps 1-5
   สำหรับ PR number เดียวกัน step 1-2 ผ่านง่าย ๆ (merge ไปแล้วใน step 2 ข้างบน + อ่าน
   `baseRefName` ตรง ๆ) step 3 (sync base + delete local ในขั้นเดียวกัน) คืองานจริงที่เหลือ —
   ส่วน sync base ต้องทำเสมอ ส่วนลบ local มักเจอว่า branch หายไปแล้ว เพราะ `--delete-branch`
   ใน step 2 ข้างบนลบทั้ง local และ remote ไปแล้ว `git branch -d/-D` เลยรายงาน "not found"
   ตามด้วย step 4 ที่มักได้ `GONE` — ทั้งคู่คือผลที่คาดไว้ ไม่ใช่ความล้มเหลว

4. **Report**: merge sha, base branch synced sha, local branch deleted (y/n).

## Guardrails

- Never merge with a failing or pending required check, or an unresolved
  changes-requested review — that CI/review state is the actual safety gate here, not
  anything git-side.
- This performs a real merge to `develop`/`main` — only run for a PR number the user
  explicitly named; invoking this skill by name is the authorization for that merge.
