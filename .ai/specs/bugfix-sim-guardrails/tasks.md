# Tasks: bugfix-sim-guardrails
> Status: approved 2026-08-04

ลำดับบังคับ: T1 เปิดช่องสังเกตให้ harness ก่อน แล้ว T2 เขียน test ที่ต้อง RED แล้ว T3 ทำให้ GREEN
T4-T7 อิสระต่อกัน ทำขนานได้หลัง T3

- [x] T1. ขยาย observation channel ของ `docker/entrypoint.test.sh`
  - `docker/entrypoint.test.sh`: เพิ่ม `check_eq` และเปลี่ยน `run_entrypoint` ให้เรียกในรูป
    ที่จับทั้ง stdout, stderr และ exit code ได้ ตามต้นแบบ `docker/migrate-entrypoint.test.sh:120,130-132`
  - ห้ามแก้ assertion เดิม 21 ข้อ — สิ่งที่เพิ่มคือ helper ไม่ใช่การแก้ case
  - Verify: `sh docker/entrypoint.test.sh` ยังได้ `pass=21 fail=0` หลังเพิ่ม helper
     Satisfies: F-9
  Evidence:
    - test: `sh docker/entrypoint.test.sh` -> pass=21 fail=0 (เพิ่มแค่ `check_eq` helper — ยังไม่มี case ใดเรียกมัน, `run_entrypoint` ไม่ต้องแก้ definition เพราะ exit code/stderr จับได้อยู่แล้วผ่าน `$?` + `2>&1` ที่ call site ตามต้นแบบ migrate-entrypoint.test.sh:130-132)
    - viewports: n/a — logic-only shell script, ไม่มี UI
    - deviations: none
- [x] T2. repro test ของ D1 (ต้อง RED)
  - `docker/entrypoint.test.sh`: เพิ่ม case หลังกลุ่ม external-sim (`:117`) ก่อนกลุ่ม invariant
  - case ที่ต้องมี: Motor ชนกับ HIPPO, NonMotor ชนกับ MAMMOTH, hybrid ที่ตั้ง Motor
    อย่างเดียวแล้วต้อง exit 0, empty string ทั้งสองฝั่งที่ต้องนับเป็น unset
  - case ของ stderr: ต้องมีชื่อ env var ทั้งสองตัว และต้องไม่มี password กับ connection string
  - Verify: `sh docker/entrypoint.test.sh` แดงที่ case ใหม่ทุกข้อ และ case เดิม 21 ข้อยังเขียว
     Satisfies: F-1, F-2, F-3, F-4, F-5
  Evidence:
    - test: `sh docker/entrypoint.test.sh` RED ก่อนแก้ entrypoint.sh -> pass=32 fail=6; แดงที่ `motor conflict: non-zero exit`, `motor conflict: stderr names HIPPO_DB_SERVER`, `motor conflict: stderr names SpDocument__MotorConnectionString`, `nonmotor conflict: non-zero exit`, `nonmotor conflict: stderr names MAMMOTH_DB_SERVER`, `nonmotor conflict: stderr names SpDocument__NonMotorConnectionString` — case เดิม 21 ข้อ + F3/F4 cases ยังเขียวหมด
    - viewports: n/a — logic-only shell script, ไม่มี UI
    - deviations: F5 assertions สองตัว ("no password value"/"no connection string value") ไม่แดงในรอบ RED นี้เพราะ bug D1 ทับค่า preset ไปแล้วก่อนตรวจ (ค่าที่ควรถูก forbid หายไปพร้อม bug) — ไม่ใช่ test เขียนผิด core requirement ของ F1/F2 (exit non-zero + stderr มีชื่อตัวแปร) แดงตรงตามคาดครบ 6 จุด
- [x] T3. precedence guard ใน `docker/entrypoint.sh` (ทำให้ T2 GREEN)
  - `docker/entrypoint.sh:38-45`: เช็คแยกคู่ Motor/HIPPO และ NonMotor/MAMMOTH ตั้งทั้งคู่
    ในคู่เดียวกัน = เขียน stderr แล้ว exit non-zero; ตั้งข้างเดียว = ค่า pre-set ชนะ
  - empty string นับเป็น unset ทั้งสองฝั่ง ตามบรรทัดฐาน `docker/entrypoint.sh:20`
  - ข้อความ error ระบุได้เฉพาะชื่อตัวแปร ห้ามพ่นค่า (`build_conn` ประกอบ `$DB_PW` เข้าสตริง)
  - Verify: `sh docker/entrypoint.test.sh` เขียวทั้งไฟล์ + `docker compose -f docker-compose.prod.yml config`
    ด้วยชุด placeholder ของ `.github/workflows/ci.yml:115-128` render สำเร็จ + `docker compose up migrate`
    บน local จบ exit 0
     Satisfies: F-1, F-2, F-3, F-4, F-5, B-1, B-2, B-3, B-4, B-5, B-6, B-7, B-8, B-9, B-10
  Evidence:
    - test: `sh docker/entrypoint.test.sh` -> pass=38 fail=0 (21 เดิม + 17 ใหม่จาก T2, ทุกข้อเขียว); mutation check: ย้อน `docker/entrypoint.sh` เป็น pre-fix (HEAD) ชั่วคราว -> `pass=32 fail=6` ตรงกับผล RED ของ T2 เป๊ะทุก case, คืนไฟล์ -> กลับมา `pass=38 fail=0`; `docker compose -f docker-compose.prod.yml config` ด้วยชุด placeholder `.github/workflows/ci.yml:115-128` -> exit 0 render สำเร็จ; `sh -n docker/entrypoint.sh` -> syntax OK
    - viewports: n/a — logic-only shell script, ไม่มี UI
    - deviations: ข้าม `docker compose up migrate` บน local — เหตุผล 2 ข้อ: (1) service `migrate` เรียก `docker/migrate-entrypoint.sh` คนละไฟล์กับ guard ที่แก้ (`docker/entrypoint.sh` เป็นของ service `api`) จึงไม่ exercise โค้ดที่เปลี่ยนแม้รันผ่าน (2) เครื่องนี้ไม่มีไฟล์ `secrets/db_ca_cert` ที่ compose ประกาศให้ service `migrate` mount (`secrets: [pol_app_password, db_ca_cert]`) — มีแค่ `pol_app_password` — สร้างไฟล์ secret ใหม่เป็นการ setup environment นอกขอบเขต "แตะแค่ entrypoint.sh + entrypoint.test.sh" ของ T1-T3 จึงไม่ทำโดยไม่ถามก่อน `docker compose config` (parse ทั้ง YAML รวม service `api` ที่ใช้ entrypoint.sh) + `sh -n` (syntax ของตัวสคริปต์เอง) ทดแทนเป็นหลักฐานว่า pipeline ไม่พัง

- [x] T4. ปิด invariant gap ของสาย sim
  - `docker/entrypoint.test.sh:119-124`: ขยาย invariant trust flag ให้ครอบ `$out_hippo`
    และ `$out_mammoth`
  - เพิ่ม scenario ใหม่: `HIPPO_DB_SERVER` และ `MAMMOTH_DB_SERVER` คู่กับ `DB_CA_CERTIFICATE_FILE`
    แล้ว assert `Encrypt=Strict` ครบชุดเหมือนสาย App (`:77-83`)
  - Verify (mutation): แก้ `docker/entrypoint.sh:40` ให้ประกอบสตริงเองพร้อม `TrustServerCertificate=True`
    โดยไม่เรียก `build_conn` แล้ว suite ต้องแดง — ปัจจุบัน `pass=21 fail=0` ไม่ขยับ; คืนไฟล์แล้วต้องเขียว
     Satisfies: F-7, F-8
> ของ T4 เพราะ F5 core requirement ถูก T2/T3 satisfy ไปแล้ว รอบนี้คือการเสริม coverage ของ assertion เดิม ไม่ใช่ requirement ใหม่ (2) ตำแหน่งของ scenario ใหม่ (F8) วางไว้ในกลุ่ม external-sim เดิม (หลัง `out_mammoth`, ก่อนกลุ่ม comment D1) แทนที่จะต่อท้ายไฟล์ — spec ไม่ได้ระบุตำแหน่ง เลือกจุดนี้เพราะเนื้อหาเป็นเรื่อง connection-string assembly ของสาย sim เหมือนกลุ่มนั้น ไม่ใช่เรื่อง conflict/precedence ของ D1




  Evidence:
    - test: `sh docker/entrypoint.test.sh` -> pass=50 fail=0 (38 เดิม + 12 ใหม่: 8 จาก sim-strict scenario F8 + 2 จาก invariant expansion F7 + 2 จาก F5 coverage-gap fix, MAJOR ของ auditor); mutation C (F7 — hippo ประกอบเองไม่เรียก `build_conn` พร้อม `TrustServerCertificate=True`) -> pass=45 fail=5 (invariant: hippo branch + sim strict hippo x4); mutation strict (F8 — สาย sim ตกไป fallback branch ทั้งที่ตั้ง `DB_CA_CERTIFICATE_FILE`) -> pass=42 fail=8 (sim strict hippo x4 + mammoth x4); mutation DB_PW (F5 MAJOR — error message เรียก `build_conn` จริงจน `$DB_PW` หลุด stderr ตามหลักฐานของ auditor) -> pass=48 fail=2 (motor+nonmotor conflict "no real DB_PW secret"); ทุก mutation ทำบน `docker/entrypoint.sh` ชั่วคราวแล้ว restore จาก backup ใน scratch dir ทันที ยืนยันด้วย `diff` ตรงกับ backup ทุกรอบ และ `git diff --stat -- docker/entrypoint.sh` คงที่ที่ 8 insertions เดิมของ T3 ตลอด ไม่มีการเปลี่ยนแปลงหลงเหลือ
    - viewports: n/a — logic-only shell script, ไม่มี UI
    - deviations: (1) เพิ่ม assertion ปิด MAJOR finding ของ auditor นอกเหนือจากขอบเขต F7/F8 เดิมของ T4 — auditor พิสูจน์ด้วย mutation จริงว่า F5 assertion เดิม (T2/T3) ครอบเฉพาะค่าที่ test ตั้งเอง (`Password=x`/`Password=y`, `motordb.real`/`centerdb.real`) แต่ไม่ครอบ `$DB_PW` ซึ่งเป็น secret ตัวจริงจาก `DB_PASSWORD_FILE` (fixture `s3cret`) เพิ่ม `check_not_contains ... "s3cret"` เข้าทั้ง motor และ nonmotor conflict block (`entrypoint.test.sh:159,169`) ปิดช่องนี้ — ไม่เพิ่ม "F5" เข้า `Satisfies:` ของ T4 เพราะ F5 core requirement ถูก T2/T3 satisfy ไปแล้ว รอบนี้คือการเสริม coverage ของ assertion เดิม ไม่ใช่ requirement ใหม่ (2) ตำแหน่งของ scenario ใหม่ (F8) วางไว้ในกลุ่ม external-sim เดิม (หลัง `out_mammoth`, ก่อนกลุ่ม comment D1) แทนที่จะต่อท้ายไฟล์ — spec ไม่ได้ระบุตำแหน่ง เลือกจุดนี้เพราะเนื้อหาเป็นเรื่อง connection-string assembly ของสาย sim เหมือนกลุ่มนั้น ไม่ใช่เรื่อง conflict/precedence ของ D1

- [x] T5. แก้เอกสารสี่จุดให้ตรงความจริง
  - `src/Modules/Products/Products.Infrastructure/Sp/SpDocumentOptions.cs:12-13`,
    `src/Hosts/Api/Program.cs:155-156`, `docs/reference/products.md:139-141`,
    comment `docker/entrypoint.sh:31-37`
  - เนื้อหาต้องระบุครบสี่ชั้น: ไม่มี key `SpDocument__*` ใน `docker-compose.prod.yml`,
    `:?` ที่ `:76,78`, sim ที่ `migrate-entrypoint.sh:12-13,73-83` บังคับ, catalog กับ principal
    ที่ `build_conn` hardcode — และระบุพฤติกรรมใหม่ของ guard
  - ห้ามคงประโยค "override สองค่าและไม่มีอย่างอื่น" ไว้ในรูปใด
  - Verify: `rg -n 'config override|เหลือแค่เปลี่ยน' src docs docker` ไม่คืนบรรทัดที่ยังสัญญาแบบเดิม
     Satisfies: F-6
  Evidence:
    - test: `rg -n 'config override|เหลือแค่เปลี่ยน|and nothing else|not a code change' src docs docker` -> คืน 4 บรรทัดที่ไม่เกี่ยวกับ D1/cutover เลย (`ListProducts.cs:262` payment-status local var, `Program.cs:1047` health-check response payload, `WriteAuthorizers.cs:239` login-flow allowlist, `MerchantUserRoleRepository.cs:13` query-filter scope) — ไม่มีบรรทัดใดสัญญา "override สองค่าและไม่มีอย่างอื่น" แบบเดิมอีก; `dotnet build src/Modules/Products/Products.Infrastructure` -> 7 projects, 0 errors, 0 warnings; `dotnet build src/Hosts/Api` -> 46 projects, 0 errors, 0 warnings
    - viewports: n/a — docs/comment only
    - deviations: (1) พบ `docs/reference/products.md:105-108` มีประโยคสั้น ๆ สื่อความผิดแบบเดียวกัน ("วันเชื่อมของจริงเปลี่ยนแค่ connection string") แต่ไม่อยู่ใน 4 จุดที่ spec ระบุ (`:139-141` เท่านั้น) และไม่ match rg pattern ของ DoD ("เปลี่ยนแค่" ≠ "เหลือแค่เปลี่ยน") — ไม่แตะตามขอบเขตที่สั่งชัดเจนว่า "แตะได้ 4 จุดนี้เท่านั้น" รายงานไว้เผื่อต้องเปิด task แก้เพิ่ม (2) ระหว่างแก้ `docker/entrypoint.sh` เจอ transient revert หนึ่งครั้ง — comment ที่ insert หายไปจาก disk ทันทีหลัง Edit สำเร็จ ตรวจสอบพบว่า T4 (agent อื่นที่ทำงานคู่ขนานตาม tasks.md `T4-T7 อิสระต่อกัน ทำขนานได้หลัง T3`) กำลังรัน mutation-test บน `docker/entrypoint.sh` ด้วย backup/restore กลับไปที่ snapshot ก่อนหน้า T5 ชนกับ Edit ของผมพอดี — retry Edit ครั้งที่สองแล้ว verify ด้วย `git diff --stat` ทันที คงอยู่ถาวรหลัง T4 ปิดงาน ไม่กระทบเนื้อหาสุดท้าย

- [x] T6. เอา shell test suites เข้า CI job `verify`
  - `.github/workflows/ci.yml`: เพิ่ม step รัน `docker/entrypoint.test.sh` และ
    `docker/migrate-entrypoint.test.sh` ในกลุ่มเดียวกับ `.claude/hooks/tests/*.test.sh` (`:31`)
  - ไม่แตะ step อื่นของ `verify` และไม่แตะ job `dotnet` กับ `docker-build`
  - Verify: push แล้ว job `guards + spec-trace` เขียวและ log แสดงผลรันทั้งสอง suite;
    ทดสอบ negative ด้วยการทำให้ suite แดงชั่วคราวแล้วยืนยันว่า job แดงตาม
     Satisfies: F-10, B-13, B-14
  Evidence:
    - test: `bash .claude/hooks/tests/*.test.sh docker/entrypoint.test.sh docker/migrate-entrypoint.test.sh` (loop เดียวกับ step "Guard regression tests" ที่แก้ที่ ci.yml:34) -> รวม 9 ไฟล์ pass=362 fail=0 skip=10 (skip เดิมของ destructive-guard.test.sh ผูกกับ branch=develop ไม่เกี่ยวกับ T6); แยกสองตัวที่ T6 เพิ่มเข้า array: `docker/entrypoint.test.sh` pass=38 fail=0, `docker/migrate-entrypoint.test.sh` pass=43 fail=0 (เขียวทั้งคู่โดยไม่แก้ตัว test เอง ตรง B14)
    - viewports: n/a — logic-only shell/YAML, ไม่มี UI
    - deviations: เพิ่มเข้า `tests=(...)` array เดิมที่ ci.yml:34 (step เดียวกับ `.claude/hooks/tests/*.test.sh`) แทนการสร้าง step ใหม่แยกต่างหาก — spec ชี้ตำแหน่งตรง `:31` (บรรทัด tests array) และ "กลุ่มเดียวกับที่รัน" ตรงกับรวม loop เดียวกันมากกว่าแค่ job เดียวกัน reuse logic เดิม (loop/group/status) แทน duplicate; ปรับ comment ของ step ให้ตรงความจริงใหม่ด้วย (เดิมพูดถึงเฉพาะ .claude/hooks/tests/ เท่านั้น)

- [x] T7. ทำให้ `dotnet-integration` fail-closed
  - `.github/workflows/ci.yml`: ลบ job `integration-gate` (`:130-141`) และลบ `needs:` กับ `if:`
    ของ `dotnet-integration` (`:144-145`) ทิ้ง
  - ห้ามแก้ `name:` ของ job ใดที่อยู่ใน required contexts และห้ามแตะลำดับ step ใน job นั้น
  - เพิ่ม guard กัน regression: test ที่ fail เมื่อ `dotnet-integration` กลับมามี `needs:` หรือ `if:`
    วางไว้ที่ `.claude/hooks/tests/` ให้ job `verify` รันตาม T6
  - Verify: `gh api repos/:owner/:repo/branches/develop/protection` ยังคืน contexts สามตัวเดิม;
    รัน CI จริงแล้ว `dotnet integration (live SQL 2025)` ขึ้นเป็น success ไม่ใช่ skipped;
    guard แดงเมื่อลองใส่ `if:` กลับเข้าไป
     Satisfies: F-11, F-12, B-11, B-12
  Evidence:
    - test: `bash .claude/hooks/tests/ci-dotnet-integration-fail-closed.test.sh` (ไฟล์ใหม่) -> pass=3 fail=0; negative-proof ทิศ `if:`: ใส่ `if: ${{ always() }}` กลับที่ `dotnet-integration` -> pass=2 fail=1 (FAIL job-level 'if:' found), เอาออก -> pass=3 fail=0; negative-proof ทิศ `needs:`: ใส่ `needs: dotnet` กลับ -> pass=2 fail=1 (FAIL job-level 'needs:' found), เอาออก -> pass=3 fail=0; `ruby -ryaml -e "YAML.load_file('.github/workflows/ci.yml')"` -> parse OK (เครื่องนี้ไม่มี PyYAML ใช้ ruby ตาม "เครื่องมือเทียบเท่า" ที่อนุญาตไว้); เทียบ `git show HEAD:...ci.yml` กับ working tree ด้วย grep job-key+name: -> ก่อนแก้ 5 jobs (verify/dotnet/docker-build/integration-gate/dotnet-integration), หลังแก้ 4 jobs (integration-gate หายไปตามสั่ง), `name:` ทั้ง 4 job ที่เหลือตรงเดิมทุกตัวอักษร; `gh api repos/:owner/:repo/branches/develop/protection --jq '.required_status_checks.contexts'` (read-only) -> `["guards + spec-trace","dotnet build + test","dotnet integration (live SQL 2025)"]` ตรง B12
    - viewports: n/a — logic-only shell/YAML, ไม่มี UI
    - deviations: (1) การรัน CI จริงบน GitHub แล้วยืนยัน `dotnet integration (live SQL 2025)` ขึ้น success ไม่ใช่ skipped (ตามที่ Verify ของ task นี้ระบุ) ทำไม่ได้ในรอบนี้เพราะ Coder ถูกห้าม push/commit ตรง ๆ — F11 จึงยังไม่ถูก proof แบบ end-to-end บน CI จริง แต่ถูก cover ทางอ้อม: ไม่มี `if:`/`needs:` เหลือแล้วจึงไม่มี path ใดที่ resolve เป็น `skipped` ได้อีก ต้องรอรอบ push จริงเพื่อยืนยัน F11 ปิดสนิท (2) comment ที่เหลือใต้ `dotnet-integration:` ("Add this to branch protection once the secret is configured") เป็นข้อความล้าสมัย (secret + branch protection ตั้งไปแล้วจริงตามผล gh api ข้างต้น) แต่ไม่ได้แก้เพราะ T7 ระบุจำเพาะแค่ลบ job `integration-gate` และลบสองบรรทัด `needs:`/`if:` เท่านั้น ไม่ได้สั่งแก้ comment นี้ — รายงานไว้เผื่อต้องเปิด task แก้เอกสารเพิ่มภายหลัง
