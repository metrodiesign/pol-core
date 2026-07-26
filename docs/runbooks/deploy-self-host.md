# Runbook: deploy self-host (Docker / on-prem)

ยกระบบ pol-core แบบ **2 tier**: **App tier** (`docker-compose.prod.yml`, host เดียว, รัน API host เดียว —
Worker's outbox dispatcher/session-pruner เดิม merge เข้า Api ไปแล้ว ไม่มี container Worker แยกอีกต่อไป) +
**DB tier** (bare-VM SQL Server 2025 Standard, **ไม่ใช่ Docker**, host แยกต่างหาก จัดเตรียม/ดูแลโดย
infra/DBA — ดู prerequisites ข้อ 0). App tier ต่อ DB tier ข้าม network จริงผ่าน TCP 1433 + TLS certificate
validation จริง (ไม่มี trust-any-certificate อีกแล้ว). API เดียวเสิร์ฟทั้ง 2 browser SPA (pol-tenant,
pol-admin). secret ฉีดตอน deploy ผ่าน file mount (ไม่ commit). ใช้สำหรับ staging/prod ขนาดเล็ก-กลาง.

ข้อกำหนด rule: prod deploy ต้องผ่าน staging ก่อน; ทุก release ต้องมี rollback plan + tag + changelog;
DB migration ต้องมี backup ก่อนรันบน prod; ห้าม deploy ศุกร์เย็น/ก่อนวันหยุดยาว (ยกเว้น hotfix).

## สิ่งที่ scaffold นี้ครอบ vs ไม่ครอบ

ครอบ: build image App tier (**1 host — API, non-root, /health/ready**, Worker's outbox dispatchers merge
เข้าตัวเดียวกันแล้ว), migrate one-shot (bootstrap principals + EF migrations ต่อ DB tier ระยะไกล, bounded
retry รอ DB tier reachable ก่อน timeout), file-secret injection (DB principal password + vault master key +
DB tier CA cert), healthcheck + restart.

ไม่ครอบ (ceiling — ต้องเสริมเอง หรือเป็นของ infra/DBA): TLS termination / reverse proxy (nginx/caddy + cert)
หน้า API; **การ provision/ออก certificate/เปิด firewall ACL ของ DB tier เอง** (infra/DBA เป็นคนทำ, นอกสโคป
compose นี้); **HA ของ DB tier (SQL replica/Availability Group) หรือ Edge/DMZ load-balancer failover — ยัง
ไม่ implement ในสโคปนี้ ยอมรับเป็น ceiling ที่ยังค้างอยู่โดยตั้งใจ** (มี DB tier server เดียว, App tier server
เดียว ไม่มี replica); secret manager จริง (Vault/SOPS) แทน file ใน ./secrets/; log shipping.

## 0. Prerequisites

- Docker + Docker Compose v2 บน App tier host
- clone repo บน App tier host (compose build จาก source; migrate รัน EF จาก source ด้วย)
- host เปิด port ตาม `.env` (default API 5100; container ฟัง http 8080 ข้างใน) หรือวางหลัง reverse proxy
- **DB tier (Server 1) ต้อง provision เสร็จแล้วโดย infra/DBA ก่อน**: SQL Server 2025 Standard บน bare VM
  (ไม่ใช่ Docker), เปิด TCP 1433 ผ่าน firewall ACL ให้ App tier host เข้าถึงได้, มี login `sa` (หรือ
  sysadmin-capable login เทียบเท่า) ใช้ bootstrap/migrate ได้จริง — ถ้า DB tier เป็น hardened install ที่
  ปิด/rename `sa` ต้องคุยกับ infra/DBA ก่อน first deploy (ไม่ใช่เรื่องที่ runbook นี้แก้ให้ได้)
- รู้ hostname/IP + port ของ DB tier (`DB_SERVER`/`DB_PORT`) และถ้าจะ pin certificate เอง ต้องมีไฟล์
  CA/server cert ของ DB tier พร้อมแล้ว (ไม่บังคับ — ดูข้อ 1)

## 1. Config + secrets

```bash
cp .env.prod.example .env          # แก้ค่า non-secret + ตั้ง MSSQL_SA_PASSWORD (bootstrap-only)
mkdir -p secrets                   # ./secrets/ ถูก gitignore แล้ว
```

`.env` ต้องตั้งค่า DB tier ด้วย (REQ ของ multi-tier-deployment): `DB_SERVER`/`DB_PORT` = hostname/IP + port
จริงของ DB tier (Server 1, infra/DBA จัดเตรียมตามข้อ 0) — ไม่ใช่ literal `sql` แบบเดิม, ไม่มี same-compose
service ให้ต่อแล้ว. `DB_CA_CERTIFICATE_FILE` ไม่บังคับ: ตั้งเป็น `/run/secrets/db_ca_cert` เพื่อ pin
certificate ของ DB tier เข้ากับ `Encrypt=Strict` (ปล่อยว่างถ้า cert ของ DB tier chain ไป public CA อยู่แล้ว
— fallback เป็น `Encrypt=True;TrustServerCertificate=False` ต่อ OS trust store อัตโนมัติ, **ไม่มี env var ไหน
ทำให้ trust flag กลายเป็นค่า "ยอมรับทุก certificate" ได้**).

`.env` ต้องตั้ง (required — API ไม่ start ถ้าไม่มี): `MERCHANT_USER_FRONTEND_ORIGIN` + `ADMIN_FRONTEND_ORIGIN`
= origin ของ 2 SPA (CORS allowlist, scheme+host+port ไม่มี trailing slash). ทั้ง merchant-user และ admin เป็น
server-side OIDC BFF คนละ **confidential** Google OAuth client (type **Web application**), คนละ scheme/cookie/
callback เต็ม — ไม่ใช่ id-token bearer แบบเดิมอีกแล้ว:

- `MERCHANT_USER_OIDC_CLIENT_ID` = client ของ merchant-user SPA; client **secret** ใส่เป็น secret file
  (`merchant_user_oidc_client_secret`) ด้านล่าง ไม่ใช่ env. Authorized redirect URI ที่ Google client นั้น =
  `https://<api-host>/api/v1/merchants/auth/google/callback`.
- `ADMIN_OIDC_CLIENT_ID` = client ของ admin console; client **secret** ใส่เป็น secret file
  (`admin_oidc_client_secret`) ด้านล่าง ไม่ใช่ env. Authorized redirect URI ที่ Google client นั้น =
  `https://<api-host>/api/v1/admins/auth/google/callback`.

OIDC เป็น provider-scoped ทั้งสองฝั่งแล้ว (`multi-provider-oidc`) — Google เป็น provider บังคับ (operator var
ด้านบนไม่เปลี่ยนชื่อ) ส่วน **Microsoft Entra ID เป็น provider เสริม** (opt-in) ซึ่ง `docker-compose.prod.yml`
wire ให้แล้ว: เปิดใช้โดยตั้งใน `.env` — `ADMIN_ENTRA_CLIENT_ID` + `ADMIN_ENTRA_TENANT_ID` (admin เป็น
single-tenant; compose ประกอบ `AdminAuth__Providers__Microsoft__Authority` จาก tenant id ให้เอง) และ/หรือ
`MERCHANT_ENTRA_CLIENT_ID` (merchant-user ใช้ authority `/organizations` จาก appsettings.json) — แล้วใส่
client secret จริงลง secret file `admin_entra_client_secret` / `merchant_entra_client_secret` ด้านล่าง
(`docker/entrypoint.sh` map เป็น `AdminAuth__Providers__Microsoft__ClientSecret` /
`MerchantAuth__Providers__Microsoft__ClientSecret` เอง). เว้น `*_ENTRA_CLIENT_ID` ว่าง = ปิด Microsoft login
(scheme ถูก skip, boot Google-only ปกติ) แต่**ไฟล์ secret ทั้งสองต้องมีอยู่เสมอ**เป็น placeholder เปล่า —
กติกาเดียวกับ `db_ca_cert` (compose บังคับ path); deploy ที่มีอยู่แล้ว หลัง pull ต้องสร้าง 2 ไฟล์นี้ก่อน `up`
ไม่งั้น compose fail.
redirect URI ของ Entra client = `https://<api-host>/api/v1/admins/auth/microsoft/callback` (admin) /
`https://<api-host>/api/v1/merchants/auth/microsoft/callback` (merchant-user) — ต้องสร้าง app registration
คนละตัวต่อฝั่ง (admin single-tenant, merchant-user multi-tenant) และเพิ่ม optional claim `email` ที่ id_token
(Entra ไม่ส่ง `email`/`email_verified` โดย default).

Non-secret PSP operational config (`Payments.Infrastructure/Psp/PspOptions.cs`, ไม่ fail-fast แต่ blank แล้ว
redirect พังเงียบๆ — ตั้งให้ครบ): `PSP_USE_SANDBOX` (default `true`; ตั้ง `false` เฉพาะตอนใช้ PSP credential จริง),
`PSP_TWOCTWOP_FRONTEND_RETURN_URL` (2C2P ส่ง browser ลูกค้ากลับหลัง hosted page), `PSP_TWOCTWOP_BACKEND_RETURN_URL`
(2C2P POST callback -> route จริงคือ `POST /api/v1/webhooks/{pspConnectionId}`, **ไม่ใช่** `/webhooks` เฉยๆ —
`{pspConnectionId}` คือ `Id` ของแถวใน `txn.PspConnections` ของคู่ merchant+2C2P นั้น ไม่ใช่ค่าคงที่ หาได้จาก DB
หลัง provision merchant. ช่องโหว่ที่ยังไม่ปิด: ค่านี้เป็น env เดียวทั้งแพลตฟอร์ม แต่ route ต้องการ id ต่อ
connection — ถูกต้องเฉพาะตอนมี 2C2P connection เดียวทั้งระบบ, หลาย merchant ใช้ 2C2P พร้อมกันคือของที่ยังค้าง
ใน production-hardening PR3), `PSP_OMISE_RETURN_URI` (Omise ส่ง browser กลับหลัง hosted 3DS เท่านั้น — webhook
ของ Omise ตั้งแยกใน Omise dashboard เอง ไม่ผ่าน env นี้).

สร้าง secret file (ทุกไฟล์ = บรรทัดเดียว; entrypoint อ่านด้วย $(cat) ตัด trailing newline ให้อยู่แล้ว):

```bash
# DB principal password (1 principal เท่านั้นตอนนี้ — pol_admin/pol_worker ถูกถอดทิ้งแล้วใน
# rls-to-query-filter, ดู db-connection-and-rls.md) — ต้องผ่าน SQL complexity (CHECK_POLICY=ON):
# >=8 ตัว, upper+lower+digit
printf '%s' "Ci$(openssl rand -hex 10)Aa1" > secrets/pol_app_password

# Vault master key — 32-byte AES key, base64 (PR4 keyring อ่านจาก KeyFile; active id = v1)
head -c 32 /dev/urandom | base64 > secrets/vault_master_key

# Merchant-user OIDC client secret — confidential client secret ของ merchant-user SPA (คู่กับ MERCHANT_USER_OIDC_CLIENT_ID).
# ไม่ใช่ random: paste ค่าจริงจาก Google Cloud Console (OAuth 2.0 Client ของ merchant-user = Web application -> Client secret).
printf '%s' 'GOCSPX-...paste-from-google-console...' > secrets/merchant_user_oidc_client_secret

# Admin OIDC client secret — confidential client secret ของ admin console (คู่กับ ADMIN_OIDC_CLIENT_ID).
# ไม่ใช่ random: paste ค่าจริงจาก Google Cloud Console (OAuth 2.0 Client ของ admin = Web application -> Client secret).
printf '%s' 'GOCSPX-...paste-from-google-console...' > secrets/admin_oidc_client_secret

# Entra client secrets (opt-in) — compose บังคับให้มีไฟล์เสมอ: ปิด Microsoft login = ไฟล์เปล่า placeholder,
# เปิด = paste client secret จริงจาก Entra app registration ของฝั่งนั้น (Certificates & secrets -> Client secret).
touch secrets/admin_entra_client_secret secrets/merchant_entra_client_secret

# DB tier CA cert (pin optional) — compose mount ไฟล์นี้เสมอไม่ว่าจะ pin หรือไม่ (ต้องมีไฟล์อยู่จริง). ถ้า
# DB tier ใช้ certificate ที่ chain ไป public CA อยู่แล้ว: ปล่อย DB_CA_CERTIFICATE_FILE ว่างใน .env แล้ว
# เก็บไฟล์นี้เป็น placeholder เปล่า (ไม่ได้ถูกอ่านเลยเมื่อ env ว่าง). ถ้าจะ pin เอง: เอา CA/server cert ตัวจริง
# ของ DB tier มาวาง (ต้องเป็น PEM — migrate ติดตั้งเข้า OS trust store ตอน start ด้วย
# update-ca-certificates ซึ่งรับ PEM เท่านั้น) แล้วตั้ง DB_CA_CERTIFICATE_FILE=/run/secrets/db_ca_cert ใน .env.
# api ใช้ไฟล์เดียวกันผ่าน connection string (ServerCertificate=...) ส่วน migrate ติดตั้งเข้า trust store
# ตอน runtime — ไม่มีขั้นตอน build-time ใด ๆ (image ถูก build บน CI ที่ไม่มี cert นี้อยู่แล้ว).
touch secrets/db_ca_cert   # หรือ: cp /path/to/db-tier-ca.pem secrets/db_ca_cert (ถ้าจะ pin)

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

ลำดับ: `migrate` (รอ DB tier reachable ผ่าน bounded retry -> bootstrap principals + apply migrations ต่อ
DB tier ระยะไกล แล้ว exit 0) -> `api` start (1 host — Worker's outbox dispatchers รันในตัวเดียวกันแล้ว).
ถ้า DB tier ยังต่อไม่ได้ (firewall ACL ยังไม่เปิด, DNS ผิด ฯลฯ) `migrate` จะ retry แล้ว exit ไม่ใช่ 0
(`docker compose ... ps` เห็น `migrate` เป็น `Exited (1)`) — เช็ค log ก่อนสงสัยอย่างอื่น:
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

## 3.1 Seq — sink ของ denial/authz telemetry (ตั้ง retention/alerting หลัง boot แรก)

compose นี้มี service `seq` (`datalust/seq`, volume `seq-data`) เป็น external tamper-resistant sink ของ
denial/authz telemetry (REQ-13.4). `api` ผูก `depends_on: seq: condition: service_healthy` ไว้ ดังนั้น
**seq ไม่ healthy = api ไม่ start เลย** — ไม่ใช่ container เสริมที่ข้ามได้: ถ้า `docker compose ... ps` เห็น
`api` ค้างไม่ขึ้น ให้ดู `logs seq` ก่อนสงสัยอย่างอื่น. (ตอน runtime sink degrade เป็น log-only ถ้า Seq ล่ม
ภายหลัง ไม่ block request — gate มีเฉพาะตอน start.) `Seq__IngestionUrl` default `http://seq:80` = ในเน็ตเวิร์ก
compose เท่านั้น ไม่ใช่ secret ไม่ต้องตั้งใน `.env`.

**retention + alerting ตั้งฝั่ง Seq เท่านั้น ไม่มี env var ฝั่ง app** — ทำครั้งเดียวหลัง boot แรกที่ Seq UI:
Settings -> Retention policies (ตั้งอายุ log ให้พอดีดิสก์ที่ volume `seq-data` กินได้) + signal/notification
สำหรับ alert. compose **ไม่ publish port ของ seq ออก host** (ตั้งใจ — Seq boot แรกยังไม่มี authentication)
เข้าผ่าน SSH tunnel ไปที่ IP ของ container แทน แล้วเปิด `http://localhost:8081`:

```bash
SEQ_IP=$(ssh <deploy-user>@<app-host> "docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' pol-core-seq-1")
ssh -L 8081:$SEQ_IP:80 <deploy-user>@<app-host>
```

ถ้าจะเปิดถาวรให้ทีมใช้: ตั้ง authentication ใน Seq ก่อน แล้วค่อยเพิ่ม `ports: ["127.0.0.1:5341:80"]` ที่
service `seq` (bind loopback + หน้า reverse proxy เท่านั้น — อย่า bind `0.0.0.0`).

## 4. Upgrade deploy (มี migration ใหม่)

```bash
# 4.1 BACKUP ก่อน (rule: migration บน prod ต้อง backup ก่อน) — DB tier เป็น host แยกแล้ว, ไม่มี service
# `sql` ใน compose นี้ให้ exec เข้าไปอีกต่อไป: ประสาน DBA ให้ backup ตรงบน DB tier (Server 1) เอง ก่อนกด
# deploy ทุกครั้งที่มี migration ใหม่ (ตัวอย่างคำสั่งที่ DBA รันบน Server 1 เอง ไม่ใช่จาก App tier):
sqlcmd -S localhost -U sa -P "<SA password ของ DB tier>" -N \
  -Q "BACKUP DATABASE [VCentralPay] TO DISK='/var/opt/mssql/backup/pre-deploy.bak' WITH INIT, COMPRESSION"

# 4.2 ดึงโค้ดใหม่ + rebuild + rerun migrate + restart host
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
รันใน migrate image (มี dotnet-ef + source) — `migrate`'s `environment:` block ใน compose ฉีด
`DB_SERVER`/`DB_PORT`/`DB_CA_CERTIFICATE_FILE`/`DB_NAME`/`MSSQL_SA_PASSWORD` จาก `.env` ให้อยู่แล้ว ใช้
logic การประกอบ connection string เดียวกับ `docker/migrate-entrypoint.sh` เป๊ะ (pin cert ถ้าตั้ง
`DB_CA_CERTIFICATE_FILE` ไม่งั้น fallback `Encrypt=True;TrustServerCertificate=False` — ไม่มีทางได้ค่า
trust-any-certificate เดิม):
```bash
docker compose -f docker-compose.prod.yml run --rm --entrypoint sh migrate -c '
  : "${DB_PORT:=1433}";
  if [ -n "${DB_CA_CERTIFICATE_FILE:-}" ]; then
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=Strict;ServerCertificate=${DB_CA_CERTIFICATE_FILE};HostNameInCertificate=${DB_SERVER}";
  else
    export POL_DESIGN_SQL="Server=${DB_SERVER},${DB_PORT};Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=False";
  fi;
  dotnet ef database update <PreviousMigrationName> \
    --project src/BuildingBlocks/BuildingBlocks.Infrastructure --startup-project src/Hosts/Api'
```
ถ้า migration rollback เสี่ยง (data loss) -> restore จาก backup ที่ DBA ทำไว้บน DB tier (Server 1, ข้อ 4.1)
แทน. ออกแบบ migration ให้ backward-compatible (expand/contract) เพื่อให้ app เก่า+ใหม่ทำงานกับ schema
เดียวกันได้ระหว่าง roll.

## 6. Deploy ผ่าน GitLab CI (ทางหลักหลังตั้งระบบครั้งแรก)

โค้ดหลักอยู่ GitHub; GitLab องค์กร (`gitlab2.viriyah.co.th/central-software/vcentralpayapi`)
เป็นช่อง CI/CD: GitHub Actions (`mirror-gitlab.yml`) push mirror `develop`/`main`/tag `v*` ให้อัตโนมัติ
แล้ว pipeline (`.gitlab-ci.yml`) รัน gate เดิม + build/push image เข้า GitLab Container Registry.
ขั้นตอนตั้งค่าครั้งแรกแบบละเอียดทีละคลิก: [gitlab-cicd-setup.md](gitlab-cicd-setup.md).

Flow (2 environment, manual gate ทั้งคู่):

1. merge เข้า develop บน GitHub ตามปกติ (PR + CI GitHub เป็น merge gate เดิม) — mirror ไป GitLab เอง
2. **UAT**: pipeline ของ develop build image `:short-sha` เข้า registry → กด play job `deploy-uat`
   (environment `uat`) เพื่อยก UAT เป็น commit นั้น
3. **Prod**: tag `vX.Y.Z` + changelog (rule เดิม, ผ่าน UAT แล้ว) แล้ว push tag — pipeline ของ tag
   build image `:vX.Y.Z` → กด play job `deploy-prod` (environment `production`)
4. job deploy ทั้งสองทำเหมือนกัน: scp `docker-compose.prod.yml` + `docker-compose.registry.yml`
   ไป host แล้ว ssh รัน `docker compose ... pull` + `up -d --no-build` (ลำดับ `migrate` -> `api` ตาม
   `depends_on` เดิม — `migrate` รอ DB tier reachable เองก่อนแล้วค่อย bootstrap+migrate) แล้ว verify
   `/health/ready`

หมายเหตุ:

- ข้อ 1-3 ของ runbook นี้ (`.env`, `./secrets/`, first deploy) ยังเป็นขั้น manual บนแต่ละ host เหมือนเดิม —
  GitLab deploy ไม่แตะ secret ใด ๆ, ใช้สำหรับ upgrade รอบถัดไปแทนข้อ 4.2 (backup ข้อ 4.1 ยังต้องทำก่อนกดเสมอ)
- rollback ผ่าน GitLab = กด job deploy จาก pipeline ของ commit/tag ก่อนหน้า (image เก่ายังอยู่ใน registry);
  DB rollback ยังใช้ข้อ 5 เดิม
- ตัวแปร CI/CD ที่ infra ต้องตั้งใน GitLab (protected, **environment-scoped** — ชื่อเดียวกัน แยกค่าต่อ
  scope `uat`/`production`): `SSH_PRIVATE_KEY` (File), `SSH_KNOWN_HOSTS` (File),
  `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_PATH`, `REGISTRY_DEPLOY_USER`/`REGISTRY_DEPLOY_TOKEN`
  (deploy token scope `read_registry`, คนละใบต่อ env); optional สำหรับ job integration: `POL_SA_PASSWORD` (masked).
  ฝั่ง GitHub ต้องมี secret `GITLAB_MIRROR_TOKEN` (project access token scope `write_repository`).
  Runner ต้องเป็น docker executor และ job `package` ต้องมี privileged (DinD) หรือสลับเป็น kaniko.
- host เตรียมครั้งเดียว: user SSH อยู่ group `docker` + authorize key ของ CI + `$DEPLOY_PATH` มี `.env` และ `secrets/`

## 7. SA password rotation (post-bootstrap)

`sa` ใช้แค่ตอน bootstrap/migrate (รันบน DB tier, Server 1) — runtime (Api ทั้ง flow HTTP + background
dispatcher ที่ merge เข้ามาแล้ว) ต่อด้วย principal เดียว `pol_app` เท่านั้น (ดู
[db-connection-and-rls.md](../reference/db-connection-and-rls.md)). หลัง deploy แรก หมุน SA ได้ (ทำบน
DB tier โดย DBA): `ALTER LOGIN sa WITH PASSWORD='...'` แล้วอัปเดต `MSSQL_SA_PASSWORD` ใน `.env` ของ App tier
(ใช้รอบ migrate ถัดไป).
