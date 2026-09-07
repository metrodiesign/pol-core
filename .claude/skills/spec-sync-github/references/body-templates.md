# Issue body templates (spec-sync-github)

ใช้ render โดย skill `/spec-sync-github` และเติม placeholder `<...>` จาก spec artifact
ต้องคง HTML comment แรกไว้เป็น recovery marker ใช้ `[done]` / `[open]` โดยไม่มี emoji

## Epic issue

- Title: `[spec] <ชื่อฟีเจอร์ภาษาไทย>`
- Labels: `spec:<feature>`, `spec-epic`, `req-spine`

```markdown
<!-- spec-sync: feature=<feature> epic -->
**Spec:** `.ai/specs/<feature>/`
**Status:** <บรรทัด `> Status:` จาก requirements.md> · <M> tasks

ติดตาม spec `<feature>` โดยแต่ละ sub-issue เป็น slice ที่ cohesive และ verify แยกได้
ความคืบหน้าแสดงผ่าน checklist ของ sub-issue ที่ GitHub render ด้านบน

### Requirement coverage (REQ spine)

| Task | Satisfies REQ | Done = |
| ---- | ------------- | ------ |
| #<task-issue> <หัวข้อ> | <Satisfies IDs> | <นิยามว่าเสร็จแบบสั้น> |
| ... | ... | ... |

_สร้างโดย `/spec-sync-github` ให้รันคำสั่งใหม่เมื่อต้องการแก้ ห้ามแก้ด้วยมือ_
```

สำหรับ bugfix spec ที่ไม่มี heading `## REQ-N:` ให้ตัด table "Requirement coverage"

## Sub-issue (one per task)

- Title: `[<feature>] <N>. <หัวข้อ>`
- Labels: `spec:<feature>`, `spec-task`
- State: `[x]` -> closed, `[ ]` -> open

```markdown
<!-- spec-sync: feature=<feature> task=<N> -->
**Epic:** #<epic-issue> · **Task <N>** ของ spec `<feature>`

**Scope / done =** <ข้อความส่วน "— <scope + นิยามว่าเสร็จ>" จากบรรทัด task ตามต้นฉบับ>

**Satisfies:** <REQ/B ID จากบรรทัด Satisfies:>
**Depends on:** <เลข task ใน Depends-on ที่ map เป็น #NN ผ่าน manifest หรือ "none">
**Verify:** <บรรทัด Verify:>

**Status:** [done] | [open]

<details><summary>Evidence</summary>

<Evidence block ตามต้นฉบับ หรือ "n/a — ยังไม่ได้ implement">
</details>

_spec คือ source of truth ส่วน issue นี้เป็นข้อมูลสะท้อน สร้างโดย `/spec-sync-github`_
```
