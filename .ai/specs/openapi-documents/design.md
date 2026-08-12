# Design: OpenAPI Documents by Audience

> Status: approved 2026-08-12 (quick, no gates)

ใช้ multi-document support ที่มีใน `Microsoft.AspNetCore.OpenApi` และ Scalar อยู่แล้ว ไม่มี custom UI
หรือ package ใหม่

## Document model

| Document | Included surface | Scalar |
|---|---|---|
| `v1` | ทุก operation เพื่อ backward compatibility | ซ่อนจาก selector |
| `merchant` | `MerchantUserSession`, Merchant auth/register, public customer payment flow | แสดงและเป็น default |
| `admin` | `AdminSession`, Admin auth | แสดง |
| `integration` | public customer payment flow และ PSP webhook callback | แสดง |

Operation ที่รองรับสอง session อยู่ในสอง named documents. Security requirement ถูกลดให้เหลือ scheme ของ
document ปัจจุบัน จึงไม่โฆษณา credential คนละ audience.

## Implementation

- `OpenApiDocuments` เป็น single source ของชื่อ document, inclusion predicate, title, description,
  security-scheme visibility และ canonical tag-to-module map
- registration เดิมถูก reuse สำหรับทั้งสี่ documents ผ่าน `AddOpenApi(documentName, ...)`
- `ShouldInclude` แบ่ง operation จาก endpoint authorization metadata และ anonymous route ที่ระบุชัด
- audience request/response transformer เลือก DTO ของ named document และคง `oneOf` ใน `v1`
- document transformer สร้าง `x-tagGroups` จาก active tags หลัง filter จึงไม่มี missing/stale tags
- Scalar ใช้ `AddDocument` สามครั้งและยังอ่าน route pattern `/openapi/{documentName}.json`
- endpoint metadata ใช้ `WithSummary` และ `WithDescription` ที่จุด map route เพื่อให้ข้อความอยู่ติดกับ behavior จริง
- security-scheme description อ้าง provider-scoped login route ปัจจุบัน และไม่มีชื่อ task หรือ auth mechanism ที่ retired

## Failure behavior

- document name ที่ไม่ได้ register คืนพฤติกรรม framework เดิม
- operation ใหม่ที่ไม่มี session metadataและไม่ตรง anonymous allowlist ยังอยู่ใน `v1` แต่ named-document
  coverage test แดง ทำให้เจ้าของต้องเลือก audience ชัดก่อนส่งงาน

## Test strategy

Contract test boot host จริงใน Development แล้วตรวจ:

- named documents และ compatibility `v1` ตอบสำเร็จ
- union ของสาม named documents ครบ operation ใน `v1`
- expected shared/public/admin/merchant routes อยู่ถูก document
- named security schemes และ operation requirements ตรง audience
- active tags เท่ากับ grouped tagsแบบหนึ่งต่อหนึ่ง ไม่มี stale tag
- Scalar config มีสาม named documents และไม่ใช้ `v1` เป็นตัวเลือก
- ทุก operation ใน `v1` มี summary และ description ที่ไม่ว่าง
- security description ชี้ `/api/v1/admins/auth/{provider}/login` และ `/api/v1/merchants/auth/{provider}/login`
- public OpenAPI text ไม่มี internal task ID, retired Bearer flow หรือ retired auth route

## Requirement Traceability

| Requirement | Design element | Verification |
|---|---|---|
| REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5 | named `AddOpenApi`, Development guard, Scalar `AddDocument` | host contract test + existing production route convention |
| REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4, REQ-2.5, REQ-2.6, REQ-2.7, REQ-2.8, REQ-2.9 | `OpenApiDocuments.ShouldInclude` | document operation-set assertions |
| REQ-3.1, REQ-3.2, REQ-3.3, REQ-3.6 | document-local scheme และ DTO filtering | security component/operation + schema assertions |
| REQ-3.4, REQ-3.5 | active-tag filtered canonical groups | tag-group coverage assertions |
| REQ-4.1, REQ-4.2, REQ-4.3, REQ-4.4 | route-local summaries/descriptions + current session scheme descriptions | content completeness and stale-text contract assertions |

## Rollback

คืน registration เป็น `v1` ตัวเดียวและ Scalar default config. ไม่มี data migration หรือ external runtime contract change.
