---
name: sync
description: Sync local repo หลัง PR ถูก merge — fetch, fast-forward default branch, แล้วถามก่อนลบ feature branch เสมอ (3 ตัวเลือก)
---

# Sync หลัง merge PR

ขั้นตอนหลัง PR ถูก merge เข้า default branch (repo นี้ = `develop`). ทุกคำสั่ง `git`/`gh` ที่แตะ
remote ต้องรันด้วย `env -u GH_TOKEN` (stale GH_TOKEN บนเครื่องนี้ shadow keyring).

## ขั้นตอน

1. **ยืนยันว่า merge จริง** ก่อนแตะอะไร:
   ```bash
   env -u GH_TOKEN gh pr view <n> --json state,mergedAt,mergeCommit
   ```
   ถ้า `state != MERGED` — หยุด รายงาน ไม่ sync.
2. **เช็ค working tree** — `git status --short` ต้องว่าง. ถ้ามีไฟล์ค้าง หยุดถาม user ก่อน
   (ห้าม stash/discard เอง).
3. **Fetch + fast-forward**:
   ```bash
   env -u GH_TOKEN git fetch origin
   git checkout develop
   env -u GH_TOKEN git pull --ff-only origin develop
   ```
   `--ff-only` เท่านั้น — ถ้า ff ไม่ได้ แปลว่ามี local commit แปลกปลอมบน develop หยุดรายงาน
   (ห้าม merge/rebase เอง — กฎ repo ห้าม commit ตรงเข้า develop).
4. **ยืนยันผล** — `git log --oneline -3` ต้องเห็น merge commit ของ PR.
5. **feature branch ที่เพิ่ง merge** — ห้ามลบทันที ให้ถาม user เสมอด้วย AskUserQuestion
   3 ตัวเลือกนี้ (ห้ามข้ามแม้ user เคยตอบใน session ก่อน):

   | ตัวเลือก | ทำอะไร |
   |---|---|
   | ลบ local + prune remote refs | `git branch -d <branch>` แล้ว `env -u GH_TOKEN git fetch --prune origin` |
   | ลบเฉพาะ local | `git branch -d <branch>` — คง remote-tracking refs ไว้ |
   | เก็บไว้ทั้งหมด | ไม่แตะ branch เลย |

   ใช้ `-d` (ไม่ใช่ `-D`) เสมอ — ถ้า `-d` ปฏิเสธแปลว่ามี commit ที่ยังไม่ถูก merge ให้หยุดรายงาน
   ห้าม escalate เป็น `-D` เอง.
6. **รายงานสรุป**: HEAD ใหม่ของ develop, จำนวน commit ที่ดึงมา, และผลการจัดการ branch.

## ข้อห้าม

- ห้าม `git reset --hard` / `git clean` ทุกกรณีใน flow นี้
- ห้าม force push
- ห้ามลบ branch โดยไม่ผ่านคำถาม 3 ตัวเลือกข้างบน
