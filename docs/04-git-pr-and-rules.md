# 4. Git / PR + Rules

ต้นทางกฎทั้งหมด: `../CLAUDE.md` + `../.claude/rules/`. ที่นี่สรุปเชิงปฏิบัติ.

## 4.1 Git / branch / PR

- **ห้าม push ตรงเข้า `main` / `develop`** — ต้องผ่าน PR เสมอ
- **ห้าม force push**, **ห้าม commit ตรงโดยไม่มี review**
- commit message ลงท้ายด้วย:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- ฟีเจอร์/chore ทำบน branch แยก (เช่น `feat/<feature-name>`, `docs/...`) -> เปิด PR เข้า
  **`develop`** (base จริงของ work branch); `develop` -> `main` เป็นอีกชั้น
- `destructive-guard` hook block `git commit`/`push` ขณะอยู่บน main/develop + force push ให้
  อัตโนมัติ (ดู [05-hooks.md](05-hooks.md))

## 4.2 CI gate

- PR merge ได้เมื่อ CI ผ่าน (test + lint) เป็น required check
- ห้าม merge ข้าม failing check
- ห้าม commit `.only` / `.skip` ค้างใน test
- coverage ห้ามต่ำกว่าเกณฑ์

## 4.3 Secrets

- ห้าม commit secret ทุกชนิด (API key, token, password, private key, connection string)
- `.env` / `.env.*` อยู่ใน `.gitignore` เสมอ; commit ได้แค่ `.env.example` (ค่าปลอม)
- ห้าม hardcode credential — อ่านจาก env / secret manager
- secret หลุด -> rotate/revoke ทันที (ลบ commit ไม่พอ history ยังมี)
- บังคับด้วย hook: `secret-guard` (git pre-commit) scan ก่อน commit + `hook-bypass-guard` กัน
  การข้าม (`--no-verify` / `core.hooksPath` / `SECRET_GUARD_SKIP`) — ดู [05-hooks.md](05-hooks.md)

## 4.4 Destructive ops

- ห้าม `DROP`/`DELETE`/`TRUNCATE` บน prod โดยไม่มี WHERE + ไม่ยืนยัน
- ห้าม `rm -rf`, `git reset --hard`, `git clean -fd` โดยไม่ยืนยันเป้าหมาย
- DB migration ต้องมี rollback + backup ก่อนรัน prod
- บังคับด้วย hook: `destructive-guard` (PreToolUse Bash) block `rm` recursive+force,
  `git reset --hard`, `git clean -f`, `find -delete`, force push อัตโนมัติ — ดู [05-hooks.md](05-hooks.md)

## 4.5 Dependency

- ห้ามเพิ่ม dependency ใหม่โดยไม่ review license + maintenance + ขออนุมัติ
- lock file ของ project (เช่น `package-lock.json`) commit เสมอ
- ห้าม pin floating (`*` / `latest`) บน prod dep
- audit ช่องโหว่ของ dependency เป็นนโยบาย; ใช้ audit ของ ecosystem นั้น และ **ห้าม
  รัน auto-fix แบบ force** ที่ยอม downgrade/แก้ breaking ให้อัตโนมัติ

## 4.6 Deploy / release

- prod deploy ต้องผ่าน staging ก่อน
- ทุก release มี rollback plan + tag เวอร์ชัน + changelog
- ห้าม deploy prod ศุกร์เย็น/ก่อนวันหยุดยาว (ยกเว้น hotfix ฉุกเฉิน)

## 4.7 Conventions ของโค้ด (ดู rules เต็ม)

| ด้าน            | สรุป                                                            | ไฟล์                            |
| --------------- | --------------------------------------------------------------- | ------------------------------- |
| โครงไฟล์/naming | โครงไฟล์ + convention การตั้งชื่อตามที่ project กำหนด           | `../.claude/rules/structure.md` |
| tech stack      | stack + hard constraints ที่ project เลือก                     | `../.claude/rules/tech.md`      |
| product         | ตัวผลิตภัณฑ์คืออะไรและทำไม                                      | `../.claude/rules/product.md`   |
| บทเรียนสะสม     | กับดักจริงที่เจอแล้ว (อ่านก่อนงานคล้ายกัน)                     | `../.claude/rules/lessons.md`   |

## 4.8 Language / markdown

- คุยกับ user + output เป็นภาษาไทยเสมอ (ยกเว้น code/command/path/error/technical term)
- **ห้าม emoji ในไฟล์ `.md` ทุกชนิด**

## 4.9 Model routing

default = **Opus 4.8 (1M context)** (ดู global `~/.claude/CLAUDE.md`):

- งานใหญ่/ใกล้ปิด หรือต้อง reasoning หนัก -> plan mode + ใส่คำว่า `ultrathink` ใน prompt
  (keyword จริงตัวเดียวที่ CC รู้จัก) หรือยก `/effort` เป็น `xhigh` ชั่วคราว
- error เดิมซ้ำ 2 ครั้ง -> หยุด Shift+Tab กลับ plan mode (อย่าด้นสด)
- งานแตะหลายไฟล์/หลายโมดูล -> Shift+Tab กลับ plan mode ก่อน
