# Task Brief: <ชื่องาน>

> ใช้ [นโยบายภาษาของผลลัพธ์](../shared/TASK_PROTOCOL.md#ภาษาของผลลัพธ์)
> กรอกก่อนเริ่มลงมือ เพื่อให้ agent ตัวใดก็รับงานต่อได้ ลบบรรทัด `<...>` ที่กรอกแล้ว

## Goal

<หนึ่งประโยค: งานนี้ต้องทำอะไรให้สำเร็จ>

## Context

- Spec / Feature: <ชื่อฟีเจอร์ หรือ .ai/specs/<feature>/>
- Active task ID: <เช่น task 4 ใน tasks.md>
- REQ IDs in scope: <REQ-1.1, REQ-13.2, ...>

## Scope

- In scope: <ไฟล์หรือพฤติกรรมที่ต้องแตะ>
- Out of scope: <สิ่งที่อยู่นอกงานนี้>

## Inputs / Required reading

- <.ai/shared/<X>.md, design.md, ไฟล์ที่ต้องอ่านก่อน>

## Definition of Done

- [ ] <criterion ที่ตรวจได้ เช่น unit test เขียว>
- [ ] <คำสั่ง typecheck> -> pass (คำสั่งของโปรเจกต์จาก `SDD_TYPECHECK_CMD` หรือ script `typecheck` ใน `package.json`)
- [ ] <คำสั่ง test> -> pass (test runner ของโปรเจกต์จาก `SDD_TEST_CMD` หรือ script `test` ใน `package.json` สำหรับ Node project)
- [ ] tasks.md checkbox + Evidence block ตรงกับงานจริง

## Open questions

- <คำถามที่ต้องเคลียร์ก่อน หรือ "ไม่มี">
