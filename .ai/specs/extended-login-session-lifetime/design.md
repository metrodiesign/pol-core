# Design: Extended Login Session Lifetime

> Status: unknown

เปลี่ยนเฉพาะค่า config และ fallback default ที่ flow เดิมอ่านอยู่แล้ว ไม่มี schema, API หรือ cookie contract ใหม่

## การออกแบบ

| ฝั่ง | Config section | Idle | Absolute |
|---|---|---:|---:|
| Admin | `AdminSession` | `IdleMinutes = 1440` | `AbsoluteHours = 168` |
| Merchant User | `MerchantUser:Session` | `IdleMinutes = 1440` | `AbsoluteHours = 168` |

`LoginService` และ `UserLoginService` สร้าง `SessionPolicy` จากค่าเหล่านี้ก่อนเรียก `Session.Start`.
Authentication handler ใช้ค่าเดียวกันตอน slide idle และยัง clamp ด้วย `AbsoluteExpiresAt` ที่บันทึกใน session

## ไฟล์ที่แก้

| File | Change |
|---|---|
| `src/Hosts/Api/Admins/AuthOptions.cs` | เปลี่ยน fallback default ฝั่ง Admin |
| `src/Hosts/Api/Merchants/UserOidcOptions.cs` | เปลี่ยน fallback default ฝั่ง Merchant User |
| `src/Hosts/Api/appsettings.json` | เปลี่ยน committed defaults ของทั้งสอง section |
| `tests/Hosts.Tests/AdminLoginServiceTests.cs` | ยืนยัน expiry ของ session ใหม่ฝั่ง Admin |
| `tests/Hosts.Tests/MerchantUserLoginServiceTests.cs` | ยืนยัน expiry ของ session ใหม่ฝั่ง Merchant User |
| `tests/Hosts.Tests/AdminSessionCookieTests.cs` | ยืนยัน cookie ยังไม่ persistent |
| `tests/Hosts.Tests/MerchantUserSessionCookieTests.cs` | ยืนยัน cookie ยังไม่ persistent |

## พฤติกรรมที่คงเดิม

- `RotationMinutes = 15`
- `GraceSeconds = 60`
- revoke, reuse detection, CSRF และ SameSite posture
- cookie ไม่มี `Expires` และ `Max-Age`; ปิด browser แล้ว login สิ้นสุด
- ไม่มี migration; session เดิมคง absolute expiry ที่มีอยู่

## การทดสอบ

- Login service test ตรวจ `IdleExpiresAt = issuedAt + 24 ชั่วโมง`
- Login service test ตรวจ `AbsoluteExpiresAt = issuedAt + 7 วัน`
- Cookie test ตรวจ header ไม่มี `Expires` และ `Max-Age`
- Full build และ test suite ตรวจ regression ของ rotation, expiry, revoke และ auth flow เดิม

## Requirement Traceability

| Requirement | Design element | Verification |
|---|---|---|
| REQ-1.1, REQ-1.2 | `AdminSessionOptions` + `AdminSession` config | `AdminLoginServiceTests` |
| REQ-1.3, REQ-1.4 | `UserSessionOptions` + `MerchantUser:Session` config | `MerchantUserLoginServiceTests` |
| REQ-1.5, REQ-1.6 | Authentication handlers ใช้ policy เดิมและ clamp stored absolute expiry | full auth/domain test suite |
| REQ-2.1, REQ-2.2 | ไม่แก้ rotation/grace config | full auth/domain test suite |
| REQ-2.3 | ไม่แก้ cookie lifetime attributes | Admin/Merchant cookie tests |
| REQ-2.4 | ไม่มี data migration หรือ bulk update | diff review + full test suite |

## ความเสี่ยงและ rollback

อายุ credential ยาวขึ้นเพิ่มช่วงเวลาที่ cookie ถูกขโมยแล้วอาจใช้งานได้. ความเสี่ยงลดด้วย rotation,
reuse detection, per-request account status check และ logout/revoke ที่คงเดิม. Rollback โดยคืนค่าเป็น
`IdleMinutes = 30` และ `AbsoluteHours = 8`; session ที่สร้างช่วงค่าใหม่ยังคง stored absolute expiry เดิม

