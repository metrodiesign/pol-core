---
name: ship-pr
description: Commit งานปัจจุบันขึ้น feature branch แล้วเปิด PR เข้า develop ตาม workflow ของ repo (ห้าม push ตรง, ห้าม force push, PR template + test evidence จริง)
argument-hint: "[<branch-suffix หรือชื่อ feature> — ว่างได้ ถ้าเดาได้จาก spec/งานที่ทำอยู่]"
---

# Ship PR: $ARGUMENTS

Commit งานใน working tree ปัจจุบันแล้วเปิด PR เข้า `develop` ตามกติกา repo นี้
ทุกขั้นเป็นภาษาไทยเวลา narrate; commit message / PR title+body ตาม convention repo (อังกฤษได้)

## Pre-flight (หยุดทันทีถ้าไม่ผ่าน)

1. `git status --short` — ต้องมีของให้ commit **หรือ** branch มี commit ที่ยังไม่ push/ไม่อยู่ใน base
   (`git log --oneline origin/<base>..HEAD` ไม่ว่าง) — working tree ว่างแต่ commit พร้อมแล้ว = ผ่าน
   ข้ามขั้น commit ไป push/เปิด PR ได้เลย; ทั้งสองว่างจึงหยุด (ไม่มีอะไรให้ ship)
   มีไฟล์ค้าง → review รายการไฟล์ว่าตรงกับงานที่ทำจริง
   ไฟล์แปลกปลอม (`.env*`, secret, ไฟล์นอก scope งาน) ห้ามติดไป — unstage/แจ้ง user
2. ถ้ายังไม่ได้รัน full gate ในบทสนทนานี้: รัน `dotnet build` + `dotnet test` ให้เขียวก่อน
   (Integration ต้อง `source .env.integration` ใน Bash call เดียวกับ `dotnet test`)
   ห้ามเปิด PR ข้าม failing test — ไม่มีข้อยกเว้น
3. งานมาจาก spec (`.ai/specs/<feature>/`) → tasks.md ที่เกี่ยวต้อง flip `[x]` + `Evidence:` ครบแล้ว
   และรัน `scripts/spec-trace.sh <feature>` ผ่าน

## Branch + commit

4. อยู่บน `develop`/`main` → สร้าง branch ใหม่ก่อน: `feature/<kebab-name>` (หรือ `fix/`, `data/`, `docs/`
   ตามชนิดงาน — ดู prefix จาก `git log --oneline` ล่าสุด) ชื่อจาก $ARGUMENTS หรือชื่อ spec ที่ทำอยู่
   อยู่บน branch งานอยู่แล้ว → ใช้ต่อ ไม่สร้างซ้อน
5. `git add -A` แล้ว `git commit` **แยกคนละ Bash call** — ห้าม `add && commit`
   (hook เคย block ครึ่งทางแล้ว commit ได้ crap ค้าง — ดู LESSONS)
   commit message: conventional (`feat(scope):`, `fix(scope):`, ...) + เนื้อหาอธิบาย why ไม่ใช่แค่ what
   ปิดท้ายด้วยบรรทัด `Co-Authored-By: Claude ... <noreply@anthropic.com>` ตามที่ harness กำหนด
6. hook block commit → อ่านเหตุผล แก้ตามกฎที่มันชี้ แล้วลองใหม่ **ห้าม bypass / ห้าม --no-verify**
   หลัง block ของ compound command ต้องเช็คว่าส่วนไหนรันไปแล้ว (`git status` ใหม่เสมอ)

## Push + PR

7. push: `unset GH_TOKEN; git push -u origin <branch>` — **`unset GH_TOKEN` ทุก Bash call ที่แตะ
   git push / gh** (stale env token shadow keyring — ดู memory `gh-token-env-shadows-keyring`)
8. เปิด PR: `unset GH_TOKEN; gh pr create --base develop --title "<conventional title>" --body ...`
   - body ตาม `.github/pull_request_template.md` ครบทุก section:
     Summary (ไทย, อธิบาย what+why), Tracking (`Closes #<epic>` เมื่อจบ feature / `Refs #`
     เมื่อยังไม่ครบ — ไม่มี epic ก็เขียนบอกตรงๆ), Tasks advanced, Test evidence (คำสั่ง+ผลที่รันจริง
     เท่านั้น ห้ามแต่ง), Checklist (ติ๊กเฉพาะที่จริง)
   - ปิดท้าย body: `🤖 Generated with [Claude Code](https://claude.com/claude-code)`
9. รายงาน URL ของ PR ให้ user

## ข้อห้ามยืนพื้น (จาก CLAUDE.md — ทวนกันหลุด)

- ห้าม push ตรงเข้า `develop`/`main` — ทุกอย่างผ่าน PR
- ห้าม force push
- ห้าม merge เอง — รอ CI เขียว + user เป็นคนตัดสิน merge
- ห้าม commit secret ทุกชนิด; `.env*` อยู่ใน `.gitignore` เสมอ
- CI แดง → แก้ใน branch เดิมแล้ว push เพิ่ม (trigger `synchronize`); required check ค้าง
  'Expected — waiting' 0 runs → push commit เปล่าๆ เพื่อ retrigger (memory `ci-pr-opened-event-can-skip-run`)
