# Microsoft Workforce JIT Rollout Runbook

คู่มือนี้ใช้เตรียม staging, cutover production และ rollback สำหรับ Admin workforce JIT ของ `pol-core` กับ `pol-admin` โดยไม่เพิ่ม schema, table หรือ migration และไม่เรียก Microsoft Graph.

## ขอบเขตความปลอดภัย

- Admin รับเฉพาะ Microsoft Entra ที่ผ่าน tenant เดียว, role `vcp.employee` และ exact domain `viriyah.co.th`.
- ตรวจ domain แบบ case-insensitive แต่ role `vcp.employee` แบบ case-sensitive; subdomain, Hotmail และ `onmicrosoft.com` ไม่ผ่าน.
- identity key คือ `(provider=microsoft, oid)`; ห้าม bind ด้วย email.
- JIT account ใหม่เป็น `Active + Scoped`, ไม่มี role และไม่มี merchant assignment.
- Guest เข้าได้เมื่อ claims ทั้งหมดผ่าน; Guest ไม่ข้าม workforce policy.
- Admin Google login/callback ไม่ register และต้องคืน `404`; Merchant Google flow ไม่เปลี่ยน.
- Conditional Access และ MFA บังคับใน Entra; runtime ไม่อ่าน Graph และไม่เก็บ token หรือ raw external identity ใน audit.

## Staging preflight

### Entra application

1. สร้าง App Role value `vcp.employee` ใน Admin App Registration และให้ role ออกใน ID token claim `roles`.
2. เปิด `Assignment required` ใน Enterprise Application.
3. Assign security group พนักงานแบบ direct membership ให้ Enterprise Application.
4. ตั้ง Conditional Access/MFA ตาม policy องค์กร.
5. ตั้ง Admin Web redirect URI ให้ตรงทุกตัวอักษร:

   `https://<api-origin>/api/v1/admins/auth/microsoft/callback`

6. ตั้ง Authority เป็น `https://login.microsoftonline.com/<workforce-tenant-id>/v2.0` และใช้ client secret Value จาก secret store เท่านั้น.
7. ตรวจว่า Admin provider มี Microsoft เพียงตัวเดียว; ห้ามเปิด Google ใน production.

### Application checks

รันจาก `pol-core`:

```bash
scripts/spec-trace.sh admin-workforce-jit
dotnet test tests/Admins.Tests/Admins.Tests.csproj --no-restore
dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-restore \
  --filter "FullyQualifiedName~AdminLoginServiceTests|FullyQualifiedName~AdminCallbackResolverInviteBindTests|FullyQualifiedName~MicrosoftOidcTests|FullyQualifiedName~ProvisioningGuardsTests"
dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-restore --filter "Category!=Integration"
```

รันจาก `pol-admin`:

```bash
npm test
npm run typecheck
npm run lint
npm run build
```

ห้ามพิมพ์ค่า secret ตรวจเพียงว่าค่าถูก inject และ file มี mode ที่เหมาะสม. ห้ามใช้ `.env` หรือ tracked config เก็บ secret.

## Production cutover

### ลำดับ deploy

1. ผ่าน staging smoke และเก็บ release/image digest.
2. เปิด maintenance window; deploy `pol-admin` แล้ว `pol-core` ตามขั้นตอน release ขององค์กร.
3. ตรวจ health และ confirm ว่า production guard ไม่พบ Admin Google provider และพบ Microsoft tenant-pinned provider.
4. ทดสอบ login ด้วย corporate Super ที่มี `vcp.employee`.

### Bootstrap corporate Super

ใช้ Admin management API จาก session ของ Super ปัจจุบัน โดยรักษา role codes เดิม:

1. `GET /api/v1/admins?search=supachaip%40viriyah.co.th` แล้วตรวจ `status=active` และ `email` ตรง.
2. `GET /api/v1/admins/{id}` เก็บ `ETag`, `roleCodes`, `tier` และ `version`; ตรวจว่าเป็น identity เดิม ไม่ใช้ email bind.
3. `POST /api/v1/admins/{id}/tier` body `{ "tier": "super" }` พร้อม `If-Match`.
4. `PUT /api/v1/admins/{id}/roles` body เป็น role codes เดิมรวม `platform_admin` พร้อม `If-Match`; ห้ามแทนที่ด้วยชุดใหม่ที่ตัด role เดิม.
5. `GET /api/v1/admins/{id}/effective-permissions` ตรวจ permission ของ role เดิมและ `platform_admin`.
6. Login ใหม่ผ่าน Microsoft และตรวจ `GET /api/v1/admins/me` คืน `tier=super` และ effective permissions ที่คาดไว้.

ถ้าบัญชีถูก `suspended` ให้หยุด cutover และใช้ `POST /api/v1/admins/{id}/reactivate` ตาม approval ก่อน; ห้าม JIT ซ้ำหรือ bind ด้วย email.

### Session enumeration และ revoke

ทำหลัง bootstrap และก่อน revoke session ของ operator:

1. `GET /api/v1/admins` ระบุบัญชีเป้าหมายทีละราย.
2. `GET /api/v1/admins/{id}/sessions` ตรวจ `sessionId`, `familyId`, `isLive`; API ไม่คืน token.
3. `DELETE /api/v1/admins/{id}/sessions/{sessionId}` พร้อม `Idempotency-Key` ที่ไม่ซ้ำต่อ operation; endpoint revoke ทั้ง rotation family.
4. ตรวจ login ใหม่ของบัญชีเป้าหมาย และตรวจ session เก่าถูกปฏิเสธ.
5. ทำ operator session เป็นรายการสุดท้าย แล้ว login ใหม่ด้วย corporate Super.

ห้ามเรียก revoke production โดยไม่มี change approval และ operator owner. Logout ปัจจุบันใช้ `POST /api/v1/admins/auth/logout`; logout ทุกเครื่องของตัวเองใช้ `POST /api/v1/admins/auth/logout-all`.

## Browser acceptance matrix

ทดสอบบน production build ของ `pol-admin` และ backend staging เท่านั้น. ที่ 375, 768 และ 1440 ต้องยืนยัน `document.documentElement.clientWidth === target` และ `document.documentElement.scrollWidth <= window.innerWidth`.

| Scenario | Expected result | Evidence status |
|---|---|---|
| Eligible new corporate Microsoft identity | JIT ครั้งเดียว, `Active + Scoped`, no role/merchant, session created | รอ staging Entra + SQL |
| `/admin/me` หลัง JIT | `permissions=[]`, existing SPA 403 | รอ live backend |
| Assign active role แล้ว refresh | effective permissions ใหม่แสดงและ route ใช้งานได้ | รอ live backend |
| Existing identity | tier, roles, merchant assignments เดิมไม่เปลี่ยน | รอ staging data |
| Collision / suspended | typed denial, no partial user/session/audit | รอ integration |
| Hotmail / `onmicrosoft.com` / wrong tenant / missing role | `workforce-access-denied`, no user/session | รอ Entra negative test |
| Admin Google login | `404` | backend unit/route contract ผ่าน, live route รอ staging |
| Merchant Google login | behavior เดิม | frontend source/tests ผ่าน, live route รอ staging |
| Logout and session revoke | cookie/session family revoked | รอ live backend |

หลักฐาน frontend ที่มีแล้ว: production build มี employee card Microsoft ปุ่มเดียว, Merchant card มี Google/Microsoft, error copy provider-neutral และกลับหน้า login ได้. ตรวจ `clientWidth` จริง 375/768/1440 แล้ว โดย `scrollWidth` เท่ากับ viewport ทุกขนาด. หลักฐานนี้เป็น UI-only; ห้ามนับเป็น live JIT evidence.

## Rollback

1. หยุด rollout เมื่อ auth, authorization, health หรือ smoke gate ล้มเหลว.
2. คืน image เดิมของ `pol-admin` และ `pol-core` ตาม release record; ไม่รัน migration `Down` และไม่แก้ schema.
3. เก็บ JIT rows ไว้เพื่อ audit; rows ใหม่เป็น `Scoped`, ไม่มี role และไม่มี merchant assignment จึงไม่เพิ่มสิทธิ์เอง.
4. ตรวจว่า old image ไม่สร้าง session จาก wrong domain, Hotmail, `onmicrosoft.com` หรือ Google Admin.
5. หลัง restore ตรวจ health, `/api/v1/admins/me`, session revoke และ audit โดยใช้ synthetic account เท่านั้น.

Rollback evidence ต้องแนบ image digest, timestamp, health result และผล authorization negative controls. ห้ามลบ JIT account หรือ audit เพื่อ rollback.

## Current gate status

- Local unit, architecture, frontend test/typecheck/lint/build และ spec trace ผ่าน.
- Integration gate ผ่าน: Integration.Tests 168/168 และ Architecture integration 4/4.
- Live Entra, SQL race/rollback, browser JIT-to-403-to-role-refresh, session revoke และ rollback rehearsal ยังไม่มีหลักฐาน.
