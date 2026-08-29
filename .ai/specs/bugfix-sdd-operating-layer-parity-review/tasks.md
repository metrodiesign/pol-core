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

- [x] 3. ทำ strict all-spec coverage และ historical inventory ให้ซื่อสัตย์
  - **Scope**: ใช้ canonical historical inventory 61 directory, แยก current feature, และห้าม directory ที่ไม่ตรวจจริงมี `strictOk=true`
  - **Files**: `scripts/spec_contract.py`, `scripts/spec-retrofit.py`, tests ที่เกี่ยวข้อง และเอกสาร contract ที่นับ historical directory
  Satisfies: F-4, F-5, B-6
  - **Verify**: broken legacy fixture ทำ strict all-spec fail; report แยก checked/residual; canonical count เป็น historical 61 กับ current feature แยกกัน
  Evidence:
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest scripts.tests.test_spec_contract.StrictAllSpecCoverageTest scripts.tests.test_spec_retrofit.GuardedWriterTest scripts.tests.test_spec_retrofit.StrictHistoricalInventoryTest` -> `Ran 96 tests`; `OK`; ครบ two-owner same-batch ทั้ง helper, active-owner startup และ public apply interleaving โดยผู้แพ้คืน exact `MIGRATION_RECOVERY_REQUIRED` และไม่แตะ winner target/journal รวม dry-run JSON, report text, normal check, `final-all-spec` check, fd-bound recursive cleanup, parent mutation lock และ foreign-byte preservation ทุก destructive path
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s /Users/king_developer/Desktop/Project/pol-core/scripts/tests -p 'test_*.py'` -> `Ran 290 tests`; `OK`
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest scripts.tests.test_spec_retrofit.GuardedWriterTest.test_exclusive_journal_claim_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_initial_journal_missing_entry_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_mutating_cleanup_resume_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_read_only_cleanup_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_original_hash_mutation_is_killed scripts.tests.test_spec_retrofit.StrictHistoricalInventoryTest.test_report_truthfulness_mutations_are_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_durable_intent_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_atomic_swap_back_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_write_intent_owner_acquisition_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_cleanup_owner_acquisition_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_fd_bound_cleanup_mutation_is_killed scripts.tests.test_spec_retrofit.GuardedWriterTest.test_parent_mutation_lock_mutation_is_killed` -> `Ran 12 tests`; `OK`; exclusive owner, initial no-clobber, mutating/read-only cleanup, original hash, report truth, durable intent, atomic swap-back, write-intent owner, cleanup owner, fd-bound cleanup และ parent mutation lock mutations ถูก kill
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec-retrofit.py --check --batch final-all-spec` -> exit `1` ตาม contract; historical `61/61` checked, `53` failing, `0` unchecked, engine failure `0`, `currentFeature.legacyResidual=false`, outside residual labels `0`, aggregate `strictOk=false`
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py check --all --strict` -> exit `1` ตาม contract; `63` direct entries checked, `53` failing, `0` unchecked
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> exit `0`; criteria `178` ข้อผ่าน
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/repo_policy_alignment.py --check` -> exit `0`; source-to-assertion alignment ตรงทุก row
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; ไม่มี output
    - test: `git diff --check` -> exit `0`; ไม่มี output และ Task 7 trailing-whitespace lineคง SHA-256 prefix `ed2619284397`
    - viewports: n/a — tooling-only ไม่มี UI surface
    - deviations: historical residual `53` directories ยังคง strict-failing ตามขอบเขตที่ห้าม broad corpus migration; nonzero เป็น honest expected result ไม่ใช่ compatibility skip

- [x] 3A. ทำ existing-file writes และ recovery retirement ให้ crash-consistent
  - **Scope**: คง platform atomic exchange กับ durable per-write intent สำหรับ existing-file writer ทุก caller, retire recovery root แบบ append-only in place โดยไม่ลบ ย้าย หรือ rename root กับ children อัตโนมัติ, และทำ total resolver ให้ classify ทุก root ทุก batch ก่อน mutation, process stale valid root ครบตาม deterministic order แล้ว rescan ก่อน claim ใหม่
  - **Files**: `scripts/spec-retrofit.py`, `scripts/tests/test_spec_retrofit.py`
  Satisfies: F-4, F-5, B-6
  Depends on: 3
  - **Parent requirements**: `REQ-5.8`, `REQ-5.13`
  - **Order**: เขียน subprocess regression tests ให้แดงก่อนแก้ source แล้วจึง implement shared writer, generation resolver และ `_retire_claimed_recovery_root(claimed_fd, owner_lock_fd, operation)`
  - **Verify**: core writer ใช้ pipe handshake ส่ง `SIGKILL` หลัง intent fsync, exchange, target-directory fsync และ displaced-entry unlink; restart ต้องรักษา canonical basename, reconcile intent แบบ idempotent และ preserve foreign bytes
  - **Verify**: existing-file caller matrix ครบ apply target, recovery restore, journal manifest update และ resolution report overwrite โดยไม่พึ่ง `sleep`
  - **Verify**: retirement caller matrix ใช้ห้า caller exact ได้แก่ `_create_write_intent()`, `_delete_write_intent()`, `_remove_cleanup_tombstone()`, `_remove_incomplete_journals()` และ `clear_journal()` โดยทุก caller ผ่าน seam เดียวที่ไม่มี parent fd หรือ basename parameter
  - **Verify**: `_create_write_intent()` error ก่อน publish ใช้ `owner_lock_fd=None` ได้เฉพาะเมื่อไม่มี `.owner.lock`; อีกสี่ caller ต้องส่ง valid claimed owner lock fd และใช้ operation ตาม caller matrix ใน design
  - **Verify**: marker `.retired-v1` เป็น zero-byte regular single-link, owner-only และ durable หลัง fsync marker กับ claimed directory โดยไม่มี payload หรือ digest; malformed set คือ symlink, non-regular, hardlink หรือ `nlink != 1`, nonzero size, non-owner-only mode และ inode mismatch
  - **Verify**: pipe handshake ครอบ crash ก่อน marker create, หลัง marker entry, หลัง marker fsync และหลัง claimed-directory fsync; restart ต้องเห็น root เป็น active/recoverable หรือ retired เท่านั้นโดยไม่แตะ children
  - **Verify**: active owner ต้อง block ก่อน mutation และรักษา tree byte-identical; read-only modes ต้องไม่สร้าง marker, reconcile หรือ retire state และต้องคืน `MIGRATION_RECOVERY_REQUIRED` เมื่อมี active/recoverable generation
  - **Verify**: generation resolver scan/classify ทุก trusted journal-parent entry ก่อน claim, ignore `.mutation.lock` กับ valid retired root และอ่าน logical `batchId` จาก valid `manifest.json` เท่านั้น
  - **Verify**: opaque root ที่ไม่มี manifest เป็น global incomplete pre-manifest generation ไม่มี batch identity; stale valid owner retire ด้วย `incomplete-before-manifest`, active ownerคืน `MIGRATION_RECOVERY_REQUIRED`, malformed state คืน `MIGRATION_RECOVERY_FAILED` และ read-only คง tree byte-identical
  - **Verify**: exactly one stale unretired generation ที่มี manifest ต้อง recover/retire ก่อน claim; unretired generation มากกว่าหนึ่งตัวที่มี logical `batchId` เดียวกันคืน exact `MIGRATION_RECOVERY_FAILED`; เมื่อทุก state ถูกจัดการแล้วจึงสร้าง `.journal-<32hex>`, owner lock และ manifest state `preparing`
  - **Verify**: successful write ที่ต้อง claim generation อาจเพิ่ม retained root ตาม write จริง แต่ no-op invocation ต้องไม่เพิ่ม generation, marker หรือ retained bytes; ไม่มี auto-GC, TTL หรือ cap
  - **Verify**: Darwin ใช้ `renameatx_np(RENAME_SWAP)` และ Linux ใช้ `renameat2(RENAME_EXCHANGE)` ผ่าน stdlib `ctypes`; unsupported primitive หรือ filesystem ต้อง fail closed ก่อน canonical mutation
  - **Verify**: adversarial syscall tests ต้องแดงหาก retirement path เรียก `unlink`, `rmdir`, `rmtree`, rename หรือเปิด parent/name capability และ mutation proof ต้องแดงเมื่อตัด durable intent, atomic swap-back, owner acquisition, marker validation, generation uniqueness หรือ read-only preservation ออก
  Evidence:
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest scripts.tests.test_spec_retrofit.GuardedWriterTest` -> `Ran 103 tests`; `OK`
    - test: mutation proof 12 รายการของ `GuardedWriterTest` และ `StrictHistoricalInventoryTest` -> `Ran 12 tests`; `OK`
    - test: audit regression ครอบ swap-name reuse, manifest publish ก่อน owner lock, direct-child/original inventory และ owner lock `None` -> `Ran 6 tests`; `OK`
    - test: remaining `test_spec_retrofit` classes -> `Ran 30 tests`; `OK`
    - test: `test_spec_contract.py`, `test_guard_policy.py`, `test_guard_contract.py`, `test_ci_workflow_preservation.py`, `test_repo_policy_alignment.py` -> `Ran 182 tests`; `OK`
    - test: `python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> exit `0`; `178` criteria covered
    - test: `python3 scripts/repo_policy_alignment.py --check` -> exit `0`; source-to-assertion alignment exact
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; no output
    - test: `git diff --check` -> exit `0`; no output except known sandbox denial for `.env.prod.example`
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec-retrofit.py --check --batch final-all-spec` -> exit `1` by contract; `61/61` historical checked, `53` legacy residual failures, `0` unchecked
    - viewports: n/a — tooling-only
    - deviations: ไม่มี; full discovery command เกิน tool time limit จึงแยก run ทุก test file/class ได้รวม `315` tests และทุกชุดผ่าน

- [x] 4. แก้ initial-push Evidence range ผ่าน shared resolver
  - **Scope**: map initial-push zero SHA เป็น Git empty tree ใน owner script และให้ provider workflow เรียก owner เดียว
  - **Files**: `scripts/ci-evidence-scope.sh`, `.github/workflows/ci.yml`, `.gitlab-ci.yml`, `.claude/hooks/tests/ci-evidence-scope.test.sh`
  Satisfies: F-6, B-3
  Depends on: 3A
  - **Verify**: initial commit ที่ Evidence ถูกต้อง allow; Evidence ไม่ถูกต้องเป็น policy failure; normal push และ pull request range รักษา exact snapshot validation
  Evidence:
    - test: `bash .claude/hooks/tests/ci-evidence-scope.test.sh` -> `pass=10 fail=0`; ครอบ normal, initial zero SHA good/bad และ unchanged range
    - test: `bash -n scripts/ci-evidence-scope.sh && bash -n .claude/hooks/tests/ci-evidence-scope.test.sh` -> exit `0`
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_ci_workflow_preservation.py'` -> `Ran 13 tests`; `OK`
    - test: `git diff --check -- scripts/ci-evidence-scope.sh .claude/hooks/tests/ci-evidence-scope.test.sh .github/workflows/ci.yml .gitlab-ci.yml` -> exit `0`
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; no output
    - viewports: n/a — CI tooling
    - deviations: ไม่มี

- [x] 5. ทำให้ pane loop surface engine และ retrospective failures
  - **Scope**: แยก zero pending task ออกจาก engine failure, propagate subprocess return code และคง pane เมื่อ retrospective timeout หรือ failure
  - **Files**: `scripts/pane-loop.sh`, fixtures pane-loop ที่เกี่ยวข้อง
  Satisfies: F-7, B-4
  Depends on: 3A
  - **Verify**: engine nonzero, malformed graph และ retrospective timeout คืน nonzero โดยไม่เรียก clear; valid zero pending ยัง success พร้อม no-task
  Evidence:
    - test: `bash scripts/pane-loop.test.sh` -> `pass=8 fail=0`; engine error, zero pending และ retrospective timeout
    - test: `bash -n scripts/pane-loop.sh && bash -n scripts/pane-loop.test.sh` -> exit `0`
    - test: `git diff --check -- scripts/pane-loop.sh scripts/pane-loop.test.sh` -> exit `0`
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; no output
    - viewports: n/a — shell controller
    - deviations: ไม่มี

- [x] 6. ทำ shell fixture ให้ fail-fast เฉพาะ setup failure
  - **Scope**: ตรวจผล `mktemp` ใน fixture ทุกจุดที่พบ โดยไม่ใช้ blanket `set -e` จนทำ expected-negative assertion เสีย
  - **Files**: `.claude/hooks/tests/*` เฉพาะ fixtures ที่ใช้ `mktemp` และไม่มี failure guard
  Satisfies: F-8, B-5
  Depends on: 3A
  - **Verify**: PATH shim ที่ทำให้ `mktemp` คืน nonzero ต้องหยุด fixture ก่อน test body; expected-negative fixtures ยัง aggregate assertion ได้
  Evidence:
    - test: `bash .claude/hooks/tests/mktemp-fail-fast.test.sh` -> `pass=9 fail=0`
    - test: `bash .claude/hooks/tests/ci-evidence-scope.test.sh && bash .claude/hooks/tests/check-evidence.test.sh && bash .claude/hooks/tests/cross-harness-conformance.test.sh` -> `pass=10 fail=0`, `pass=8 fail=0`, `passed=18 failed=0`
    - test: `bash -n .claude/hooks/tests/mktemp-fail-fast.test.sh` -> exit `0`
    - test: `git diff --check -- .claude/hooks/tests` -> exit `0`
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; no output
    - viewports: n/a — shell fixtures
    - deviations: ไม่มี

- [x] 7. ลบ trailing whitespace และปิด verification record
  - **Scope**: ลบ whitespace ที่ `scripts/spec-retrofit.py` บรรทัด 606 และรัน checks ที่พิสูจน์ว่า bugfix ไม่ทิ้ง format regression
  - **Files**: `scripts/spec-retrofit.py`, tasks evidence
  Satisfies: F-9
  Depends on: 3A
  - **Verify**: `git diff --check` ต่อ diff ของงานนี้ exit 0; full relevant Python, shell และ spec-contract checks ผ่าน
  Evidence:
    - test: `git diff --check` -> exit `0`; no diff whitespace errors (known sandbox denial for `.env.prod.example` only)
    - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest scripts.tests.test_spec_retrofit.GuardedWriterTest` -> `Ran 103 tests`; `OK`
    - test: `bash scripts/pane-loop.test.sh && bash .claude/hooks/tests/mktemp-fail-fast.test.sh && bash .claude/hooks/tests/ci-evidence-scope.test.sh` -> `pass=8 fail=0`, `pass=9 fail=0`, `pass=10 fail=0`
    - test: `python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> exit `0`; `178` criteria covered
    - test: `.ai/bin/check-secrets.sh --all` -> exit `0`; no output
    - viewports: n/a — tooling-only
    - deviations: ไม่มี

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
