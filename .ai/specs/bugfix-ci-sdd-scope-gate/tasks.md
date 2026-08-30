# งานแก้ scope ของ CI protected-job และ protected-path guard

> Status: approved 2026-08-30

เอกสารนี้แบ่ง bugfix เป็น 2 งาน ทุกงานต้องมี regression proof ก่อนประกาศผ่าน และห้ามขยาย policy ของ comparator นอกเกณฑ์ใน `bugfix.md`

## Implementation tasks

- [x] 1. เพิ่ม scope classifier ให้ comparator และ skip real-merge-base test เมื่อ layer untouched
  - **Scope**: `--sdd-scope` พิมพ์ `touched`/`untouched` จาก diff `base..working tree` ตาม path set ของ SDD operating layer; base resolve ไม่ได้เป็น engine-fail `2`; `test_real_merge_base_comparator_green` skip เมื่อ `untouched`
  - **Files**: `scripts/ci-workflow-preservation.py`, `scripts/tests/test_ci_workflow_preservation.py`
  Satisfies: F-1, F-3, F-4, F-5, B-1, B-3, B-4
  - **Verify**: product-only range เป็น `untouched`; range ที่แตะ SDD layer เป็น `touched` และ one-byte protected mutation ยัง policy-fail `1`; zero SHA base เป็น exit `2`
  Evidence:
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_ci_workflow_preservation.py'` -> `Ran 18 tests`; `OK` (เพิ่ม `SddScopeTest` 4 เคส: product-only untouched, SDD-layer touched, touched ยัง reject protected mutation, unresolvable base exit 2)
    - test: `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> `Ran 333 tests`; `OK`
    - test: `python3 scripts/ci-workflow-preservation.py --base "$(git merge-base develop HEAD)" --sdd-scope` บน branch นี้ -> `touched` (`scripts/ci-workflow-preservation.py`, `scripts/tests/test_ci_workflow_preservation.py`)
    - test: classifier ต่อ diff ของ PR #208 (`5d1c008..feat/migrate-via-sql-script`, 7 ไฟล์ใน `docker/`, `Dockerfile`, `.github/workflows/ci.yml`, `docs/`, `scripts/check-migration-script.sh`) -> `untouched`
    - viewports: n/a — tooling-only
    - deviations: ไม่มี

- [x] 2. ผูก scope gate เข้า verify path ของ GitHub และ GitLab และบันทึกกติกาใน design
  - **Scope**: verify step เรียก `--sdd-scope` ก่อน แล้วรัน comparator กับ `git diff --quiet` protected-path guard เฉพาะ `touched`; strict checks ที่เหลือรันทุก range; เพิ่มหัวข้อ Scope ใน `design.md` ของ `sdd-operating-layer-parity`
  - **Files**: `.github/workflows/ci.yml`, `.gitlab-ci.yml`, `.ai/specs/sdd-operating-layer-parity/design.md`
  Satisfies: F-2, B-2
  - **Verify**: verify step บน branch นี้ (touched) รัน comparator ผ่านและ product path ไม่เปลี่ยน; `test_verify_jobs_use_shared_strict_cutover_owners` ยังเห็น shared owner ทุก token ในทั้งสอง workflow
  Evidence:
    - test: `B=$(git merge-base develop HEAD); python3 scripts/ci-workflow-preservation.py --base "$B"` -> `"verdict": "allow"`; exit `0`
    - test: `git diff --quiet "$B"..HEAD -- src tests docker pol-core.slnx Directory.Packages.props` -> exit `0` (product path ไม่เปลี่ยน)
    - test: `bash scripts/ci-evidence-scope.sh "$B" HEAD` -> exit `0`
    - test: `python3 scripts/spec_contract.py check --feature bugfix-ci-sdd-scope-gate --strict` -> exit `0`
    - test: `bash .claude/hooks/tests/repo-policy-alignment.test.sh` และ `bash .claude/hooks/tests/cross-harness-conformance.test.sh` -> exit `0` ทั้งคู่
    - test: `git diff --check` -> exit `0`; `.ai/bin/check-secrets.sh --all` -> exit `0`
    - viewports: n/a — tooling-only
    - deviations: ไม่มี
