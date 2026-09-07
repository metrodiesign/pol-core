# Handoff Note: <ชื่องานหรือฟีเจอร์>

> ใช้ [นโยบายภาษาของผลลัพธ์](../shared/TASK_PROTOCOL.md#ภาษาของผลลัพธ์)
> Schema ตาม AGENT_HANDOFF_PROTOCOL.md (../shared/AGENT_HANDOFF_PROTOCOL.md)
> กรอกก่อนส่งงานต่อให้ agent อื่น หรือก่อน /clear / compaction ลบบรรทัด `<...>` ที่กรอกแล้ว

## Task Summary

<งานนี้คืออะไร spec/feature ใด task ID ใด เป้าหมายโดยรวม>

## Current Status

<ทำถึงไหนแล้ว: done / in-progress / blocked — สรุปสั้นๆ>

## Files Changed

- `<path>` — <สิ่งที่เปลี่ยน> <(new / edited / untracked)>

## Important Decisions

- <การตัดสินใจ + เหตุผลที่ต้องคงไว้ ไม่ให้ตกหล่นตอน handoff>

## Constraints

- <ข้อจำกัดที่ผู้รับต้องเคารพ เช่น ห้ามแตะ app/, ห้าม push main>

## Tests Run

- `<command>` -> <ผล>
- typecheck: `<คำสั่ง typecheck>` -> <ผล> (คำสั่งของโปรเจกต์จาก `SDD_TYPECHECK_CMD` หรือ script `typecheck` ใน `package.json`)
- test: `<คำสั่ง test>` -> <ผล> (test runner ของโปรเจกต์จาก `SDD_TEST_CMD` หรือ script `test` ใน `package.json` สำหรับ Node project)

## Known Issues

- <bug/gap/ข้อจำกัดที่ยังค้าง หรือ "none">

## Next Recommended Agent

<role/persona ที่ควรรับต่อ เช่น .ai/roles/bug-investigator.md หรือ "ให้มนุษย์ review">

## Next Steps

1. <step ถัดไปที่ผู้รับควรทำเป็นอันดับแรก>
2. <ขั้นตอนถัดมา>
