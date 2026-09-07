# Output Formats

> โครงกลางที่ไม่ผูกกับ provider เพื่อให้ output จาก Claude, Codex, OpenCode และ Pi
> เปรียบเทียบกันและอ่านด้วยเครื่องได้

ใช้โครงต่อไปนี้ เมื่อมี template ใน [`../templates/`](../templates/) ให้เริ่มจาก
template แทนการสร้างโครงใหม่ เขียนให้กระชับและเน้นข้อมูลทางวิศวกรรม

ก่อนสร้างหรือแก้ผลลัพธ์ทุกชนิดในหน้านี้ ให้ใช้
[นโยบายภาษาของผลลัพธ์](TASK_PROTOCOL.md#ภาษาของผลลัพธ์): เติม placeholder ที่เป็นข้อความอธิบาย
ด้วยภาษาไทย คงหัวข้อบังคับ, schema keys, enum และรูปแบบที่เครื่องอ่านตาม template เดิม

## Implementation plan

ระบุสิ่งที่จะสร้างก่อนเริ่มลงมือ ดู
[`../templates/implementation-plan-template.md`](../templates/implementation-plan-template.md).

```
## Goal
<งานหนึ่งประโยค พร้อม REQ-ID ที่งานนี้ทำให้ผ่าน>

## Affected files
- <path> — <create | edit> — <เหตุผล>

## Steps
1. <ขั้นตอน> -> verify: <วิธีตรวจ>
2. <ขั้นตอน> -> verify: <วิธีตรวจ>

## Risks / open questions
- <ความเสี่ยงหรือคำถาม>
```

## Code review report

ดูมิติการ review ใน [REVIEW_PROTOCOL.md](REVIEW_PROTOCOL.md) และใช้
[`../templates/review-report-template.md`](../templates/review-report-template.md).

```
## Summary
<สิ่งที่เปลี่ยน และสิ่งที่ใช้เป็นเกณฑ์ review พร้อม REQ-ID>

## Critical
## High
## Medium
## Low
## Suggestions
<finding ใต้แต่ละหัวข้อ: file:line — มิติ — เหตุผล — วิธีแก้ที่ชัดเจน>

## Verdict
<approve | request changes — block ขณะที่ยังมี Critical/High>
```

## Bug analysis

เริ่มจาก root cause ส่วนโครง defect/expected/unchanged อยู่ใน bugfix spec

```
## Symptom
เมื่อ<เงื่อนไขที่ทำให้เกิดปัญหา> <พฤติกรรมที่ผิดพร้อมค่าที่วัดได้>

## Root cause
<สาเหตุจริงที่ trace ถึง file/line ไม่ใช่การคาดเดา>

## Fix outline
<การเปลี่ยนแปลงขั้นต่ำที่แก้ root cause>

## Regression surface
<พฤติกรรมเดิมที่เสี่ยง -> แปลงเป็น B-ID ใน bugfix spec>
```

## Refactor summary

```
## Intent
<สิ่งที่ปรับให้ดีขึ้น และเหตุผลที่ทำตอนนี้>

## Behavior change
none — refactor รักษาพฤติกรรมเดิม (หรือ: <พฤติกรรมที่ตั้งใจเปลี่ยน + REQ-ID>)

## Files touched
- <path> — <สิ่งที่ย้ายหรือเปลี่ยน>

## Verification
<test ที่เขียวทั้งก่อนและหลัง เพื่อยืนยันว่าพฤติกรรมเดิมยังอยู่>
```

## Test report

ดูรูปแบบ Evidence block ใน [TESTING_PROTOCOL.md](TESTING_PROTOCOL.md)

```
## Scope
<REQ-ID / F-ID / B-ID ที่ test ครอบคลุม>

## Result
- test: `<คำสั่งจริง>` -> <ผ่าน N / ไม่ผ่าน M>
- viewports: 375 OK | 768 OK | 1440 OK   (งาน browser; กรณีอื่นใช้ `n/a — logic-only`)
- coverage: <ค่าที่วัดได้เทียบ threshold ถ้ามี>

## Gaps
<สิ่งที่ยังไม่ครอบคลุม พร้อมเหตุผล>
```

## Handoff note

schema อยู่ใน [AGENT_HANDOFF_PROTOCOL.md](AGENT_HANDOFF_PROTOCOL.md) ให้กรอก
[`../templates/handoff-note-template.md`](../templates/handoff-note-template.md).

## ADR (Architecture Decision Record)

หนึ่งการตัดสินใจต่อหนึ่ง record และห้ามแก้หลัง accepted

```
# ADR-<n>: <ชื่อการตัดสินใจ>
> Status: proposed | accepted | superseded by ADR-<m>
> Date: <YYYY-MM-DD>

## Context
<แรงผลักและข้อจำกัด>

## Decision
<สิ่งที่จะทำ>

## Consequences
<trade-off ที่ยอมรับ และสิ่งที่ง่ายหรือยากขึ้น>

## Alternatives considered
<ทางเลือกที่ไม่เลือก พร้อมเหตุผล>
```

## Risk report

```
## Risk
<สิ่งที่อาจผิดพลาดอย่างชัดเจน>

## Severity / likelihood
<high | medium | low> / <high | medium | low>

## Impact
<สิ่งที่จะพังและผู้ได้รับผลกระทบ>

## Mitigation / follow-up
<การดำเนินการเพื่อลดหรือยอมรับความเสี่ยง พร้อม owner ถ้าทราบ>
```

## Changelog entry

ดู [`../templates/changelog-entry-template.md`](../templates/changelog-entry-template.md)
จัดรายการใต้ Added / Changed / Fixed / Removed และอ้าง version tag
