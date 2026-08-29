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

## Successor clarification for Task 3

การตรวจ Task 3 หลัง rework ครบ `5/5` พบ blocker สองตัวที่ทำให้ implementation ยังไม่รักษา contract เดิมของ `F-4`, `F-5`, `B-6` และ parent `REQ-5.8`, `REQ-5.13` ภายใต้ process crash

| Blocker | Observable failure | Contract เดิมที่ต้องรักษา |
|---|---|---|
| Existing-file install ถูก `SIGKILL` หลังย้าย canonical entry ออก | canonical basename หาย และ public recovery คืน `MIGRATION_RECOVERY_FAILED` | atomic replace ต้องไม่เปิดช่วงที่ canonical path หาย และ concurrent bytes ต้องไม่ถูกทับ |
| Cleanup เลือก recovery root หรือ child ด้วย pathname หลังตรวจ owner | process same-UID ที่ไม่ร่วมมือสลับ entry แล้วทำให้ invocation แตะ foreign bytes ได้ | recovery state ต้องถูก retire แบบ append-only ผ่าน claimed fd โดยไม่มี automatic physical deletion |

- เพิ่ม Task `3A` เป็น successor หลัง Task 3 ไม่ใช่ rework รอบที่ 6 และไม่เปลี่ยนสถานะ Evidence เดิมของ Task 3
- Task `3A` คง existing-file writer บน platform atomic exchange ร่วมกับ durable per-write intent และเปลี่ยน cleanup ทุก recovery root เป็น in-place retirement ผ่าน `_retire_claimed_recovery_root(claimed_fd, owner_lock_fd, operation)`
- Retirement สร้าง marker `.retired-v1` แบบ zero-byte regular single-link ภายใน claimed fd เท่านั้น ห้าม automatic `unlink`, `rmdir`, `rmtree` หรือ rename recovery root และ children
- Resolver ต้อง scan และ classify ทุก entry ใต้ trusted journal parent ก่อน claim ใหม่ โดย ignore `.mutation.lock` และ ignore root ที่มี `.retired-v1` valid ในฐานะ terminal history
- Unretired root ที่มี `manifest.json` valid ต้องใช้ logical `batchId` จาก manifest เท่านั้น; ไม่มี preparing marker หรือชื่อ directory ใดเป็นแหล่ง batch identity
- Opaque root ที่ไม่มี manifest คือ global incomplete pre-manifest generation ที่ไม่มี batch identity: active owner คืน `MIGRATION_RECOVERY_REQUIRED`, stale valid owner retire in place ด้วย operation `incomplete-before-manifest` และ malformed state คืน `MIGRATION_RECOVERY_FAILED`
- Unretired generation ที่มี manifest หนึ่งตัวและ owner stale ต้องถูก recover แล้ว retire ก่อน claim ใหม่; owner active คืน `MIGRATION_RECOVERY_REQUIRED`
- หากมี unretired generation มากกว่าหนึ่งตัวที่มี logical `batchId` เดียวกัน ต้องคืน exact `MIGRATION_RECOVERY_FAILED`
- หลังจัดการ incomplete กับ recoverable state ครบและไม่มี matching active generation เท่านั้น จึงสร้าง `.journal-<32hex>`, owner lock และ `manifest.json` state `preparing` ตามลำดับ
- Unsupported primitive, unsupported filesystem, ambiguous recovery, malformed marker, symlink, non-regular entry หรือ owner lock ที่พิสูจน์ไม่ได้ต้อง fail closed ก่อน canonical mutation
- Read-only modes ต้องรายงาน recovery required โดยไม่ reconcile หรือ retire state และรักษา target กับ recovery tree แบบ byte-identical
- Retired roots คงอยู่บน disk โดยไม่มี auto-GC, TTL หรือ cap; no-op invocation ต้องไม่เพิ่ม generation หรือ retained bytes ส่วน manual purge เป็น destructive operator taskนอกขอบเขต
- `_retire_claimed_recovery_root(claimed_fd, owner_lock_fd, operation)` รับ `owner_lock_fd=None` ได้เฉพาะ create-error root ที่ยังไม่ publish และไม่มี entry `.owner.lock` อยู่เลย; หากพบ entry ต้องเปิด ตรวจรูปแบบ และ acquire nonblocking ก่อน retire เสมอ ห้ามตีความ `None` ว่าข้าม owner proof
- Owner ที่ active ต้อง block retirement ก่อน mutation และคง recovery tree byte-identical

## Total resolver contract for Task 3A

ก่อน mutation ใด ๆ resolver ต้อง scan และ classify recovery root ทุกตัวใต้ trusted journal parent ให้ครบทั้งทุก logical batch โดยเรียง deterministic ตาม canonical root name แล้วตัดสินจาก snapshot เดียวกัน

- malformed state แม้เพียง root เดียวต้องคืน exact `MIGRATION_RECOVERY_FAILED` โดยไม่ claim, restore, retire หรือ mutate state ใด
- active valid owner แม้เพียง root เดียวต้องคืน exact `MIGRATION_RECOVERY_REQUIRED` โดยไม่ claim, restore, retire หรือ mutate state ใด
- unretired valid manifest มากกว่าหนึ่ง root ที่มี logical `batchId` เดียวกันต้องคืน exact `MIGRATION_RECOVERY_FAILED` โดยไม่ claim หรือ mutate state ใด
- หลัง full classification ผ่านเท่านั้น จึง process stale valid root ทุกตัวตาม deterministic order ครบทุก batch: opaque pre-manifest root retire ด้วย `incomplete-before-manifest`; manifest root recover, restore ตาม hash guard และ retire ตาม terminal operation
- เมื่อ stale pass จบ resolver ต้อง scan และ classify ใหม่ทั้งหมดก่อน claim generation ใหม่ ผล rescan ต้องเหลือเพียง `.mutation.lock` กับ valid retired root เท่านั้น
- state ใหม่หรือ state ที่ยัง unretired หลัง rescan ต้อง block ด้วย verdict exact ตาม class (`MIGRATION_RECOVERY_REQUIRED` สำหรับ active valid owner, `MIGRATION_RECOVERY_FAILED` สำหรับ malformed หรือ duplicate) และห้าม claim root ใหม่

Regression fixtures ต้องพิสูจน์ total resolver contract ด้วย stale manifest สอง batch, opaque stale หลาย root, active กับ stale ที่ tree byte-identical, malformed กับ stale ที่ tree byte-identical และ root ใหม่ที่เกิดหลัง stale pass ก่อน rescan
