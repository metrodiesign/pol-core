# Handoff Note: <task-or-feature>

> Schema ตาม AGENT_HANDOFF_PROTOCOL.md (../shared/AGENT_HANDOFF_PROTOCOL.md)
> กรอกก่อนส่งงานต่อให้ agent อื่น หรือก่อน /clear / compaction ลบบรรทัด `<...>` ที่กรอกแล้ว

## Task Summary

<งานนี้คืออะไร spec/feature ใด task ID ใด เป้าหมายโดยรวม>

## Current Status

<ทำถึงไหนแล้ว: done / in-progress / blocked — สรุปสั้นๆ>

## Files Changed

- `<path>` — <สิ่งที่เปลี่ยน> <(new / edited / untracked)>

## Important Decisions

- <decision + rationale ที่ต้องคงไว้ ไม่ให้ตกหล่นตอน handoff>

## Constraints

- <ข้อจำกัดที่ผู้รับต้องเคารพ เช่น ห้ามแตะ app/, ห้าม push main>

## Tests Run

- `<command>` -> <ผล>
- typecheck: `<typecheck command>` -> <result> (the project typecheck command via SDD_TYPECHECK_CMD env, or a package.json typecheck script)
- test: `<test command>` -> <result> (the project test runner via SDD_TEST_CMD env, or a package.json test script for a Node project)

## Known Issues

- <bug/gap/limitation ที่ยังค้าง หรือ "none">

## Next Recommended Agent

<role/persona ที่ควรรับต่อ เช่น .ai/roles/bug-investigator.md, หรือ "human review">

## Next Steps

1. <step ถัดไปที่ผู้รับควรทำเป็นอันดับแรก>
2. <step>
