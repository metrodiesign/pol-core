## Summary

<!-- อธิบายสั้นๆ ว่า PR นี้ทำอะไรและทำไม -->

## Tracking

<!-- ผูก PR เข้ากับ epic issue ของ spec:
     "Closes #<epic>" = PR นี้ทำ feature ครบ -> merge แล้วปิด epic อัตโนมัติ
     "Refs #<epic>"   = ทำไม่ครบ (ยังเหลือ task) -> ไม่ปิด epic -->

Closes #

Tasks advanced: <!-- เลข task ใน tasks.md ที่ PR นี้ดัน เช่น 4, 5, 6 -->

## Test evidence

<!-- คำสั่งที่รันจริง + ผล -->

- test: `<test command>` -> <!-- project test runner: SDD_TEST_CMD env หรือ package.json test script -->
- typecheck: `<typecheck command>` -> <!-- project typecheck command: SDD_TYPECHECK_CMD env หรือ package.json typecheck script -->

## Checklist

- [ ] tasks.md checkbox + Evidence block ตรงกับงานจริง
- [ ] ไม่มี secret / `.only` / `.skip` ค้าง
- [ ] รัน `/spec-sync-github <feature>` แล้ว issue บน GitHub สะท้อนสถานะล่าสุด
