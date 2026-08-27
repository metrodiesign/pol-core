# งานแก้ final review ของ SDD operating layer

> Status: approved 2026-08-27

เอกสารนี้แบ่ง bugfix เป็น 8 งานตามกลไกที่ผิดจริง ทุกงานต้องเพิ่ม regression proof ก่อนประกาศผ่าน และห้ามขยาย policy หรือ behavior นอกเกณฑ์ใน `bugfix.md`

## Implementation tasks

- [x] 1. ปิดช่องว่าง protection ของ guard source owner และส่ง argument ให้ครบ
  - **Scope**: เพิ่ม source owner ของ guard เข้า protected-path classifier และทำให้ bypass wrapper ส่ง caller argument vector ครบ
  - **Files**: `scripts/guard_policy.py`, `scripts/guard_contract.py`, `.ai/bin/check-bypass.sh`, `scripts/tests/test_guard_policy.py`, fixtures guard ที่เกี่ยวข้อง
  Satisfies: F-1, B-1
  - **Verify**: protected-path tests ครอบ source owner และ wrapper; benign quoted data, read-only query และ copy-from ยัง allow
  Evidence:
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_guard_policy.py'` -> `Ran 19 tests`; `OK`
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_guard_contract.py'` -> `Ran 14 tests`; `OK`
    - test: `bash .claude/hooks/tests/hook-bypass-guard.test.sh` -> `pass=94 fail=0`
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> `Ran 184 tests`; `OK`
    - test: `fixture_rc=0; for fixture in .claude/hooks/tests/*.test.sh; do bash "$fixture" || fixture_rc=1; done; exit "$fixture_rc"` -> fixture ถูก execute `12/12`; `aggregated_exit=0`
    - test: `git diff --check` -> exit `0`; ไม่มี output
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; ไม่มี output
    - test: `python3 scripts/spec_contract.py check --all --strict` -> `9 active / 54 legacy-residual / 0 failing`
    - viewports: n/a — logic-only
    - deviations: ไม่มี
    - environment: shell fixture inventory รอบแรกใน sandbox ได้ `Operation not permitted`; retry คำสั่งเดิมนอก sandbox แล้วผ่าน `12/12` — เป็นข้อจำกัด environment ไม่ใช่ deviation ของ behavior

- [x] 2. Wire phase gate และ feature slice ใน canonical spec skills
  - **Scope**: ให้ requirement/design/tasks skills เรียก phase gate ก่อน advance; implementation เรียก slice และ full-read เมื่อพบ `MISSING:`; แก้ status ของ quick workflow ให้ใช้ grammar canonical
  - **Files**: `.claude/skills/spec-*/SKILL.md`, source assertions และ fixtures ที่เกี่ยวข้อง
  Satisfies: F-2, F-3, B-2
  - **Verify**: source-to-assertion matrix ครบทุก canonical phase; malformed, unapproved และ missing upstream ถูก block ก่อน write
  Evidence:
    - test: `python3 -m unittest scripts.tests.test_repo_policy_alignment.PhaseSkillsRowTest` ก่อนแก้ source รอบ rework 3 -> `Ran 14 tests`; exit `1`; failures `57/57` ตรง command `9` occurrences และ semantic/status `10` spans ภายใต้ wrapper `3` classes
    - test: `python3 -m unittest scripts.tests.test_repo_policy_alignment.PhaseSkillsRowTest` -> `Ran 15 tests`; outer wrapper mutations `57/57` ถูก reject และ unclosed backtick/tilde/HTML comment ทั้ง `3/3` ถูก reject; `OK`
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_repo_policy_alignment.py'` -> `Ran 45 tests`; `OK`
    - test: selector family บน approved bugfix จริง -> exact `2` exit `0`; range `1-2` exit `0`; all ผ่าน `--pending` คืน `3,4,5,6,7`; unknown exit `1` ด้วย `TASK_SELECTOR_AMBIGUOUS`
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> `Ran 199 tests`; `OK`
    - test: shell fixture inventory -> sandbox exit `1` จาก `mktemp: Operation not permitted`; retry คำสั่งเดียวกันนอก sandboxได้ `fixture_count=12 aggregate_exit=0`; slice fixture แสดง `PASS`
    - test: `python3 scripts/repo_policy_alignment.py --check` -> `OK: source-to-assertion alignment ตรงทุก row`
    - test: `python3 scripts/spec_contract.py check --all --strict` -> `9 active / 54 legacy-residual dirs / 0 failing`
    - test: `python3 scripts/spec_contract.py gate phase --feature bugfix-sdd-operating-layer-parity-review --phase implement --workflow bugfix` -> phase `implement` ผ่าน workflow `bugfix`
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; ไม่มี output
    - test: `git diff --check` -> exit `0`; ไม่มี output
    - viewports: n/a — tooling-only ไม่มี UI surface
    - deviations: ไม่มี
    - environment: shell fixture inventory ใช้ unsandboxed retry ตามข้อจำกัด `mktemp` ที่บันทึกใน pipeline state แล้ว

- [ ] 3. ทำ strict all-spec coverage และ historical inventory ให้ซื่อสัตย์
  - **Scope**: ใช้ canonical historical inventory 61 directory, แยก current feature, และห้าม directory ที่ไม่ตรวจจริงมี `strictOk=true`
  - **Files**: `scripts/spec_contract.py`, `scripts/spec-retrofit.py`, tests ที่เกี่ยวข้อง และเอกสาร contract ที่นับ historical directory
  Satisfies: F-4, F-5, B-6
  - **Verify**: broken legacy fixture ทำ strict all-spec fail; report แยก checked/residual; canonical count เป็น historical 61 กับ current feature แยกกัน

- [ ] 4. แก้ initial-push Evidence range ผ่าน shared resolver
  - **Scope**: map initial-push zero SHA เป็น Git empty tree ใน owner script และให้ provider workflow เรียก owner เดียว
  - **Files**: `scripts/ci-evidence-scope.sh`, `.github/workflows/ci.yml`, `.gitlab-ci.yml`, `.claude/hooks/tests/ci-evidence-scope.test.sh`
  Satisfies: F-6, B-3
  - **Verify**: initial commit ที่ Evidence ถูกต้อง allow; Evidence ไม่ถูกต้องเป็น policy failure; normal push และ pull request range รักษา exact snapshot validation

- [ ] 5. ทำให้ pane loop surface engine และ retrospective failures
  - **Scope**: แยก zero pending task ออกจาก engine failure, propagate subprocess return code และคง pane เมื่อ retrospective timeout หรือ failure
  - **Files**: `scripts/pane-loop.sh`, fixtures pane-loop ที่เกี่ยวข้อง
  Satisfies: F-7, B-4
  - **Verify**: engine nonzero, malformed graph และ retrospective timeout คืน nonzero โดยไม่เรียก clear; valid zero pending ยัง success พร้อม no-task

- [ ] 6. ทำ shell fixture ให้ fail-fast เฉพาะ setup failure
  - **Scope**: ตรวจผล `mktemp` ใน fixture ทุกจุดที่พบ โดยไม่ใช้ blanket `set -e` จนทำ expected-negative assertion เสีย
  - **Files**: `.claude/hooks/tests/*` เฉพาะ fixtures ที่ใช้ `mktemp` และไม่มี failure guard
  Satisfies: F-8, B-5
  - **Verify**: PATH shim ที่ทำให้ `mktemp` คืน nonzero ต้องหยุด fixture ก่อน test body; expected-negative fixtures ยัง aggregate assertion ได้

- [ ] 7. ลบ trailing whitespace และปิด verification record
  - **Scope**: ลบ whitespace ที่ `scripts/spec-retrofit.py` บรรทัด 606 และรัน checks ที่พิสูจน์ว่า bugfix ไม่ทิ้ง format regression
  - **Files**: `scripts/spec-retrofit.py`, tasks evidence
  Satisfies: F-9
  - **Verify**: `git diff --check` ต่อ diff ของงานนี้ exit 0; full relevant Python, shell และ spec-contract checks ผ่าน

- [x] 8. คืน executable mode ให้ task snapshot hook
  - **Scope**: ทำให้ configured `task-snapshot.sh` execute ได้จริงก่อน task gate ใช้ snapshot และเพิ่ม proof ว่า non-task edit ไม่สร้าง snapshot
  - **Files**: `.claude/hooks/task-snapshot.sh`, test หรือ verification record ที่พิสูจน์ mode และ capture behavior
  Satisfies: F-10, B-7
  - **Verify**: hook file executable; simulated pre-tool input ต่อ `tasks.md` สร้าง snapshot; input ที่ไม่ใช่ `tasks.md` ไม่สร้าง snapshot
  Evidence:
    - test: `git ls-files --stage .claude/hooks/task-snapshot.sh` -> mode `100755`
    - test: direct pre-tool hook input ต่อ `tasks.md` -> `task snapshot capture: OK`
    - test: direct non-task input ต่อ hook -> `non-task input: no snapshot change`
    - viewports: n/a — hook mode และ filesystem behavior
    - deviations: ไม่มี

## Completion rules

- ทุก task ต้องมี `Evidence:` จากคำสั่งที่รันจริงก่อนเปลี่ยนเป็น `[x]`
- ทุก finding ระดับ policy หรือ engine failure ต้องมี test ที่ยืนยัน observable failure ก่อนและ behavior หลังแก้
- หลังทุก task ที่ผ่าน gate ให้ commit checkpoint แยกบน feature branch โดยไม่ push
- ก่อน ship ต้องรัน full build, full test, strict spec check, review และตรวจว่าไม่มี `.env*` ถูก stage
