# Review record — PR #<n> @ <head sha7>

> ใช้ [นโยบายภาษาของผลลัพธ์](../shared/TASK_PROTOCOL.md#ภาษาของผลลัพธ์)
> sdd-premerge-review-standard — หนึ่งไฟล์ต่อ PR head ที่ review แล้ว ใช้ head sha เป็น key
> เพราะ PR number อย่างเดียวระบุสิ่งที่ ship ไม่แน่นอนเมื่อ squash-merge ตาม LESSONS.md
> `squash-merge-title-unreliable` ค่า `<sha7>` ต้องตรงกับ prefix ของ `headRefOid` ปัจจุบัน
> record ของ head เก่าไม่ผ่าน gate ต้องสร้างใหม่หรือ rename และอัปเดตหลัง commit แก้ review

- date: <YYYY-MM-DD>
- kind: review-fanout | override
- finders/verifiers: <จำนวน เช่น "5 finders / 3 verifiers">             (override: "—")
- override reason: <เหตุผลหนึ่งบรรทัด บังคับเมื่อ kind: override>         (review: "—")

| # | finding (file:line) | verdict | outcome |
|---|---|---|---|
| 1 | <path:line> | CONFIRMED \| PLAUSIBLE \| REFUTED | fixed \| accepted \| rejected |

(ถ้าไม่มี confirmed finding ให้แทน table ด้วยบรรทัดเดียว: "no confirmed findings")
