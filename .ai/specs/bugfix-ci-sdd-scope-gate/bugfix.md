# Bugfix: CI protected-job และ protected-path guard รันกับทุก PR

> Status: approved 2026-08-30

เอกสารนี้กำหนดการแก้ scope ของ guard สองตัวใน step `SDD strict checks` ของ verify path (GitHub `.github/workflows/ci.yml` และ GitLab `.gitlab-ci.yml`) โดยคงพฤติกรรม strict ทั้งหมดของ `sdd-operating-layer-parity` ไว้

## Current Behavior (Defect)

| Repro condition | Defective behavior |
|---|---|
| เปิด PR ที่แก้ `docker/migrate-entrypoint.sh` และ job `dotnet-integration` (PR #208, merge-base `5d1c008`) | job `guards + spec-trace` ล้มด้วย `CI_PROTECTED_JOB_CHANGED: github:dotnet block bytes differ` และ `protected product/runtime path changed` |
| รัน `python3 scripts/ci-workflow-preservation.py --base "$(git merge-base develop HEAD)"` บน branch ที่แก้ product CI job | exit `1` แม้ range ไม่แตะ SDD operating layer เลย |
| รัน `git diff --quiet <merge-base>..HEAD -- src tests docker pol-core.slnx Directory.Packages.props` บน PR product ใด ๆ | exit `1` ทำให้ PR ที่แตะโค้ด product ทุกตัวไม่มีทางผ่าน required check |
| รัน `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` บน branch product | `test_real_merge_base_comparator_green` ล้ม เพราะเรียก comparator กับ merge-base จริงโดยไม่ดู scope |

Root cause: `REQ-1.1`-`1.11` และ `REQ-7.10` ของ `sdd-operating-layer-parity` เป็นเกณฑ์ `WHILE งานนี้ถูก implement` คือ precondition ของ cutover commit ตาม `design.md` หัวข้อ Cutover gate แต่ task 9 ใส่ guard ทั้งสองลง verify path แบบรันทุก range โดยไม่มีเงื่อนไข scope

## Expected Behavior

- F-1 WHEN the verify path resolves a comparison base, THE SYSTEM SHALL classify the range as `touched` when any changed path belongs to the SDD operating layer and as `untouched` otherwise.
- F-2 WHEN the range is `untouched`, THE SYSTEM SHALL skip the protected-job comparator and the protected product-path guard, print the reason, and continue with the remaining strict checks.
- F-3 WHEN the Python unit suite runs against a merge-base whose range is `untouched`, THE SYSTEM SHALL skip the real-merge-base comparator test instead of failing.
- F-4 WHEN the comparison base cannot be resolved for scope classification, THE SYSTEM SHALL fail closed with `CI_PROTECTED_JOB_PARSE_FAILED` and exit `2`.
- F-5 THE SYSTEM SHALL define the SDD operating layer as `.ai/bin/**`, `.claude/hooks/**`, `.githooks/**`, `scripts/tests/**`, `scripts/spec*`, `scripts/ci-*`, `scripts/guard_contract.py`, `scripts/guard_policy.py`, `scripts/repo_policy_alignment.py` and `scripts/pane-loop*`, and SHALL NOT count `.ai/specs/**`, `.ai/shared/**` or the workflow files themselves.

## Unchanged Behavior

- B-1 WHEN the range is `touched`, THE SYSTEM SHALL CONTINUE TO require every protected GitHub/GitLab job block to be byte-identical to the merge-base and SHALL CONTINUE TO reject any change under the protected product/runtime paths.
- B-2 WHEN the verify path runs, THE SYSTEM SHALL CONTINUE TO run Evidence scope, the Python unit suite, the strict all-spec check, the repo-policy alignment fixture and the cross-harness conformance fixture for every range.
- B-3 WHEN the protected workflow parser cannot resolve a base blob or a block boundary, THE SYSTEM SHALL CONTINUE TO fail closed with `CI_PROTECTED_JOB_PARSE_FAILED`.
- B-4 WHEN the GitHub verify shell inventory loses a token, THE SYSTEM SHALL CONTINUE TO fail with `CI_SHELL_INVENTORY_REMOVED` on a `touched` range.

## Scope Decision

- แก้เฉพาะ verify path ของ GitHub และ GitLab, `scripts/ci-workflow-preservation.py`, fixture ของมัน และ `design.md` ของ `sdd-operating-layer-parity`
- ไม่แตะ protected jobs, product paths หรือ policy ของ comparator เมื่อ range เป็น `touched`
- ไม่เปลี่ยน status หรือ Evidence ของ task 9 เดิม
