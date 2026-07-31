---
description: Merge PR ที่ CI เขียวแล้ว จากนั้นรัน /sync ต่อทันที (fetch, fast-forward develop, ถามเรื่อง branch)
argument-hint: <pr-number>
allowed-tools: Bash, Skill, AskUserQuestion
---

Merge PR หมายเลข `$ARGUMENTS` แล้ว sync repo ต่อ

หมายเหตุ credential: ถ้า `gh`/`git` ที่แตะ remote ล้มเหลวและมี `GH_TOKEN` อยู่ใน environment
ให้สงสัยว่า token stale แล้ว shadow keyring — รันด้วย `env -u GH_TOKEN`

## ขั้นตอน

1. **ตรวจสถานะก่อนแตะอะไร** — เก็บ `headRefOid` ไว้ใช้ในขั้นตอน 2 และ 4:
   ```bash
   gh pr view <n> --json state,mergeable,reviewDecision,headRefName,headRefOid,baseRefName
   gh pr checks <n>
   ```
   - `state` ต้องเป็น `OPEN` — ถ้า `MERGED` แล้ว ข้ามไปขั้นตอน 3 เลย, ถ้า `CLOSED` หยุดรายงาน
   - `baseRefName` ต้องเป็น `develop` — ถ้าไม่ใช่ หยุดถาม user ก่อน
   - **`reviewDecision` ต้องเป็น `APPROVED`** — `CHANGES_REQUESTED` / `REVIEW_REQUIRED` / `null`
     ให้หยุดรายงาน ไม่ merge เว้นแต่ user สั่งข้ามอย่างชัดเจนในข้อความนั้น. อย่าหวังให้เซิร์ฟเวอร์กันให้:
     branch protection ของ `develop` บังคับ required check ตัวเดียวคือ `CI / guards + spec-trace`
     (ดู `docs/04-git-pr-and-rules.md` §4.2) ไม่ได้บังคับ review และไม่ได้บังคับ check ตัวอื่นด้วย
   - **CI ต้องผ่านครบ ไม่มี fail** (กฎ repo: ห้าม merge ข้าม failing check) ถ้ายังมี `pending`
     ให้รายงานว่ายังไม่พร้อมและถาม user ว่าจะรอหรือหยุด — ห้าม merge ทับ pending เอง
   - ถ้ามี check ที่ fail: หยุด รายงานชื่อ check + ลิงก์ ห้าม merge

2. **Merge** — ผูกกับ SHA ที่เพิ่งตรวจ, ไม่ลบ branch ตอน merge (ขั้นตอน 4 จะถามเอง):
   ```bash
   gh pr merge <n> --squash --delete-branch=false --match-head-commit <headRefOid>
   ```
   `--match-head-commit` คือสิ่งที่กันกรณีมีคน push commit ใหม่ระหว่างขั้นตอน 1 กับ 2 — ถ้า head ขยับ
   คำสั่งจะล้มแทนที่จะ merge โค้ดที่ยังไม่ผ่าน CI. เจอกรณีนี้ให้กลับไปขั้นตอน 1 ใหม่ทั้งหมด
   ห้ามถอด flag นี้ทิ้งเพื่อให้ผ่าน. ถ้า repo ตั้ง merge strategy อื่นไว้ให้ทำตามที่ repo บังคับ

3. **Sync** — เรียก skill `sync` (อย่าเขียน logic ซ้ำที่นี่) skill นั้นจะ fetch, fast-forward
   `develop` แบบ `--ff-only`, ยืนยันว่า merge commit อยู่ใน develop จริง แล้วถามเรื่อง branch

4. **ถ้า `git branch -d` ปฏิเสธเพราะ squash** — squash merge สร้าง commit ใหม่ ปลาย feature branch
   จึงไม่เป็น ancestor ของ `develop`. `git branch -d` ผ่านได้เมื่อ branch merged เข้า **upstream ของมัน**
   (กรณี `push -u`) แต่ถ้า branch ไม่มี upstream git จะเทียบกับ `HEAD` แทน แล้วปฏิเสธทั้งที่ merge ไปแล้วจริง

   กรณีนี้เท่านั้นที่ `-D` ปลอดภัย และต้องยืนยัน **ครบทั้งสองข้อ** ก่อน:
   ```bash
   gh pr view <n> --json state --jq '.state'          # ต้องได้ MERGED
   git rev-parse <headRefName>                         # ต้องตรงกับ headRefOid จากขั้นตอน 1
   ```
   ตรงทั้งคู่ = ทุก commit บน branch อยู่ใน squash commit แล้ว ลบด้วย `git branch -D <headRefName>` ได้
   ไม่ตรงข้อใดข้อหนึ่ง (เช่นมี commit ที่ push ไม่ทันเข้า PR) = **หยุด รายงาน ห้ามลบ**

5. **รายงาน**: หมายเลข PR + merge commit, HEAD ใหม่ของ develop, ผลการจัดการ branch

## ข้อห้าม

- ห้าม merge ขณะมี check ที่ fail หรือขณะที่ยังไม่รู้ผล CI
- ห้าม merge เมื่อ `reviewDecision` ไม่ใช่ `APPROVED` เว้นแต่ user สั่งข้ามชัดเจน
- ห้ามถอด `--match-head-commit` ออกเพื่อให้ merge ผ่าน
- ห้าม `--admin` / bypass required check
- ห้าม force push, ห้าม `git reset --hard`
- ห้ามลบ branch โดยไม่ผ่านคำถาม 3 ตัวเลือกใน skill `sync`
- ห้ามใช้ `git branch -D` นอกกรณีขั้นตอน 4 ที่ยืนยัน MERGED + SHA ตรงแล้วเท่านั้น
