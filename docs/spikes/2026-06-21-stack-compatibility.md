# Spike: stack compatibility (task #1 gate)

> Throwaway compatibility probe ที่ Codex review กำหนดเป็น gate ก่อน scaffold (PLAN.md).
> วันที่: 2026-06-21 · ผล: **PASS** → freeze stack, เริ่ม task #2 ได้

## คำถามที่ต้องตอบ

stack ที่ pin (.NET 10 / EF Core 10 / SQL Server 2025 Standard / martinothamar/Mediator 3.x)
ใช้ได้จริง end-to-end ก่อนลงทุน scaffold ทั้ง Modular Monolith ไหม

## วิธี

probe console (net10.0, C# 14) ที่: Mediator source-gen (`IQuery`+handler) → DI →
EF Core 10 ต่อ SQL Server 2025 (docker) → create schema (`producer.Tenant`) → insert+query
roundtrip → `SESSION_CONTEXT` (RLS floor ของ decision #3). throwaway ที่ `/tmp/pol-spike`,
SQL ผ่าน container `mcr.microsoft.com/mssql/server:2025-latest`.

## ผล — PASS

| ส่วน | เวอร์ชันจริง | ผล |
|---|---|---|
| .NET SDK | 10.0.300 | build/run ✓ |
| C# | 14 | compile ✓ |
| EF Core | 10.0.0 (SqlServer + Design) | connect + create + roundtrip ✓ |
| SQL Server | 2025 RTM-CU5 (17.0.4045.5, X64) | ✓ |
| martinothamar/Mediator | **3.0.1** (3.0.0 ไม่มีบน NuGet) | source-gen + DI ✓ |

output: `[mediator] pong:spike` · `[efcore] roundtrip TenantId=1` · `[session_context] same-conn TenantId=42` · `SPIKE_OK`

## Findings (ต้อง action)

1. **Mediator pin = 3.0.1** ไม่ใช่ 3.0.0 (3.0.0 ไม่ publish). อัปเดต CODING_STANDARDS.
2. **Transitive vuln (CI audit gate).** `Mediator.SourceGenerator` 3.0.1 ดึง **`Scriban` 6.2.0
   (critical/high advisories)** + `System.Security.Cryptography.Xml` 9.0.0 (high). ทั้งคู่เป็น
   **build/design-time** (SourceGenerator = `PrivateAssets=all`, ไม่ ship ลง runtime output) →
   ความเสี่ยง runtime ต่ำ แต่ **CI dependency audit จะ flag**. ต้อง: suppress รายตัวพร้อมเหตุผล
   (build-time only) ใน `Directory.Packages.props` / audit config, หรือรอ Mediator อัปเดต. อย่า
   force-downgrade core dependency.
3. **RLS `SESSION_CONTEXT` เป็น per-connection (decision #3 ยืนยัน).** set กับ read คนละ pooled
   connection → read ได้ NULL. real impl ต้อง **set `TenantId` ตอน connection-open ผ่าน
   `DbConnectionInterceptor`** ไม่ใช่ต่อ query + test pooled-reuse ไม่ retain tenant เดิม (acceptance ของ infra task).
4. **SQL Server 2025 image = amd64.** บน arm64 Mac รันผ่าน emulation (ช้าหน่อย); GitHub runner เป็น
   amd64 → native ใน CI (services container) ไม่ต้อง emulate.

## Fallback (ไม่ต้องใช้)

spike ผ่าน → ไม่ต้อง downgrade. (fallback ที่เตรียมไว้ถ้าล้ม: .NET 8 LTS / SQL Server 2022)

## Cleanup

`/tmp/pol-spike` + container `pol-spike-sql` = throwaway, ลบหลังจบ. ไม่มี code เข้า repo.
