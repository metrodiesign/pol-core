---
description: Merge PR ที่ CI เขียวแล้ว จากนั้นรัน /sync ต่อทันที (fetch, fast-forward develop, ถามเรื่อง branch)
argument-hint: <pr-number>
allowed-tools: Bash, Skill, AskUserQuestion
---

Merge PR หมายเลข `$ARGUMENTS` แล้ว sync repo ต่อ

หมายเหตุ credential: ถ้า `gh`/`git` ที่แตะ remote ล้มเหลวและมี `GH_TOKEN` อยู่ใน environment
ให้สงสัยว่า token stale แล้ว shadow keyring — รันด้วย `env -u GH_TOKEN`

## ขั้นตอน

1. **ตรวจสถานะก่อนแตะอะไร**:
   ```bash
   gh pr view <n> --json state,mergeable,reviewDecision,headRefName,baseRefName
   gh pr checks <n>
   ```
   - `state` ต้องเป็น `OPEN` — ถ้า `MERGED` แล้ว ข้ามไปขั้นตอน 3 เลย, ถ้า `CLOSED` หยุดรายงาน
   - `baseRefName` ต้องเป็น `develop` — ถ้าไม่ใช่ หยุดถาม user ก่อน
   - **CI ต้องผ่านครบ ไม่มี fail** (กฎ repo: ห้าม merge ข้าม failing check) ถ้ายังมี `pending`
     ให้รายงานว่ายังไม่พร้อมและถาม user ว่าจะรอหรือหยุด — ห้าม merge ทับ pending เอง
   - ถ้ามี check ที่ fail: หยุด รายงานชื่อ check + ลิงก์ ห้าม merge

2. **Merge** — squash เป็นค่าเริ่มต้น, ไม่ลบ branch ตอน merge (ขั้นตอน 4 จะถามเอง):
   ```bash
   gh pr merge <n> --squash --delete-branch=false
   ```
   ถ้า repo ตั้ง merge strategy อื่นไว้ให้ทำตามที่ repo บังคับ

3. **Sync** — เรียก skill `sync` (อย่าเขียน logic ซ้ำที่นี่) skill นั้นจะ fetch, fast-forward
   `develop` แบบ `--ff-only`, ยืนยันว่า merge commit อยู่ใน develop จริง แล้วถามเรื่อง branch

4. **รายงาน**: หมายเลข PR + merge commit, HEAD ใหม่ของ develop, ผลการจัดการ branch

## ข้อห้าม

- ห้าม merge ขณะมี check ที่ fail หรือขณะที่ยังไม่รู้ผล CI
- ห้าม `--admin` / bypass required check
- ห้าม force push, ห้าม `git reset --hard`
- ห้ามลบ branch โดยไม่ผ่านคำถาม 3 ตัวเลือกใน skill `sync`
