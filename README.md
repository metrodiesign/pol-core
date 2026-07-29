# pol-core

**Internal Payment Orchestration Platform (captive)** — SaaS อีคอมเมิร์ซประกันภัย multi-tenant
ที่ให้บริษัทในเครือ (vPrivilege / vCommerce / vSouvenir) รับชำระเงินผ่าน PSP ที่ถือใบอนุญาตอยู่แล้ว
(2C2P + Omise/Opn) แบบ **redirect-only** โดย **เงินจริงไม่วิ่งผ่านแพลตฟอร์ม** — เรา "ใช้" PSP ไม่ใช่ "เป็น" PSP

โมเดล: **captive · ไม่ถือเงิน · ใช้ฟรีภายในเครือ · PCI SAQ A**

## สถานะ

อยู่ระหว่าง implement บน branch `develop` (spec-driven — specs come before code, ALWAYS).
โมดูลที่ลงแล้ว: Products / Cart / Checkout / Orders / Payments (E2E), Tenant provisioning,
Admin (Google SSO + RBAC), Producer (Google SSO + registration). งานใหม่ผ่าน workflow gates เสมอ.

## Stack

Modular Monolith (Clean Architecture + CQRS) · .NET 10 / ASP.NET Core 10 / C# 14 (LTS) ·
EF Core 10 + SQL Server 2025 Standard · **martinothamar/Mediator** 3.x (in-process, source-generated)

5 โมดูลคุยกันผ่าน Mediator: Products → Cart → Checkout → Orders → Payments (จบที่ emit `PaymentPaid`).
version pin เต็มดู `.ai/shared/CODING_STANDARDS.md`

## โครงสร้าง

| path | คือ |
|---|---|
| `.ai/shared/` | single source of truth — product canon, standards, architecture, protocols (ทุก agent อ่านที่นี่) |
| `.ai/specs/<feature>/` | spec artifact ต่อ feature: requirements → design → tasks |
| `.claude/` · `.codex/` · `.opencode/` | per-agent adapter (commands, hooks, agents) |
| `.githooks/` · `.github/` | enforcement floor: pre-commit/pre-push + CI |
| `docs/reference/` | สเปกอ้างอิงเต็มของ Payments module |

## การรัน (Local Dev)

คู่มือฉบับเต็ม (Google SSO setup, troubleshooting, DB cheatsheet): `docs/runbooks/local-dev-run.md`.
ด้านล่างคือทางลัดให้รันได้เร็ว.

### Prerequisites

| tool | version |
|---|---|
| .NET SDK | 10.x (`dotnet --version`) |
| Docker + Compose | ล่าสุด (รัน SQL Server 2025) |
| `dotnet-ef` | `dotnet tool install --global dotnet-ef` |

### First-time setup (ครั้งเดียว)

```bash
# 1) env — copy template, ใส่ค่า LOCAL (ห้าม secret จริง). .env เป็น gitignored.
cp .env.example .env
#    แก้: MSSQL_SA_PASSWORD + POL_SA_PASSWORD (ค่าเดียวกัน), POL_APP_PASSWORD (ห้ามมีชื่อ login อยู่ในรหัส
#    — มีรหัส DB แค่ 2 ตัว, pol_admin/pol_worker ถูกยุบเข้า pol_app แล้ว), ConnectionStrings__App,
#    ConnectionStrings__Migrator (sa), POL_DESIGN_SQL (sa), Vault__MasterKeyBase64
#    (head -c 32 /dev/urandom | base64), Psp__PublicBaseUrl

# 2) git hooks (enforcement floor)
git config core.hooksPath .githooks

# 3) ยก DB + สร้าง principal (idempotent): สร้าง DB VCentralPay + login/user pol_app ตัวเดียว
#    (rls-to-query-filter task 8 ยุบ pol_admin/pol_worker/pol_resolver/pol_vault_auditor + pol_rls_bypass ทิ้ง)
docker compose up -d
docker compose ps pol-db      # รอจนขึ้น (healthy) ก่อนค่อยไปต่อ — ดูหมายเหตุด้านล่าง
```

> `docker compose up -d` คืน prompt ทันที แต่ SQL Server ยังบูตอยู่อีก ~30-60 วิ (นานกว่านั้นถ้าเพิ่ง
> `down -v` เพราะต้อง init volume ใหม่). ยิง `dotnet ef` ก่อน `pol-db` เป็น `healthy` จะได้
> `Connection refused` (error 10061) — ไม่ใช่ config พัง แค่เร็วไป.

> ค่าใน `.env` ที่มี `;` (connection string ทุกตัว) **ต้องอยู่ใน single quote** ตามที่ `.env.example` ทำไว้ —
> Docker Compose ไม่แคร์ แต่ `source .env` ในเชลล์จะตัดค่าทิ้งที่ `;` ตัวแรกอย่างเงียบ ๆ แล้วโผล่มาเป็น
> pre-login-handshake error ตอนรัน `dotnet ef` ทีหลัง.

migrations: API auto-migrate ตอน boot ใน Dev (ถ้าตั้ง `ConnectionStrings:Migrator`). หรือรันเอง:

```bash
set -a && source .env && set +a
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api
```

### Topology

| host | port | principal | ใช้ทำอะไร |
|---|---|---|---|
| SQL Server (dev + integration) | `11433` | — | DB หลัก `VCentralPay` — container เดียวเสิร์ฟทั้ง dev และ Integration suite (`.env.integration`) ตั้งแต่ rf1 cutover 2026-07-12 |
| API (`src/Hosts/Api`) | `5100` / `5101` (https) | `pol_app` (default) + `pol_admin` (keyed, control-plane) | REST + BFF auth + outbox dispatcher (Worker merge เข้ามาแล้ว, `multi-tier-deployment`) |
| FE `pol-admin` (repo แยก) | `5200` | — | Next.js, proxy `/admin/*` + `/merchants/*` -> `:5100` |

connection strings (map `ConnectionStrings__<Name>` -> `ConnectionStrings:<Name>`):
`App`=pol_app (RLS) · `Admin`=pol_admin (control-plane) · `Worker`=pol_worker · `Migrator`=sa (DDL, Dev auto-migrate).

> ชื่อคีย์คือ **`App`** — rf1 rename มาจาก `Producer` แล้ว (`Program.cs` เรียก `GetConnectionString("App")`).
> คีย์เก่าที่ค้างใน `.env` / `appsettings.Development.json` ของเครื่องใครจะ **ไม่ถูกอ่านเลย** และ `App` จะตกไป
> หยิบค่าจาก `appsettings.json` ที่ commit ไว้ซึ่ง password ว่าง -> `pol_app` ต่อ DB ไม่ได้. ทั้งสองไฟล์
> gitignored จึงไม่มี CI จับให้ — เช็คด้วยตาเองตอน setup.

### รันประจำวัน

```bash
docker compose up -d                                      # DB (ถ้ายังไม่ขึ้น)
dotnet watch --project src/Hosts/Api/Api.csproj run       # API :5100 (hot reload) — outbox dispatcher รันในตัวเดียวกัน
```

> config change (`appsettings.*.json`) / DI ต้อง **restart เต็ม** (hot reload ไม่จับ).
> รัน API ค้างใน terminal ของคุณเอง — อย่ารันผ่าน agent background (ถูก kill).
> Google SSO (Admin + Producer OIDC, Google Console redirect URI) ดู runbook §5.

### เทส

```bash
dotnet test pol-core.slnx --filter "Category!=Integration"   # unit (ไม่ต้องใช้ DB)
source .env.integration                                      # gitignored — สร้างเอง (runbook §6)
dotnet test pol-core.slnx --filter "Category=Integration"    # integration (SQL :11433)
```

### สำหรับ contributor / agent

1. read order: `.ai/shared/PROJECT_CONTEXT.md` -> `ARCHITECTURE.md` -> `CODING_STANDARDS.md` -> `TASK_PROTOCOL.md`
   (AI agent: เริ่มที่ `AGENTS.md` หรือ `CLAUDE.md`)
2. งานใหม่ผ่าน spec workflow เสมอ — ไม่ code ก่อน requirements -> design -> tasks

## Demo seed data (dev only)

DB ที่ migrate เสร็จใหม่มีแค่ IAM catalog กับ master data (`cfg.*`) — ตารางอื่นว่างเปล่า ทำให้เปิด
console/เรียก API แล้วไม่เห็นอะไร. `docker/bootstrap/seed-demo.sql` เติม demo dataset ครอบคลุมทั้ง
funnel (merchant -> ผู้ใช้ทั้งสองฝั่ง -> สินค้า -> ตะกร้า -> checkout -> order -> payment session)
สำหรับ dev/localhost เท่านั้น — **ห้ามรันบน prod**.

```bash
set -a && source .env && set +a
./scripts/seed-demo.sh          # โหลด/โหลดซ้ำได้เรื่อย ๆ (idempotent, ไม่ TRUNCATE)
```

- **ไม่ใช่ EF migration** — `dotnet ef database update` ไม่แตะ demo data แม้แต่แถวเดียว; รันแยกด้วยมือ
  เท่านั้น เพราะ demo data ไม่ควรอยู่ใน schema-migration history ที่วิ่งบน prod ด้วย
- id ทุกแถวเป็น GUID คงที่ (prefix `e1…`–`ee…` ต่อตาราง) — รันซ้ำ = ลบแถว demo ของตัวเองแล้วใส่ใหม่
  เท่านั้น ไม่แตะแถวอื่น
- **login Google จริงไม่ได้** — `Subject`/`sub` ของบัญชี demo ทั้งหมดเป็นค่าปลอม (prefix `demo-adm-`/`demo-mch-`)
- password อ่านจาก `POL_SA_PASSWORD` หรือ `MSSQL_SA_PASSWORD` เท่านั้น ไม่มี secret ฝังในสคริปต์
- **guard เป้าหมาย** — สคริปต์ echo `server=… db=…` ก่อนเสมอ และ **ปฏิเสธถ้า `POL_SQL_SERVER` ไม่ใช่
  localhost** (เผลอ `source` prod env แล้วรัน = ปลูก demo data ลง prod). DB dev/test ที่ไม่ใช่ localhost
  จริง ๆ ต้องยืนยันด้วย `POL_ALLOW_DEMO_SEED=1 ./scripts/seed-demo.sh`
- **ไม่ต้องลง `sqlcmd` บน host** — ถ้าไม่มีบน PATH สคริปต์ fall back ไปใช้ตัวใน container `pol-db` ให้เอง
- รายละเอียด: `.ai/specs/demo-seed-data/{requirements,design}.md`

## กฎที่ขาดไม่ได้

- **Spec first** — ไม่ code ก่อนผ่าน gate (requirements → design → tasks)
- **redirect-only / ไม่แตะข้อมูลบัตร / ไม่ถือเงิน** — ดู Non-Goals ใน `PROJECT_CONTEXT.md`; เจอ requirement ที่ขัด → หยุดถามก่อน
- ห้าม push ตรง `main`/`develop` — ผ่าน PR + CI เสมอ
