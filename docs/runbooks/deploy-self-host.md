# Runbook: deploy self-host (Docker / on-prem)

ยกระบบ pol-core (Backend API เดียว + Worker + SQL Server 2025) ด้วย `docker-compose.prod.yml`
บน host เดียว. API เดียวเสิร์ฟทั้ง 2 browser SPA (pol-tenant, pol-admin). secret ฉีดตอน deploy ผ่าน file
mount (ไม่ commit). ใช้สำหรับ staging/prod ขนาดเล็ก-กลาง.

ข้อกำหนด rule: prod deploy ต้องผ่าน staging ก่อน; ทุก release ต้องมี rollback plan + tag + changelog;
DB migration ต้องมี backup ก่อนรันบน prod; ห้าม deploy ศุกร์เย็น/ก่อนวันหยุดยาว (ยกเว้น hotfix).

## สิ่งที่ scaffold นี้ครอบ vs ไม่ครอบ

ครอบ: build image 2 host (API + Worker, non-root, /health/ready), SQL container, migrate one-shot (bootstrap
principals + EF migrations), file-secret injection (DB principal passwords + vault master key), healthcheck + restart.

ไม่ครอบ (ceiling — ต้องเสริมเอง): TLS termination / reverse proxy (nginx/caddy + cert) หน้า API;
HA / SQL replica / backup อัตโนมัติ; secret manager จริง (Vault/SOPS) แทน file ใน ./secrets/; log shipping.

## 0. Prerequisites

- Docker + Docker Compose v2 บน host
- clone repo บน host (compose build จาก source; migrate รัน EF จาก source ด้วย)
- host เปิด port ตาม `.env` (default API 5100; container ฟัง http 8080 ข้างใน) หรือวางหลัง reverse proxy

## 1. Config + secrets

```bash
cp .env.prod.example .env          # แก้ค่า non-secret + ตั้ง MSSQL_SA_PASSWORD (bootstrap-only)
mkdir -p secrets                   # ./secrets/ ถูก gitignore แล้ว
```

`.env` ต้องตั้ง (required — API ไม่ start ถ้าไม่มี): `TENANT_FRONTEND_ORIGIN` + `ADMIN_FRONTEND_ORIGIN` = origin
ของ 2 SPA (CORS allowlist, scheme+host+port ไม่มี trailing slash); `TENANT_GOOGLE_CLIENT_ID` = Google OAuth
client ของ tenant SPA (API รับเป็น `tenant` audience ของ id-token bearer); `ADMIN_OIDC_CLIENT_ID` = Google OAuth
client (type **Web application** = confidential) ของ admin console สำหรับ OIDC BFF login — client **secret** ของ
มันใส่เป็น secret file (`admin_oidc_client_secret`) ด้านล่าง ไม่ใช่ env. ตั้ง Authorized redirect URI ที่ Google
client นั้น = `https://<api-host>/admin/auth/callback`.

สร้าง secret file (ทุกไฟล์ = บรรทัดเดียว; entrypoint อ่านด้วย $(cat) ตัด trailing newline ให้อยู่แล้ว):

```bash
# DB principal passwords — ต้องผ่าน SQL complexity (CHECK_POLICY=ON): >=8 ตัว, upper+lower+digit
printf '%s' "Ci$(openssl rand -hex 10)Aa1" > secrets/pol_app_password
printf '%s' "Ci$(openssl rand -hex 10)Bb2" > secrets/pol_admin_password
printf '%s' "Ci$(openssl rand -hex 10)Cc3" > secrets/pol_worker_password

# Vault master key — 32-byte AES key, base64 (PR4 keyring อ่านจาก KeyFile; active id = v1)
head -c 32 /dev/urandom | base64 > secrets/vault_master_key

# Admin OIDC client secret — confidential client secret ของ admin console (คู่กับ ADMIN_OIDC_CLIENT_ID).
# ไม่ใช่ random: paste ค่าจริงจาก Google Cloud Console (OAuth 2.0 Client ของ admin = Web application -> Client secret).
printf '%s' 'GOCSPX-...paste-from-google-console...' > secrets/admin_oidc_client_secret

chmod 600 secrets/*
```

เก็บ secret เหล่านี้ใน secret manager จริง (backup แยก) — ถ้า `vault_master_key` หาย = ถอด secret ใน vault
ไม่ได้ทั้งหมด (ดู [[vault-key-rotation]] สำหรับการหมุน). อย่า commit ./secrets/.

## 1.1 หลัง reverse proxy + admin returnTo

วาง API หลัง TLS-terminating reverse proxy (nginx/caddy): proxy เชื่อมจาก IP ใน docker/private network (ไม่ใช่
loopback) → ต้อง trust proxy นั้น ไม่งั้น API เมิน `X-Forwarded-Host`/`-Proto` แล้ว OIDC `redirect_uri` กลายเป็น
internal host (`http://...:8080`) → Google ตอบ `redirect_uri_mismatch`. ตั้งใน `.env` (ค่าว่าง = loopback only,
พอสำหรับ proxy บน localhost host เดียวกัน):

```bash
# CIDR ของ network ที่ proxy เชื่อมมา (docker bridge subnet — ดู `docker network inspect <project>_default`)
ForwardedHeaders__KnownNetworks__0=172.18.0.0/16
# หรือ IP เดี่ยวของ proxy: ForwardedHeaders__KnownProxies__0=10.0.0.5
```

admin returnTo: หลัง login backend redirect ไปได้เฉพาะ path ใน `AdminSession:ReturnUrlAllowlist` (committed
default = `/` เท่านั้น). เพิ่ม route ปลายทาง login ที่ admin SPA ใช้จริง:

```bash
AdminSession__ReturnUrlAllowlist__0=/
AdminSession__ReturnUrlAllowlist__1=/dashboard
```

## 2. First deploy

```bash
docker compose -f docker-compose.prod.yml up -d --build
```

ลำดับ: `sql` (healthy) -> `migrate` (bootstrap principals + apply migrations แล้ว exit 0) -> 2 host start (API + Worker).
ดู migrate log:

```bash
docker compose -f docker-compose.prod.yml logs migrate     # ต้องจบด้วย "[migrate] done."
```

## 3. Verify

```bash
curl -fsS http://localhost:5100/health/ready    # API -> {"status":"healthy"}
docker compose -f docker-compose.prod.yml ps     # ทุก service healthy / migrate = exited (0)
```

healthy = keyring build ได้ (master key 32 byte) + DB ต่อได้. ถ้า not_ready: ดู log ของ host นั้น
(`docker compose ... logs api`) — มักเป็น vault key file ผิด หรือ DB password ไม่ตรง.

## 4. Upgrade deploy (มี migration ใหม่)

```bash
# 4.1 BACKUP ก่อน (rule: migration บน prod ต้อง backup ก่อน)
docker compose -f docker-compose.prod.yml exec sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE [VCentralPay] TO DISK='/var/opt/mssql/backup/pre-deploy.bak' WITH INIT, COMPRESSION"

# 4.2 ดึงโค้ดใหม่ + rebuild + rerun migrate + restart hosts
git fetch && git checkout <release-tag>
docker compose -f docker-compose.prod.yml up -d --build
```

migrate รันใหม่ทุกครั้ง (idempotent — bootstrap idempotent, EF apply เฉพาะ migration ที่ยังไม่ลง).

## 5. Rollback

App rollback (โค้ด):
```bash
git checkout <previous-tag>
docker compose -f docker-compose.prod.yml up -d --build
```

DB migration rollback (ถ้า migration ใหม่เข้ากันกับโค้ดเก่าไม่ได้): apply migration ก่อนหน้า แล้วค่อย rollback app.
รันใน migrate image (มี dotnet-ef + source):
```bash
docker compose -f docker-compose.prod.yml run --rm --entrypoint sh migrate -c '
  export POL_DESIGN_SQL="Server=sql;Database=VCentralPay;User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=True";
  dotnet ef database update <PreviousMigrationName> \
    --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api'
```
ถ้า migration rollback เสี่ยง (data loss) -> restore จาก backup (ข้อ 4.1) แทน. ออกแบบ migration ให้
backward-compatible (expand/contract) เพื่อให้ app เก่า+ใหม่ทำงานกับ schema เดียวกันได้ระหว่าง roll.

## 6. SA password rotation (post-bootstrap)

`sa` ใช้แค่ตอน bootstrap/migrate — app ต่อด้วย pol_app/pol_worker เท่านั้น (pol_admin = dormant, ใช้โดย integration test ต่อ DB ตรง). หลัง deploy แรก
หมุน SA ได้: `ALTER LOGIN sa WITH PASSWORD='...'` แล้วอัปเดต `MSSQL_SA_PASSWORD` ใน `.env` (ใช้รอบ migrate ถัดไป).
