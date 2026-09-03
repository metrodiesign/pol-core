# งานวิจัย Microsoft Entra claim semantics

> ตรวจสอบเมื่อ 2026-09-02 จาก Microsoft Learn ซึ่งเป็นเอกสารทางการ

## ข้อค้นพบ

1. `oid` เป็น GUID ที่ระบุ object ของ user แบบ immutable และไม่ถูก reuse ภายใน tenant เดียวกัน แอปต่างกันที่รับ user เดียวกันใน tenant เดียวกันจะได้ `oid` เดียวกัน แต่ user เดียวกันที่อยู่หลาย tenant ถือเป็นคนละ account และมี object ID ต่างกัน
2. `tid` เป็น GUID ของ tenant ที่ user กำลัง sign in และเป็น immutable tenant ID สำหรับ work/school organization
3. Microsoft แนะนำให้ใช้ immutable `tid` และ `oid` ร่วมกันเป็น key สำหรับ application data และการตัดสินว่า user ควรเข้าถึงข้อมูลหรือไม่
4. `email`, `preferred_username`, `unique_name` และ UPN เป็นค่า mutable หรือ reuse ได้ จึงห้ามใช้เป็น authorization identifier หรือ continuity key
5. `email` ไม่รับประกันว่าจะมีหรือถูกต้อง แม้ request `email` scope หรือ optional claim แล้ว จึงใช้ได้เพียง optional profile/contact hint ตามนโยบายของแอป ไม่ใช่หลักฐาน ownership หรือ identity
6. ID token ต้องผ่าน validation ก่อนใช้ claims โดย Microsoft แนะนำให้ใช้ token-validation library แทนการ validate เอง Validation ครอบ token signature และ claims ที่เกี่ยวข้องกับแอป เช่น issuer, audience และ lifetime
7. `aud` ของ ID token ต้องตรง Application ID ของแอป มิฉะนั้นต้อง reject token
8. `nonce` ใน ID token ต้องตรงค่าที่แอปส่งใน authorization request มิฉะนั้นต้อง reject token เพื่อป้องกัน token replay
9. สำหรับ tenant-bound data ต้องตรวจ `tid` ให้ตรง tenant ที่ application ใช้เก็บหรือเข้าถึงข้อมูล

## ผลต่อ spec นี้

- Admin Microsoft identity authority คือ `(Provider="microsoft", TenantId=validated tid, Subject=canonical validated oid)`
- `tid` และ `oid` ต้องถูกอ่านจาก `ClaimsPrincipal` หลัง ASP.NET Core OIDC middleware validate protocol/token แล้วเท่านั้น
- Application validation เพิ่มเงื่อนไข exact-one claim, non-empty GUID และ configured tenant match แบบ fail-closed
- Email จะไม่อยู่ใน exact lookup, candidate lookup, bind, conflict, recovery, JIT ownership หรือ authorization decision
- Same `oid` ภายใต้คนละ `tid` ต้องเป็นคนละ external identity แม้ email หรือ credential ต้นทางจะเหมือนกัน

## แหล่งอ้างอิงทางการ

- Microsoft Learn, “ID token claims reference”: https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference
  - `aud`, `email`, `preferred_username`, `nonce`, `oid`, `tid` และหัวข้อ “Use claims to reliably identify a user”
- Microsoft Learn, “OpenID Connect on the Microsoft identity platform”: https://learn.microsoft.com/en-us/entra/identity-platform/v2-protocols-oidc
  - หัวข้อ `nonce`, “Validate the ID token” และ “What to validate in an ID token”
- Microsoft Learn, “Secure applications and APIs by validating claims”: https://learn.microsoft.com/en-us/entra/identity-platform/claims-validation
  - หัวข้อ “Validate the audience”, “Validate the tenant” และ “Validate the subject”
