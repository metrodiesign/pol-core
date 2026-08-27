# Bugfix: SDD operating-layer review

> Status: approved 2026-08-27

เอกสารนี้กำหนดการแก้ findings จาก final review ของ `sdd-operating-layer-parity` โดยคงพฤติกรรมปกติที่ไม่เกี่ยวข้องไว้ทั้งหมด

## Current Behavior (Defect)

| Repro condition | Defective behavior |
|---|---|
| ตรวจ protected-path classifier ต่อ source owner ของ guard | classifier คืน `protected=False` แม้ wrapper เรียก source owner โดยตรง |
| ตรวจ canonical `spec-*` skills | ไม่มี invocation ของ phase gate หรือ implementation slice |
| รัน `check --all --strict` และ `final-all-spec` | directory ที่ถูกข้ามถูกนับเป็น `strictOk=true` |
| รัน Evidence scope บน initial push | zero SHA ทำให้ range resolver ล้มก่อนตรวจ Evidence |
| จำลอง engine ของ pane loop คืน nonzero | script รายงานว่าไม่มี task และคืน success |
| จำลอง `mktemp` คืน nonzero ใน shell fixture | fixture เดินต่อและรายงานผ่าน |
| รัน `git diff --check` ต่อ `scripts/spec-retrofit.py` | พบ trailing whitespace ที่บรรทัด 606 |
| แก้ `tasks.md` ผ่าน configured PreToolUse hook | snapshot hook ไม่ execute เพราะไม่มี executable mode แต่ task gate ยังคงบังคับ snapshot |

## Expected Behavior

- F-1 THE SYSTEM SHALL protect every source owner and wrapper that enforces guard policy, and SHALL preserve the complete caller argument vector.
- F-2 WHEN a canonical spec phase creates or advances a downstream artifact, THE SYSTEM SHALL run the required phase gate before writing or advancing that artifact.
- F-3 WHEN implementation begins, THE SYSTEM SHALL obtain the feature slice before work and SHALL perform a full read when the slice reports `MISSING:`.
- F-4 WHEN an all-spec strict check runs, THE SYSTEM SHALL validate every directory in the canonical inventory and SHALL NOT report an unchecked directory as `strictOk=true`.
- F-5 THE SYSTEM SHALL define the canonical historical inventory as 61 directories and SHALL count the current feature separately.
- F-6 WHEN Evidence scope runs on an initial push, THE SYSTEM SHALL resolve the base to the Git empty tree and SHALL classify invalid Evidence as a policy failure.
- F-7 WHEN the pane-loop engine or retrospective command returns nonzero, THE SYSTEM SHALL stop, return nonzero, and SHALL NOT report that no task exists or clear a pane.
- F-8 WHEN a shell fixture cannot create its temporary workspace, THE SYSTEM SHALL stop before executing its test body.
- F-9 THE SYSTEM SHALL contain no trailing whitespace in `scripts/spec-retrofit.py`.
- F-10 WHEN a configured task snapshot hook runs before a `tasks.md` edit, THE SYSTEM SHALL execute the hook and create its declared pre-edit snapshot.

## Unchanged Behavior

- B-1 WHEN a guard receives benign quoted data, a read-only query, or a copy-from operation, THE SYSTEM SHALL CONTINUE TO allow it.
- B-2 WHEN a phase upstream artifact is unapproved, malformed, or unknown, THE SYSTEM SHALL CONTINUE TO block progression without inferring approval.
- B-3 WHEN Evidence scope runs on a normal push or pull-request range, THE SYSTEM SHALL CONTINUE TO validate the exact base and HEAD snapshots.
- B-4 WHEN the pane-loop engine succeeds with zero pending tasks, THE SYSTEM SHALL CONTINUE TO report no pending task and return success.
- B-5 WHEN a shell fixture deliberately asserts a negative command result, THE SYSTEM SHALL CONTINUE TO aggregate that assertion rather than terminate before evaluating it.
- B-6 WHEN a historical directory remains outside a completed strict validation, THE SYSTEM SHALL CONTINUE TO report it as residual or unverified and SHALL NOT claim strict success.
- B-7 WHEN a pre-tool edit does not target `tasks.md`, THE SYSTEM SHALL CONTINUE TO avoid creating a task snapshot.

## Scope Decision

- Canonical historical inventory: 61 directories.
- Current feature directory: counted separately from the historical inventory.
- No source path is excluded from this bugfix scope.
