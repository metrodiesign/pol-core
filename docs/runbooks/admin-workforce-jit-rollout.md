# Tier 0 Microsoft Workforce Tenant-aware Identity Cutover (retired)

คู่มือนี้ถูก retire 2026-09-14 พร้อมตาราง `admin.Users`, `admin.WorkforceTenantBindings`,
`admin.WorkforceIdentityMigrations`, `admin.WorkforceIdentitySubjectRollback`, `admin.WorkforceTenantIdentityMigrations`,
`admin.WorkforceTenantIdentitySnapshot` และ tool `WorkforceIdentityMigrator` (migration `RetireLegacyAdminIdentityPlane`).

Employee identity ปัจจุบันคือ `acct.Accounts` + `acct.Employees` ที่ JIT จาก Entra (exact `tid`/`oid`) ตอน login
ผ่าน `/oauth/authorize`; ไม่มี manifest หรือ offline mapping อีก ดู
[admin-microsoft-oidc.md](admin-microsoft-oidc.md) และ [deploy-self-host.md](deploy-self-host.md).
