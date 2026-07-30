---
name: sync
description: Sync local repo หลัง PR ถูก merge — fetch, fast-forward default branch, แล้วถามก่อนลบ feature branch เสมอ (3 ตัวเลือก)
---

# Sync หลัง merge PR

ขั้นตอนหลัง PR ถูก merge เข้า default branch (repo นี้ = `develop`).

หมายเหตุ credential: ถ้า `gh auth status` ล้มเหลวแต่มี `GH_TOKEN` อยู่ใน environment ให้สงสัยว่า
token stale แล้ว shadow keyring — กรณีนั้นรัน `git`/`gh` ที่แตะ remote ด้วย `env -u GH_TOKEN`
(อาการเดิมบนเครื่องนี้ ดู memory `gh-token-env-shadows-keyring`). บนเครื่อง/CI ที่ `GH_TOKEN`
เป็น credential จริงและใช้งานได้ ห้าม strip.

## ขั้นตอน

1. **ยืนยันว่า merge จริง** ก่อนแตะอะไร และเก็บทั้ง merge commit + ชื่อ head branch จากคำตอบ:
   ```bash
   gh pr view <n> --json state,mergedAt,mergeCommit,headRefName
   ```
   ถ้า `state != MERGED` — หยุด รายงาน ไม่ sync. `headRefName` คือ branch เป้าหมายเดียวของ
   ขั้นตอน 5 — ห้ามเดาจาก branch ที่ checkout อยู่ (อาจรัน /sync จาก `develop` หรือ branch อื่น).
2. **เช็ค working tree** — `git status --short` ต้องว่าง. ถ้ามีไฟล์ค้าง หยุดถาม user ก่อน
   (ห้าม stash/discard เอง).
3. **Fetch + fast-forward**:
   ```bash
   git fetch origin
   git checkout develop
   git pull --ff-only origin develop
   ```
   `--ff-only` เท่านั้น — ถ้า ff ไม่ได้ แปลว่ามี local commit แปลกปลอมบน develop หยุดรายงาน
   (ห้าม merge/rebase เอง — กฎ repo ห้าม commit ตรงเข้า develop).
4. **ยืนยันผล** — เช็คว่า merge commit ของ PR อยู่ใน develop จริงด้วย OID ตรง ๆ ไม่ใช่กวาดตา log
   (merge commit อาจไม่อยู่ใน N บรรทัดล่าสุดถ้ามี PR อื่น merge ตามมา):
   ```bash
   git merge-base --is-ancestor <mergeCommit.oid> develop && echo "merge commit in develop"
   ```
5. **feature branch ที่เพิ่ง merge** (= `headRefName` จากขั้นตอน 1) — ห้ามลบทันที ให้ถาม user
   เสมอด้วย AskUserQuestion 3 ตัวเลือกนี้ โดยระบุชื่อ branch ตรง ๆ ในคำถาม
   (ห้ามข้ามแม้ user เคยตอบใน session ก่อน):

   | ตัวเลือก | ทำอะไร |
   |---|---|
   | ลบ local + prune remote refs | `git branch -d <headRefName>` แล้ว `git fetch --prune origin` |
   | ลบเฉพาะ local | `git branch -d <headRefName>` — คง remote-tracking refs ไว้ |
   | เก็บไว้ทั้งหมด | ไม่แตะ branch เลย |

   ถ้า `headRefName` ไม่มีเป็น local branch — ข้ามขั้นตอนนี้ รายงานเฉย ๆ. ใช้ `-d` (ไม่ใช่ `-D`)
   เสมอ — ถ้า `-d` ปฏิเสธแปลว่ามี commit ที่ยังไม่ถูก merge ให้หยุดรายงาน ห้าม escalate เป็น `-D` เอง.
6. **รายงานสรุป**: HEAD ใหม่ของ develop, จำนวน commit ที่ดึงมา, และผลการจัดการ branch.

## ข้อห้าม

- ห้าม `git reset --hard` / `git clean` ทุกกรณีใน flow นี้
- ห้าม force push
- ห้ามลบ branch โดยไม่ผ่านคำถาม 3 ตัวเลือกข้างบน
