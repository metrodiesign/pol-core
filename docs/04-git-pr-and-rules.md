# 4. Git / PR + Rules

ต้นทางกฎทั้งหมด: `../CLAUDE.md` + `../.ai/shared/`. ที่นี่สรุปเชิงปฏิบัติ.

## 4.0 ชั้นบังคับจริง (Tier 1 = git hooks + CI)

**ตัวบังคับหลักคือ committed git hooks ใน `../.githooks/` + `../.github/workflows/ci.yml`** —
harness-agnostic จึงครอบทั้ง Claude, Codex, OpenCode, Pi และคนที่พิมพ์ `git` เอง.
hook ฝั่ง Claude (`../.claude/hooks/*`, ดู [05-hooks.md](05-hooks.md)) เป็น **ชั้นสะดวก
ระหว่าง session** ที่เตือนเร็วกว่าเท่านั้น — ไม่ใช่ตัวบังคับ และไม่ใช่ของแทนกันได้.

เปิดใช้ครั้งเดียวต่อ clone: `./.ai/bin/install.sh` (ตั้ง `core.hooksPath=.githooks`) —
agent รันคำสั่งนี้เองไม่ได้ (bypass guard block การแก้ `core.hooksPath`), ต้องให้สคริปต์/คนทำ.
ตรวจ: `git config core.hooksPath` -> `.githooks`.

| hook | block อะไร |
| --- | --- |
| `pre-commit` | (1) secret scan ของ staged diff ผ่าน `.ai/bin/check-secrets.sh` (2) ถ้ามี `*/tasks.md` staged: task ที่ diff นี้เพิ่ง mark `- [x]` ต้องมีบรรทัด `Evidence:` อยู่ใน block ของตัวเอง (ยืมของ task ข้างเคียงไม่ได้; task ที่ `[x]` อยู่ก่อนแล้วไม่ถูกตรวจซ้ำ) |
| `pre-push` | ตรวจแบบ **ref-based ไม่ใช่ regex ของ command** (อ่าน `<local ref> <local sha> <remote ref> <remote sha>` จาก stdin): push ตรงเข้า `refs/heads/main` / `refs/heads/develop`, ลบ remote ref, และ non-fast-forward (force) push ที่ remote sha ไม่ใช่ ancestor ของ local sha (`git merge-base --is-ancestor`) |

`pre-commit` **ตั้งใจไม่รัน test** — ให้เร็วพอที่จะไม่มีใครอยากใช้ `--no-verify`; test อยู่ใน CI.

## 4.1 Git / branch / PR

- **ห้าม push ตรงเข้า `main` / `develop`** — ต้องผ่าน PR เสมอ
- **ห้าม force push**, **ห้าม commit ตรงโดยไม่มี review**
- commit message ลงท้ายด้วย trailer รูปแบบ:
  `Co-Authored-By: Claude <model> <noreply@anthropic.com>` — `<model>` = โมเดลที่ทำ commit
  นั้นจริง (history มีทั้ง `Claude Sonnet 5`, `Claude Fable 5`, `Claude Opus 4.8 (1M context)`)
  จึง**ห้าม hardcode** ชื่อเดียวข้ามทุก session
- ฟีเจอร์/chore ทำบน branch แยก (เช่น `feat/<feature-name>`, `docs/...`) -> เปิด PR เข้า
  **`develop`** (base จริงของ work branch); `develop` -> `main` เป็นอีกชั้น
- บังคับจริงด้วย `.githooks/pre-push` (§4.0); `destructive-guard` hook ฝั่ง Claude block
  `git commit`/`push` บน main/develop + force push ให้เร็วกว่านั้นใน session (ดู
  [05-hooks.md](05-hooks.md))

## 4.2 CI gate

`../.github/workflows/ci.yml` ยิงบน PR + push เข้า `main`/`develop`, มี 5 job:

| job (`name:`)                            | ทำอะไร                                                                                                                                                              |
| ---------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `guards + spec-trace`                    | guard regression suite (`.claude/hooks/tests/*.test.sh`), secret scan ทั้ง tree (`--all`), rename identifier gate, spec-trace (ทุก REQ ต้องถูกอ้างใน design.md + tasks.md) |
| `dotnet build + test`                    | `dotnet restore` + `build -warnaserror` + `test --filter "Category!=Integration"` บน `pol-core.slnx`                                                                    |
| `docker build (api, migrate)`            | build 2 image target + `docker compose -f docker-compose.prod.yml config` กัน env drift                                                                              |
| `integration gate (SQL secret present?)` | เช็คว่ามี repo secret `MSSQL_SA_PASSWORD` ไหม -> ปล่อยเป็น output boolean                                                                                             |
| `dotnet integration (live SQL 2025)`     | รันเมื่อ gate = true: SQL Server 2025 จริง + bootstrap principal + migration + lineage gate + `Category=Integration`                                                    |

- ห้าม merge ข้าม failing check
- required check บน branch protection ของ `develop` = `CI / guards + spec-trace`
  (ดู [06-github-issues.md](06-github-issues.md) §6.5)
- **ยังไม่ได้บังคับใน CI** — อย่าอ้างว่ามีคนคุมให้: ไม่มี job lint แยก (วินัย analyzer/style
  มาทาง `-warnaserror` ของ build เท่านั้น), ไม่มี coverage threshold, ไม่มี guard สแกน test
  ที่ถูก disable ค้าง (xUnit `[Fact(Skip = ...)]`) — สามข้อนี้ยังเป็นวินัยคนล้วน

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
- repo นี้มี pipeline จริงแล้ว: `.github/workflows/mirror-gitlab.yml` mirror ทุก push บน
  `develop`/`main` + tag `v*` ไป GitLab, แล้ว `.gitlab-ci.yml` ถือ deploy 2 environment
  (`deploy-uat`, `deploy-prod` — manual ทั้งคู่) — ขั้นตอนจริงอยู่ที่
  [`runbooks/deploy-self-host.md`](runbooks/deploy-self-host.md) +
  [`runbooks/gitlab-cicd-setup.md`](runbooks/gitlab-cicd-setup.md)

## 4.7 Conventions ของโค้ด (ดู rules เต็ม)

canon อยู่ใน `../.ai/shared/` ทั้งหมด — `.claude/rules/*.md` เหลือเป็น stub ชี้มาที่นี่
(อย่าแก้ stub, แก้ที่ canon ที่เดียว):

| ด้าน            | สรุป                                                            | ไฟล์                                   |
| --------------- | --------------------------------------------------------------- | -------------------------------------- |
| โครงไฟล์/naming | โครงไฟล์ + convention การตั้งชื่อตามที่ project กำหนด           | `../.ai/shared/ARCHITECTURE.md`        |
| tech stack      | stack + hard constraints ที่ project เลือก                     | `../.ai/shared/CODING_STANDARDS.md`    |
| product         | ตัวผลิตภัณฑ์คืออะไรและทำไม                                      | `../.ai/shared/PROJECT_CONTEXT.md`     |
| บทเรียนสะสม     | กับดักจริงที่เจอแล้ว (อ่านก่อนงานคล้ายกัน)                     | `../.ai/shared/LESSONS.md`             |

## 4.8 Language / markdown

- คุยกับ user + output เป็นภาษาไทยเสมอ (ยกเว้น code/command/path/error/technical term)
- **ห้าม emoji ในไฟล์ `.md` ทุกชนิด**

## 4.9 Model routing

model/effort routing เป็นค่า **ต่อเครื่อง** อยู่ใน global `~/.claude/CLAUDE.md` — ไม่ pin ชื่อ
โมเดลไว้ในไฟล์นี้ (มันเปลี่ยนบ่อยกว่า repo). กฎที่ใช้ร่วมกัน:

- งานใหญ่/ใกล้ปิด หรือต้อง reasoning หนัก -> plan mode + ใส่คำว่า `ultrathink` ใน prompt
  (keyword จริงตัวเดียวที่ CC รู้จัก)
- error เดิมซ้ำ 2 ครั้ง -> หยุด Shift+Tab กลับ plan mode (อย่าด้นสด)
- งานแตะหลายไฟล์/หลายโมดูล -> Shift+Tab กลับ plan mode ก่อน
