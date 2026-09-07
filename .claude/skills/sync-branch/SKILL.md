---
name: sync-branch
description: Sync the local base branch and delete a just-merged PR branch locally (checking and reporting the remote copy, never deleting it here), the repo's squash-merge convention. Use right after a PR is merged, when the user says "sync + ลบ branch", "cleanup branch หลัง merge", or "PR merged แล้ว".
argument-hint: [pr-number]
---

# Sync branch after merge

Post-merge cleanup: sync the base branch locally, delete the feature branch locally —
the remote copy only gets checked and reported here, never deleted (see "Critical"
below) — once it's confirmed merged. This repo squash-merges every PR (one commit per
PR on `develop`), so `git branch -d` in step 3 either succeeds with the benign warning
below, or refuses outright — both expected, not failures (see step 3):

```
warning: deleting branch '<name>' that has been merged to 'refs/remotes/origin/<name>',
but not yet merged to HEAD
```

## Critical: agent ลบ remote branch เองไม่ได้ในเรพนี้

การลบ remote ref ไม่ใช่ความสามารถของ agent ในเรพนี้ ไม่ว่าจะทางไหน:

- `git push origin --delete <branch>` (หรือ `git push --delete`, หรือ refspec ลบแบบ
  `+`/`:`) ถูก block แบบ unconditional โดย Tier 1 (`.githooks/pre-push:36`): push ไหนที่
  local sha เป็น all-zero (คือการลบ ref) โดน `block "deleting remote ref ... confirm
  with a human first."` ทันที — บล็อกนี้ไม่สนใจว่า branch ปัจจุบันคืออะไร
- `gh api -X DELETE "repos/<owner>/<repo>/git/refs/heads/<branch>"` ดูเหมือนเป็นคนละ
  เครื่องมือที่ไม่แตะ `git push` เลย แต่ก็ถูก block เช่นกัน แบบ unconditional โดย session
  hook (`.ai/bin/check-destructive.sh:158-169`) — เพิ่มเข้ามาหลังจากมี agent ใช้ทางนี้เลี่ยง
  Tier 1 floor จริง (PR #125/#126) ข้อความ block เองระบุทางที่ยอมรับไว้แล้วคือ
  `gh pr merge --delete-branch` ตอน merge

เพราะฉะนั้นทางเดียวที่ agent ลบ remote branch ได้คือ `--delete-branch` ตอนรัน
`gh pr merge` (ดู [merge-pr](../merge-pr/SKILL.md) step 2) ซึ่งต้องทำ*ก่อน*ที่ PR จะกลาย
เป็น "merged ไปแล้ว" ถ้า PR ถูก merge ไปแล้วด้วยทางอื่น (web UI, เครื่องมืออื่น) การลบ
remote เป็นงานของมนุษย์ล้วน — step 4 ด้านล่างตรวจจับกรณีนี้แล้วรายงาน ไม่ลองทางที่ถูก
block ทั้งสองทาง

## Steps

1. **Resolve the branch to clean up.**
   - If `$ARGUMENTS` is a PR number: `env -u GH_TOKEN gh pr view <n> --json state,headRefName,baseRefName,mergeCommit`.
     Stop and report if `state != "MERGED"` — do not clean up an open branch.
   - Else: use the current branch (`git branch --show-current`). Confirm it's actually
     merged: `env -u GH_TOKEN gh pr list --head <branch> --state merged --json number,mergedAt,baseRefName`.
     Stop and report if empty (nothing merged for this branch yet).

2. **Determine the base branch** — use the `baseRefName` fetched in step 1 directly
   (this repo's convention keeps it `develop`, see `CLAUDE.md`: "ห้าม push ตรงเข้า main,
   develop ต้องผ่าน PR เสมอ" — but a PR based on `main` must sync `main`, not be assumed
   into `develop`).

3. **Sync base + delete local branch:**
   ```sh
   git checkout <base>
   git pull
   git branch -d <branch> || git branch -D <branch>
   ```
   Try `-d` first. It can refuse a genuinely-merged squash branch: `-d` checks ancestry
   against the branch's configured upstream ref (or `HEAD` if none), and a squash
   commit has no ancestry relation to either — so once the upstream ref is gone or was
   never fetched (pruned remote, fresh clone, a teammate already deleted the remote
   branch), `-d` has nothing left to check against and refuses even though the content
   is already on `<base>`. Only fall back to `-D` here because step 1 already proved
   independently, via the GitHub API, that this exact branch's PR is merged — that's
   the real safety gate, not `-d`.

4. **เช็คว่า remote branch ยังอยู่ไหม** (read-only เท่านั้น — ห้ามลองลบตรงนี้ ดู "Critical"
   ด้านบน):
   ```sh
   env -u GH_TOKEN gh api "repos/<owner>/<repo>/branches/<branch>" >/dev/null 2>&1 && echo EXISTS || echo GONE
   ```
   หา `<owner>/<repo>` จาก `env -u GH_TOKEN gh repo view --json nameWithOwner -q .nameWithOwner`

   - `GONE` — ถูกลบไปแล้ว (ส่วนใหญ่มาจาก `gh pr merge --delete-branch` ใน merge-pr step 2
     หรือมีคนลบด้วยมือไปก่อนแล้ว) ไม่ต้องทำอะไรต่อ
   - `EXISTS` — รายงานให้ user ทราบแล้วหยุด การลบเป็นงานของมนุษย์ (ผ่าน GitHub UI หรือ
     terminal ของ user เอง) — ห้ามลอง `git push --delete` หรือ `gh api -X DELETE` ทั้งคู่
     ถูก block (ดู "Critical" ด้านบน)

5. **Report**: base branch + synced sha, local branch deleted (y/n), สถานะ remote
   branch (ลบไปแล้ว / ยังอยู่ — ต้องให้มนุษย์ลบเอง)

## Guardrails

- The merged-PR check in step 1 is the real safety gate, not `-d` — `-d`'s local
  ancestry check gives false negatives on squash-merged branches (see step 3), so the
  `-D` fallback there is only safe because step 1 already confirmed the merge via the
  GitHub API. If step 1 itself fails to confirm a merge, stop and report why — do not
  force-delete past that.
- Never touch worktrees here — this skill assumes a normal (non-worktree) checkout.
- ห้ามลองลบ remote branch เองทุกกรณี — ทุกทาง (`git push --delete`,
  `gh api -X DELETE .../refs/heads/`) ถูก block แบบ unconditional ในเรพนี้ (ดู "Critical"
  ด้านบน) step 4 เช็คแค่ว่ายังอยู่ไหมแล้วรายงาน ไม่ลบ
