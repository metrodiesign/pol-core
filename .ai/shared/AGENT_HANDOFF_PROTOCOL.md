# Agent Handoff Protocol

> contract กลางที่ไม่ผูกกับ provider สำหรับส่งงานระหว่าง agent (Claude -> Codex ->
> OpenCode -> Pi หรือข้าม session) agent ทุกตัวเขียนและ resume จาก handoff note ได้

ก่อนเขียน handoff note ให้ใช้
[นโยบายภาษาของผลลัพธ์](TASK_PROTOCOL.md#ภาษาของผลลัพธ์)

handoff note ย่อ conversation ที่เปลี่ยนได้ง่าย (Tier 4 ใน
[CONTEXT_MANAGEMENT.md](CONTEXT_MANAGEMENT.md)) เป็น record ถาวร เพื่อให้ agent ถัดไป
ได้ข้อมูลที่จำเป็นครบ เขียนก่อน `/clear`, ก่อน compaction หรือทุกครั้งที่ส่งงานต่อ
ถ้างานยังไม่เสร็จและ state อยู่เฉพาะใน conversation ต้องบันทึกก่อน handoff เสมอ

กรอก [`../templates/handoff-note-template.md`](../templates/handoff-note-template.md)
โดย schema ด้านล่างเป็นต้นทาง และ template ต้องตรงกับ schema นี้

## Schema

```
# Handoff: <ชื่อฟีเจอร์หรืองานแบบสั้น>
> From: <agent/session>   To: <agent ถัดไปหรือ "any">   Date: <YYYY-MM-DD>

## Task Summary
<สรุปงาน 1-3 ประโยค พร้อม active spec และ REQ-ID / F-ID / B-ID ใน scope>

## Current Status
<สิ่งที่เสร็จ กำลังทำ และยังไม่เริ่ม พร้อม active task ID และความคืบหน้าที่ชัดเจน>

## Files Changed
- <path> — <created | edited> — <สิ่งที่เปลี่ยน>
(รวมไฟล์ UNTRACKED เพราะ `git diff --stat` ไม่แสดง โดยใช้ข้อมูลจาก session และ `git status`)

## Important Decisions
<การตัดสินใจด้าน architecture/implementation พร้อมเหตุผล และอ้าง ADR ถ้ามี>

## Constraints
<ข้อจำกัดที่ agent ถัดไปต้องรักษา: do-not-modify files, scope boundary, พฤติกรรมที่ต้อง approved และ stack rules ที่เกี่ยวข้อง>

## Tests Run
- <คำสั่งจริง> -> <ผลที่สังเกตได้>
(คัดลอก Evidence block ระบุผล viewport สำหรับ browser และบอกสิ่งที่รันไม่ได้พร้อมเหตุผล)

## Known Issues
<bug, flaky check, รายการที่เลื่อน, ความเสี่ยง หรือ assumption พร้อม link ไป risk report ถ้ามี>

## Next Recommended Agent
<agent ที่ควรรับต่อพร้อมเหตุผล เช่น "spec-architect สำหรับ critique design", "builder ตัวใดก็ได้สำหรับ task ถัดไป" หรือ harness เฉพาะที่ต้องใช้>

## Next Steps
1. <การดำเนินการถัดไปที่ชัดเจน>
2. <การดำเนินการลำดับต่อมา>
(เริ่มด้วยคำสั่งจริงสำหรับโหลด context เช่น อ่าน spec files และรัน state script)
```

## Resuming from a handoff

agent ที่รับงาน:

1. อ่าน handoff note แล้วอ่าน spec files ที่อ้างถึง (Tier 3) และ project rules (Tier 2)
   โดยไฟล์ต้นทางเป็น source of truth
2. **เทียบกับ filesystem ก่อนเชื่อ status** เพราะ checkbox และ git log อาจคลาดเคลื่อน
   และ untracked files ไม่อยู่ใน `git diff --stat` ตรวจสิ่งที่มีจริงก่อนทำต่อ
3. รันคำสั่ง test/build ที่บันทึกไว้อีกครั้งเพื่อสร้าง known-good baseline
4. ทำต่อจาก "Next Steps" โดยรักษา "Constraints" ทุกข้อและทำตาม
   [TASK_PROTOCOL.md](TASK_PROTOCOL.md)
