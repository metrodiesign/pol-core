# งาน Implementation: SDD Operating-Layer Parity

> Status: approved 2026-08-25

เอกสารนี้แบ่งงานเป็น 10 vertical slices ที่ review และ commit แยกกันได้ภายในหนึ่ง session ต่อ task โดยใช้ task ID แบบ numeric ที่คงที่สำหรับ automation ปัจจุบัน ขณะที่ engine ที่สร้างขึ้นต้องรักษา alphanumeric historical IDs แบบ case-sensitive ตาม design

> แต่ละ task ต้องทำครบทั้ง characterization/TDD, implementation, verification และ scope guard ในรอบเดียว ห้ามเพิ่ม `Evidence:` จนกว่าจะรันคำสั่งจริงและ mark task นั้นเสร็จ

## Implementation tasks

- [x] 1. สร้าง canonical artifact contract engine — parser, phase gate, task graph, EARS และ strict trace ใช้ Python stdlib owner เดียวและ fail closed ตาม diagnostic contract
     Scope: เริ่มจาก characterization และ failing fixtures สำหรับ status, workflow phase, numeric/alphanumeric task IDs, selector, dependency DAG, EARS forms, code fences และ named trace columns แล้วทำ CLI/import seam ขั้นต่ำที่ทุก consumer ใช้ร่วมกันได้
     Files:
       - `scripts/spec_contract.py`
       - `scripts/tests/test_spec_contract.py`
       - `scripts/spec_trace.py`
       - `scripts/spec-trace.sh`
     Out of scope: Evidence execution gate, shell command normalization, historical writes, harness adapters และ CI workflow
     Stop condition: หยุดหาก behavior ที่ requirements กำหนดขัดกับ approved design หรือมี parser path ใดต้องเดา approval, task ID หรือ trace mapping
     TDD: ให้ adversarial cases ของ status, task IDs, EARS และ trace เป็น red ก่อน implementation แล้วทำ mutation checks ให้ numeric-only parser, loose EARS และ post-Evidence metadata parsing เป็น red
     Satisfies: REQ-2.1-REQ-2.43
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_contract.py'` — คาดว่าจะ exit 0 และ fixtures ของ canonical/invalid artifact คืน verdict, exit code, location และ diagnostic code ตาม contract
       - `scripts/spec-trace.sh sdd-operating-layer-parity` — คาดว่าจะรายงานว่า criteria 178 ข้อถูกอ้างครบและ EARS lint ตรวจจริงโดยไม่เข้า skip path
     Evidence:
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> Ran 40 tests; OK
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/tests/test_spec_contract.py SpecContractTest.test_required_mutations_are_killed SpecContractTest.test_blocking_parser_mutations_are_killed SpecContractTest.test_rework_two_mutations_are_killed SpecContractTest.test_rework_three_mutations_are_killed SpecContractTest.test_rework_four_property_mutations_are_killed SpecContractTest.test_rework_five_fixed_point_and_task_seam_mutations_are_killed SpecContractTest.test_rework_six_limit_hit_mutation_is_killed` -> Ran 7 mutation suites; OK, mutation ถูก kill 25/25
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/tests/test_spec_contract.py SpecContractTest.test_rework_four_finite_unicode_criterion_sweeps SpecContractTest.test_rework_four_wrapper_bullet_task_heading_and_public_cli_sweeps SpecContractTest.test_rework_five_composition_fixed_point_property_and_public_cli SpecContractTest.test_rework_six_fixed_point_limit_fails_closed_at_boundary` -> Ran 4 tests; OK, finite sweep เดิม `Nd=750`, `Cf=170`, NFKC confusable `96`, wrapper/bullet `70`, task opening `13`, heading suffix `8` ไม่ regress; public strict CLI 8 fixtures และ depth `15/16/17/32` false-green `0`
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> exit `0`, criteria `178` ข้อและ EARS lint ผ่าน
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py gate phase --feature sdd-operating-layer-parity --phase implement --workflow requirements-first` -> exit `0`, public phase gate ผ่าน
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 -c 'from pathlib import Path; from scripts.spec_trace import run; specs=Path(".ai/specs"); features=sorted(p.name for p in specs.iterdir() if p.is_dir() and (p / "requirements.md").is_file()); failures=[f for f in features if run(f, specs)]; print(f"historical compatibility corpus: {len(features)-len(failures)}/{len(features)}"); raise SystemExit(bool(failures))'` -> historical compatibility corpus `52/52`
       - test: `scripts/spec-trace.sh merchant-commerce-erd-reset` -> criteria `264` ข้อถูกอ้างครบ และ EARS lint ผ่านทุกข้อ
       - viewports: n/a — งาน parser/CLI ไม่มี UI
       - deviations: HIGH #5 และ MEDIUM #6/#7 ไม่ถูกแก้ตามขอบเขต Rework 5
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/tests/test_spec_contract.py SpecContractTest.test_rework_seven_task_wrapper_normalization_is_shared_and_preserves_checkbox_marker SpecContractTest.test_rework_seven_finite_task_wrapper_sweep_and_mutations_are_killed` -> Ran 2 tests; OK; ครอบ direct helper/classifier/parser, public strict CLI feature/bugfix ที่ depth `1/16/32/64`, ordinary prose, wrapper families, metadata leakage, finite sweep `0..128` และ mutation ใหม่ `4/4`
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/tests -p 'test_spec_contract.py'` -> Ran 42 tests; OK; test count `40 -> 42`
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/tests/test_spec_contract.py SpecContractTest.test_required_mutations_are_killed SpecContractTest.test_blocking_parser_mutations_are_killed SpecContractTest.test_rework_two_mutations_are_killed SpecContractTest.test_rework_three_mutations_are_killed SpecContractTest.test_rework_four_property_mutations_are_killed SpecContractTest.test_rework_five_fixed_point_and_task_seam_mutations_are_killed SpecContractTest.test_rework_six_limit_hit_mutation_is_killed SpecContractTest.test_rework_seven_finite_task_wrapper_sweep_and_mutations_are_killed` -> Ran 8 tests; OK; mutation ถูก kill `29/29`
       - test:
         ```bash
         PYTHONDONTWRITEBYTECODE=1 python3 - <<'PY'
         from pathlib import Path
         from scripts.spec_trace import run
         specs = Path('.ai/specs')
         features = sorted(path.name for path in specs.iterdir() if path.is_dir() and (path / 'requirements.md').is_file())
         failures = [feature for feature in features if run(feature, specs)]
         print(f'historical compatibility corpus: {len(features) - len(failures)}/{len(features)}')
         raise SystemExit(bool(failures))
         PY
         ```
         -> historical compatibility corpus `52/52`
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> exit `0`, criteria `178` ข้อและ EARS lint ผ่าน
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 scripts/spec_contract.py gate phase --feature sdd-operating-layer-parity --phase implement --workflow requirements-first` -> exit `0`
       - test: `PYTHONDONTWRITEBYTECODE=1 scripts/spec-trace.sh merchant-commerce-erd-reset` -> exit `0`, criteria `264` ข้อ

- [x] 2. ส่งมอบ spec slice และ derived state end-to-end — slice, state wrappers และ SessionStart ใช้ engine เดียวพร้อม full-read fallback ที่ deterministic
     Scope: เพิ่ม characterization สำหรับ ordered slice output, unknown task, `MISSING:`, five-state derivation, canonical archive location และ compact active summary แล้ว wire thin wrappers กับ SessionStart โดยไม่ parse Markdown ซ้ำ
     Files:
       - `scripts/spec_contract.py`
       - `scripts/tests/test_spec_contract.py`
       - `scripts/spec-slice.sh`
       - `scripts/spec-state.sh`
       - `scripts/session-start-active-specs.sh`
       - `.claude/hooks/tests/spec-slice.test.sh`
       - `.claude/settings.json`
     Out of scope: task build/test execution, cost parsing, GitHub sync, retrofit writes และ strict CI cutover
     Stop condition: หยุดหาก caller ไม่สามารถแยก known slice ที่มี `MISSING:` ออกจาก unknown feature/task หรือ state ต้องอาศัยชื่อ directory แทน canonical location
     TDD: ให้ golden outputs ของ feature/bugfix slice, archive-location pair, empty/ambiguous directory และ inactive-list suppression เป็น red ก่อน wiring
     Satisfies: REQ-2.44-REQ-2.59
     Depends on: 1
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_contract.py'` — คาดว่าจะ exit 0 และ slice/state cases รักษาลำดับ bytes, diagnostics และ five-state precedence
       - `bash .claude/hooks/tests/spec-slice.test.sh` — คาดว่าจะ exit 0 โดย known missing mapping แสดง `MISSING:` ส่วน unknown task คืน non-zero พร้อม available IDs ตาม file order
       - `python3 scripts/spec_contract.py state --all --format summary` — คาดว่าจะพิมพ์เฉพาะ active specs แบบ lexical order กับ blocked count โดยไม่ list complete, superseded หรือ archived specs
     Evidence:
       - test: `PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> Ran 59 tests; OK
       - test: `bash .claude/hooks/tests/spec-slice.test.sh` -> PASS: feature, bugfix, unknown, `MISSING:` และ blocked-feature raw evidence cases
       - test: `python3 scripts/spec_contract.py state --all --format summary` -> Active specs: sdd-operating-layer-parity. Blocked specs: 62.
       - test: `scripts/session-start-active-specs.sh` -> Active specs: sdd-operating-layer-parity. Blocked specs: 62.
       - test: `python3 scripts/spec_contract.py check --feature sdd-operating-layer-parity --strict` -> OK 178 criteria
       - test: `bash scripts/spec-trace.sh merchant-commerce-erd-reset` -> OK 264 criteria (compatibility corpus ไม่ regress)
       - test: `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 Warning(s), 0 Error(s)
       - test: `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 1929 passed, 0 failed
       - viewports: n/a — tooling-only slice/state และ SessionStart ไม่มี UI surface
       - deviations: Python `state --all` นับ historical 62 directories ที่ยังไม่ retrofit เป็น blocked ตาม staged migration contract; phase-skill full-read caller เป็น Task 8 scope

- [ ] 3. ปิด task completion gate แบบ fail-closed — raw snapshot selection, Evidence v2, .NET defaults, safe cache, pre-commit และ CI range selector ใช้ contract เดียว
     Scope: เขียน failing fixtures สำหรับ completed-task discovery จาก full before/after bytes, canonical changed ranges, sibling Evidence, command resolution, non-zero commands, zero tests และ cache semantics ก่อน wire enforcement floor และ adapters ที่ส่งเฉพาะ raw selection
     Files:
       - `scripts/spec_contract.py`
       - `scripts/tests/test_spec_contract.py`
       - `.ai/bin/check-evidence.sh`
       - `.ai/bin/gate-task.sh`
       - `.githooks/pre-commit`
       - `scripts/ci-evidence-scope.sh`
       - `.claude/hooks/task-gate.sh`
       - `.codex/hooks/task-gate.sh`
       - `.opencode/plugins/task-gate.js`
       - `.claude/hooks/tests/check-evidence.test.sh`
       - `.claude/hooks/tests/gate-task.test.sh`
       - `.claude/hooks/tests/ci-evidence-scope.test.sh`
     Out of scope: strict all-spec CI enablement, historical migration, product tests และ Docker/SQL execution
     Stop condition: หยุดเมื่อ snapshot correlation, changed range หรือ command resolution พิสูจน์ไม่ได้ ห้าม fallback เป็น whole-file scan, skip command หรือ cached Evidence verdict
     TDD: ให้ invalid range, after-only task, pre-existing completed task, missing Evidence fields, command failure และ no-cache behavior เป็น red ก่อน implementation
     Satisfies: REQ-3.1-REQ-3.23
     Depends on: 1
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_contract.py'` — คาดว่าจะ exit 0 และ GateSelection/Evidence fixtures ครบทั้ง positive, negative และ mutation cases
       - `for test_file in .claude/hooks/tests/check-evidence.test.sh .claude/hooks/tests/gate-task.test.sh .claude/hooks/tests/ci-evidence-scope.test.sh; do bash "$test_file"; done` — คาดว่าจะ exit 0 โดย defaults เป็น exact .NET commands, red command ถูก block และ cache hit ไม่ข้าม Evidence validation

- [ ] 4. รวม guard normalization และ bypass resistance — quote-aware command spans เป็น detection-only owner เดียวและทุก policy รักษา verdict corpus เดิม
     Scope: characterize existing destructive/bypass verdicts พร้อม benign quoted-data pairs แล้วสร้าง recursive shell span parser, thin bridge และ policy consumers ที่ normalize separators, substitutions, wrappers, env prefix, absolute binary path และ Git global options
     Files:
       - `scripts/guard_contract.py`
       - `scripts/tests/test_guard_contract.py`
       - `.ai/bin/lib-guard.sh`
       - `.ai/bin/check-destructive.sh`
       - `.ai/bin/check-bypass.sh`
       - `.claude/hooks/tests/destructive-guard.test.sh`
       - `.claude/hooks/tests/hook-bypass-guard.test.sh`
       - `.claude/hooks/tests/codex-adapters.test.sh`
     Out of scope: Markdown parsing, command execution, product shell scripts และ policy expansion นอก approved adversarial classes
     Stop condition: หยุดเมื่อ parser พบ quote/substitution ไม่ปิด, recursion/span limit เกิน หรือ adapter พยายาม pre-normalize เอง ห้าม fallback ไป flat regex
     TDD: รักษา existing corpus เป็น characterization floor และเพิ่ม mutations ที่แยก separator ใน quoted data หรือตัด recursive parse แล้วต้องเป็น red
     Satisfies: REQ-9.1-REQ-9.8
     Depends on: 1
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_guard_contract.py'` — คาดว่าจะ exit 0 และ normalized span tree ครบ quote, substitution, wrapper recursion กับ malformed input
       - `for test_file in .claude/hooks/tests/destructive-guard.test.sh .claude/hooks/tests/hook-bypass-guard.test.sh .claude/hooks/tests/codex-adapters.test.sh; do bash "$test_file"; done` — คาดว่าจะ exit 0 โดย destructive/bypass corpus เดิมไม่เปลี่ยนและ benign quoted data ยังคง allow

- [ ] 5. ย้าย shared consumers ไปใช้ string task graph และ current repository binding — pane-loop, cost, spec-state และ GitHub sync เลิก numeric-only/hardcoded repository paths
     Scope: เพิ่ม consumer fixtures ก่อนแก้ caller ให้รับ exact string IDs ตาม file order, reject invalid graph, derive `owner/repo` จาก `origin`, ตรวจ manifest ก่อน I/O และเปลี่ยน retrospective completion จาก Git HEAD เป็น artifact bytes
     Files:
       - `scripts/pane-loop.sh`
       - `scripts/cost_lib.py`
       - `scripts/spec-state.sh`
       - `.claude/skills/spec-sync-github/SKILL.md`
       - `.agents/skills/spec-sync-github/SKILL.md`
       - `.opencode/commands/spec-sync-github.md`
       - `.claude/skills/spec-retro/SKILL.md`
       - `scripts/tests/test_spec_contract.py`
     Out of scope: GitHub issue mutations ระหว่าง verification, automatic commit, CI workflow และ historical artifact writes
     Stop condition: หยุดก่อน GitHub I/O เมื่อ `origin` หาย, manifest mismatch, dependency unknown/cyclic หรือ retrospective artifact ระบุ session owner ไม่ได้
     TDD: ให้ fixtures ของ `A1`, `a1`, `migration-2`, unknown/cyclic dependency, HTTPS/SSH origin, manifest mismatch และ retro artifact hash transition เป็น red ก่อน caller cutover
     Satisfies: REQ-4.1-REQ-4.9
     Depends on: 1, 2
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_contract.py'` — คาดว่าจะ exit 0 และ consumer subprocess fixtures รักษา alphanumeric IDs, dependency verdict และ repository-binding exit contract
       - `python3 scripts/spec_contract.py task-ids --feature sdd-operating-layer-parity --pending --format lines` — คาดว่าจะพิมพ์ numeric IDs `1` ถึง `10` ตาม file order โดยยังใช้ string representation

- [ ] 6. สร้าง evidence-backed retrofit engine และจบที่ migration dry-run checkpoint — writer เดียววางแผน field-level action, blocker และ captured-byte recovery โดยไม่สร้างหลักฐานขึ้นเอง
     Scope: ทำ characterization จาก current/historical blobs แล้ว implement one-batch CLI, compatibility probe, deterministic reports, clean-tree/HEAD/hash gates, atomic replace, recovery journal และ field-specific no-fabrication checks จากนั้นรัน dry-run ทุก migration batch โดยยังไม่ apply
     Files:
       - `scripts/spec-retrofit.py`
       - `scripts/tests/test_spec_retrofit.py`
       - `scripts/spec_contract.py`
       - `scripts/tests/test_spec_contract.py`
     Out of scope: เขียน historical specs ใน task นี้, CI cutover, adapter/doc alignment และ product runtime
     Stop condition: หลัง dry-run ต้องหยุดที่ human-decision checkpoint เสมอ หากมี blocker หรือ historical proof ขัดกัน ห้าม task 7 หรือ task ถัดไปที่พึ่ง migration เริ่มจนกว่ามนุษย์จะบันทึก resolution ต่อ blocker ครบ
     TDD: ให้ dirty tree, conflicting/missing proof, overlapping spans, concurrent HEAD/file changes, mid-batch failure และ recovery race เป็น red ก่อน writer path
     Satisfies: REQ-5.1-REQ-5.20
     Depends on: 1, 2, 3
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_retrofit.py'` — คาดว่าจะ exit 0 และ fixtures พิสูจน์ deterministic planning, no-fabrication, atomic write กับ hash-guarded recovery
       - `for batch in canonical-complete approved-aliases bugfix alphanumeric-tasks evidence conflicting-status ambiguous-directories; do python3 scripts/spec-retrofit.py --dry-run --batch "$batch" --format json; done` — คาดว่าจะรายงาน sorted actions/blockers พร้อม proof ต่อ field โดยไม่เขียน target files; blocker exit ต้องถูกเก็บเพื่อ human decision ไม่ถูกตีความเป็นผ่าน
       - `git diff --exit-code -- .ai/specs` — คาดว่าจะไม่มี diff จาก dry-run

- [ ] 7. Apply migration batches ที่มนุษย์ resolve แล้วและพิสูจน์ strict 62-directory cutover gate — แต่ละ batch idempotent, recoverable และ reviewable โดยไม่ย้าย archive
     Scope: เริ่มได้เมื่อ checkpoint ของ task 6 ระบุ blocker เป็นศูนย์หรือมี human resolution ครบเท่านั้น จากนั้น apply-safe ทีละ registry batch, review diff ต่อ batch, รัน second dry-run และปิดด้วย `final-all-spec` strict check
     Files:
       - `.ai/specs/*/requirements.md` เฉพาะ action ที่มี field-level proof
       - `.ai/specs/*/design.md` เฉพาะ action ที่มี field-level proof
       - `.ai/specs/*/tasks.md` เฉพาะ action ที่มี field-level proof
       - `.ai/specs/*/bugfix.md` เฉพาะ action ที่มี field-level proof
       - `.ai/specs/*/handoff.md` เฉพาะ action ที่มี field-level proof
       - `scripts/tests/test_spec_retrofit.py`
     Out of scope: `.ai/specs/sdd-operating-layer-parity/**`, archive relocation, guessed approval/Evidence/trace, adapter code และ CI workflow
     Stop condition: หยุดทั้ง batch ก่อน write เมื่อ proof ยังไม่ครบ และหยุดก่อน task 8/9 ที่พึ่ง migrated corpus หาก second dry-run มี safe action, recovery journal ค้าง หรือ strict check ไม่ครบ 62 directories
     TDD: รัน rollback/recovery fixtures ก่อน apply และหลังแต่ละ batchพิสูจน์ no-op; หาก batch ถูก rollback ต้อง rerun dry-run ของ batch เดิมก่อนเดินต่อ
     Satisfies: REQ-5.21-REQ-5.22, REQ-8.2-REQ-8.7
     Depends on: 6
     Verify:
       - `for batch in canonical-complete approved-aliases bugfix alphanumeric-tasks evidence conflicting-status ambiguous-directories; do python3 scripts/spec-retrofit.py --dry-run --batch "$batch" --format json; done` — คาดว่าทุก applied batch รายงาน safe actions เป็นศูนย์และไม่มี unresolved blocker
       - `python3 scripts/spec-retrofit.py --check --batch final-all-spec` — คาดว่าจะ exit 0 และระบุ historical spec directories ครบ 62 แห่ง
       - `python3 -m unittest discover -s scripts/tests -p 'test_spec_retrofit.py'` — คาดว่าจะ exit 0 รวม batch-only rollback, dry-run-after-rollback และ no-dual-schema fixtures

- [ ] 8. Align adapters, canonical docs และ source assertions — Claude, Codex และ OpenCode ให้ verdict เดียวกัน ส่วน Pi อธิบาย floor-only/unsupported ตรง runtime จริง
     Scope: เขียน source-to-assertion และ cross-harness characterization fixtures ก่อน align payload capture, phase skills, routers, agent docs, canonical module/DbContext/isolation/CI/handoff/git-boundary assertions และ verification-record schema
     Files:
       - `scripts/repo_policy_alignment.py`
       - `scripts/tests/test_repo_policy_alignment.py`
       - `.claude/hooks/tests/cross-harness-conformance.test.sh`
       - `.claude/hooks/tests/repo-policy-alignment.test.sh`
       - `.claude/hooks/*.sh`
       - `.claude/skills/spec-*/SKILL.md`
       - `.codex/config.toml`
       - `.codex/hooks/*.sh`
       - `.codex/agents/*.toml`
       - `.opencode/plugins/*.js`
       - `.opencode/commands/spec-*.md`
       - `opencode.json`
       - `.agents/skills/spec-*/SKILL.md`
       - `.ai/agents/claude/AGENT.md`
       - `.ai/agents/codex/AGENT.md`
       - `.ai/agents/opencode/AGENT.md`
       - `.ai/agents/pi/AGENT.md`
       - `AGENTS.md`
       - `CLAUDE.md`
       - `.ai/README.md`
       - `.ai/shared/TASK_PROTOCOL.md`
       - `.ai/shared/EARS.md`
       - `.ai/shared/TESTING_PROTOCOL.md`
       - `.ai/shared/SECURITY_RULES.md`
       - `.ai/shared/ARCHITECTURE.md`
       - `.ai/shared/PROJECT_CONTEXT.md`
       - `.ai/shared/REVIEW_PROTOCOL.md`
       - `.ai/shared/stack/dotnet.md`
       - `.ai/templates/handoff-note-template.md`
     Out of scope: `.pi/extensions/**`, dependency ใหม่, product source, remote authorization claims และ CI workflow edits
     Stop condition: หยุดเมื่อ adapter ใดส่ง selected task ID/Markdown verdict เอง, suppress engine-fail, อ้าง Pi capability เกิน runtime หรือ canonical assertion ไม่ตรง source extractor
     TDD: ให้ normalized fixture เดียวกันวิ่งผ่านทุก harness และให้ negative source/assertion mutation ต่อ modules, DbContexts, isolation, CI jobs, handoff และ git boundaries เป็น red ก่อน alignment
     Satisfies: REQ-6.1-REQ-6.9, REQ-1.12-REQ-1.18
     Depends on: 2, 3, 4, 5
     Verify:
       - `python3 scripts/repo_policy_alignment.py --check` — คาดว่าจะ exit 0 โดย source-to-assertion rows ตรง filesystem/config จริงและ negative fixtures ใช้ stable diagnostics
       - `for test_file in .claude/hooks/tests/cross-harness-conformance.test.sh .claude/hooks/tests/repo-policy-alignment.test.sh; do bash "$test_file"; done` — คาดว่าจะ exit 0 และ Claude/Codex/OpenCode คืน allow, policy-fail หรือ engine-fail ตรงกันทุก normalized fixture
       - `test ! -d .pi/extensions` — คาดว่าจะ exit 0 และไม่มี Pi extension ถูกเพิ่ม

- [ ] 9. Cut over strict checks เฉพาะ GitHub/GitLab verify paths — protected workflow comparator กัน product/package/deploy jobs ทุก byte และ rollback CI เป็น layer แรก
     Scope: เขียน comparator/negative fixtures ก่อนแก้ workflow จากนั้นเพิ่ม Python, shell, diff-aware Evidence, strict all-spec, REQ/F/B trace และ parity/alignment checks เฉพาะ verify jobs หลัง task 7 strict check กับ task 8 conformance ผ่าน
     Files:
       - `scripts/ci-workflow-preservation.py`
       - `scripts/tests/test_ci_workflow_preservation.py`
       - `.github/workflows/ci.yml`
       - `.gitlab-ci.yml`
     Out of scope: GitHub jobs `dotnet`, `docker-build`, `dotnet-integration`; GitLab jobs `dotnet`, `integration`, `package`, `.deploy-template`, `deploy-uat`, `deploy-prod`; deploy/release execution และ product paths
     Stop condition: ห้ามแก้ workflow ก่อน `python3 scripts/spec-retrofit.py --check --batch final-all-spec` และ cross-harness conformance exit 0; หาก CI cutover ทำ pipeline fail ให้ rollback CI layer ก่อนและห้ามแก้ protected jobs ให้เขียว
     TDD: ให้ missing base, duplicate/missing protected key, YAML merge key, removed shell inventory token และ one-byte protected-job mutation เป็น red ก่อน workflow edit
     Satisfies: REQ-1.7-REQ-1.11, REQ-7.1-REQ-7.10, REQ-8.1
     Depends on: 7, 8
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_ci_workflow_preservation.py'` — คาดว่าจะ exit 0 และ comparator แยก policy-fail กับ engine-fail ตาม diagnostic contract
       - `BASE_SHA="$(git merge-base HEAD origin/develop)"; python3 scripts/ci-workflow-preservation.py --base "$BASE_SHA"` — คาดว่าจะ exit 0 โดย protected GitHub/GitLab job blocks byte-identical และ existing shell inventory เป็น subset ของ verify inventory ใหม่
       - `python3 scripts/spec_contract.py check --all --strict` — คาดว่าจะ exit 0 หลัง migration และ workflow cutover โดยตรวจทั้ง feature REQ กับ bugfix F/B paths

- [ ] 10. ปิด final verification, rollback record และ no-product-diff proof — เก็บ observed outputs จริงครบทุก local/remote scope โดยไม่ยกระดับ unverified เป็น pass
     Scope: รัน full Python/shell/retrofit/.NET/end-to-end checks, ตรวจ protected paths/runtime manifests, บันทึก verification records ตาม closed scope labels และ rehearse rollback units โดยไม่มี implementation เพิ่มนอก defect ที่ gate ตรวจพบ
     Files:
       - `.pipeline/sdd-operating-layer-parity/changes.md`
       - `.pipeline/sdd-operating-layer-parity/state.md`
       - `.ai/specs/sdd-operating-layer-parity/tasks.md` เฉพาะ checkbox กับ Evidence ที่มาจากคำสั่งซึ่งรันจริง
     Out of scope: product behavior, source/runtime edits, secret-file reads, deploy/release, fabricated remote result และการสรุป temporary evidence เป็น remote verified
     Stop condition: หยุดส่งมอบเมื่อ command ใด red, protected path มี diff, criteria/test mapping มีช่องว่าง, remote record ขาด paired temporary evidence หรือ environment limitation ไม่มี reason/scope/limitation ครบ
     TDD: ใช้ final gate เป็น assembly characterization และให้ mutations ที่ลบ engine call, protected inventory, source assertion หรือ unverified metadata ทำ suite เป็น red ก่อนยอมรับผลจริง
     Satisfies: REQ-1.1-REQ-1.6, REQ-7.11-REQ-7.17, REQ-8.8-REQ-8.13
     Depends on: 9
     Verify:
       - `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` — คาดว่าจะ exit 0 พร้อม observed test counts ที่บันทึกภายหลังรันจริง
       - `for test_file in .claude/hooks/tests/*.test.sh docker/entrypoint.test.sh docker/migrate-entrypoint.test.sh scripts/check-release-evidence.test.sh; do bash "$test_file"; done` — คาดว่าจะ exit 0 โดย inventory เดิมและ fixtures ใหม่ผ่านจาก output จริง
       - `python3 scripts/repo_policy_alignment.py --check` — คาดว่าจะ exit 0 และ verification-record scope/limitations ตรง contract
       - `BASE_SHA="$(git merge-base HEAD origin/develop)"; python3 scripts/ci-workflow-preservation.py --base "$BASE_SHA"` — คาดว่าจะ exit 0 และ protected workflow blocks คง bytes เดิม
       - `python3 scripts/spec-retrofit.py --check --batch final-all-spec` — คาดว่าจะ exit 0 พร้อม historical 62-directory strict result
       - `python3 scripts/spec_contract.py check --all --strict` — คาดว่าจะ exit 0 โดยไม่มี orphan REQ/F/B หรือ invalid artifact
       - `dotnet restore pol-core.slnx` — คาดว่าจะ exit 0 จาก toolchain จริง
       - `dotnet build pol-core.slnx --no-restore -warnaserror` — คาดว่าจะ exit 0 โดยไม่มี warning/error
       - `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` — คาดว่าจะ exit 0 ตาม process status จริงและบันทึก observed counts
       - `BASE_SHA="$(git merge-base HEAD origin/develop)"; git diff --exit-code "$BASE_SHA"...HEAD -- src tests docker pol-core.slnx Directory.Packages.props` — คาดว่าจะ exit 0 และไม่มี product/runtime diff

## Human-decision checkpoint

หลัง task 6 ให้หยุดและแสดง migration dry-run report ต่อมนุษย์ทุกครั้ง ถ้ามี blocker ต้องระบุ path, field, task ID, current/historical proof และ diagnostic code แล้วรอ explicit resolution; task 7–10 ห้ามเริ่มจากการอนุมานหรือ safe-subset apply

## Batch execution recommendation

งานนี้ coupled แต่แต่ละ task เป็น failure domain ใหญ่และ task 6 มี human checkpoint จึงแนะนำหนึ่ง task ต่อหนึ่ง session ไม่ใช้ `Batch:` tag และไม่ใช้ `all-in-one`

1. ก่อน checkpoint รันตามลำดับ `1 2 3 4 5 6`
2. หลังมนุษย์ resolve migration report แล้วรัน `7 8 9 10`
3. คำสั่ง orchestration ที่ตรง dependency คือ `scripts/pane-loop.sh sdd-operating-layer-parity 1 2 3 4 5 6` แล้วหยุด review; หลัง checkpoint ใช้ `scripts/pane-loop.sh sdd-operating-layer-parity 7 8 9 10`
4. Task 4 เริ่มหลัง task 1 ได้โดยไม่พึ่ง task 2/3 แต่ไม่แนะนำให้รันพร้อมกันบน working tree เดียว; task 9 ต้องรอทั้ง task 7 และ 8

## Traceability summary

| Task | Criteria count | Requirement ranges |
|---:|---:|---|
| 1 | 43 | `REQ-2.1-REQ-2.43` |
| 2 | 16 | `REQ-2.44-REQ-2.59` |
| 3 | 23 | `REQ-3.1-REQ-3.23` |
| 4 | 8 | `REQ-9.1-REQ-9.8` |
| 5 | 9 | `REQ-4.1-REQ-4.9` |
| 6 | 20 | `REQ-5.1-REQ-5.20` |
| 7 | 8 | `REQ-5.21-REQ-5.22`, `REQ-8.2-REQ-8.7` |
| 8 | 16 | `REQ-6.1-REQ-6.9`, `REQ-1.12-REQ-1.18` |
| 9 | 16 | `REQ-1.7-REQ-1.11`, `REQ-7.1-REQ-7.10`, `REQ-8.1` |
| 10 | 19 | `REQ-1.1-REQ-1.6`, `REQ-7.11-REQ-7.17`, `REQ-8.8-REQ-8.13` |
| รวม | 178 | criteria ทุกข้อปรากฏหนึ่งครั้ง |
