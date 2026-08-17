# Bugfix: Scalar OIDC return redirect

บันทึกรากเหตุและขอบเขตแก้ไข redirect หลัง Admin OIDC login สำหรับ Scalar ใน Development

> Status: approved 2026-08-08, amended 2026-08-17 (`local-api-port-5001`)

## Current Behavior (Defect)

เมื่อเปิด `GET /api/v1/admins/auth/google/login?returnTo=%2Fscalar` ใน Development แล้ว login สำเร็จ ระบบ redirect ไป `https://localhost:3001/dashboard` แทน `Scalar` ที่ API origin

รากเหตุมีสองส่วน:

- `/scalar` ไม่อยู่ใน `AdminSession:ReturnUrlAllowlist` จึงถูก `ReturnUrlPolicy` เปลี่ยนเป็น `/dashboard`
- `AdminSession:SpaBaseUrl` เติม `https://localhost:3001` ให้ path ที่ผ่านการ resolve แล้ว
- callback พึ่ง `AuthenticationProperties.RedirectUri` ของ framework เพียงค่าเดียว โดยไม่มี app-owned key ใน
  protected OIDC state; captured authorize/callback ใช้ state เดียวกันแต่ callback ยังเห็น fallback `/dashboard`

## Expected Behavior

- F-1 WHEN Development admin login receives allowlisted `returnTo=/scalar` THE SYSTEM SHALL redirect to `https://localhost:5001/scalar` after successful OIDC callback.
- F-2 WHEN `returnTo=/scalar` is used THE SYSTEM SHALL resolve it only through the configured Development allowlist and configured Scalar origin.

## Unchanged Behavior

- B-1 WHEN Development admin login receives `returnTo=/dashboard` THE SYSTEM SHALL CONTINUE TO redirect to `https://localhost:3001/dashboard`.
- B-2 IF `returnTo` is missing, absolute, protocol-relative, or not allowlisted THEN THE SYSTEM SHALL CONTINUE TO use `DefaultReturnPath` and the configured SPA origin.
- B-3 WHEN production configuration does not allow `/scalar` or configure a Scalar origin THE SYSTEM SHALL CONTINUE TO reject `/scalar` as a return target and SHALL NOT create an open redirect.
