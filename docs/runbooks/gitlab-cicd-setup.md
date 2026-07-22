# Runbook: ตั้งค่า GitLab CI/CD channel (จับมือทำทีละขั้น)

คู่มือตั้งค่าครบทุกฝั่งสำหรับระบบ deploy ผ่าน GitLab องค์กร
(`gitlab2.viriyah.co.th/central-software/central-payment-gateway`) ตามที่วางไว้ใน PR #125.
เรียงตามลำดับที่ต้องทำจริง — ทำ Part A ไป F ทีละขั้น เช็คผลทุกขั้นก่อนไปต่อ.

ภาพรวม: โค้ดหลัก + PR + merge gate อยู่ GitHub (`metrodiesign/pol-core`) เหมือนเดิม.
GitHub Actions (`.github/workflows/mirror-gitlab.yml`) push-mirror `develop`/`main`/tag `v*`
ไป GitLab อัตโนมัติ → GitLab pipeline (`.gitlab-ci.yml`) รัน gate เดิม + build/push image เข้า
Container Registry → deploy 2 environment (manual ทั้งคู่, SSH + `docker compose pull` +
`up -d --no-build`):

- `deploy-uat` — pipeline ของ branch `develop`, deploy image `:short-sha` ของ commit นั้น
- `deploy-prod` — pipeline ของ tag `v*` เท่านั้น, deploy image `:vX.Y.Z`

```
GitHub (code of record)          GitLab (CI/CD channel)
  PR -> merge develop       --mirror-->  pipeline develop: verify -> dotnet -> package
                                           -> deploy-uat (manual)   --ssh-->  UAT host
  tag vX.Y.Z                --mirror-->  pipeline tag: verify -> dotnet -> package
                                           -> deploy-prod (manual)  --ssh-->  Prod host
```

ลำดับใหญ่: ตั้ง **UAT ก่อน** (Part A-F) เพื่อทดสอบให้ครบวงจร → ค่อยเปิด prod
(Part G — วน C-E ซ้ำด้วย key/token ชุดใหม่ + scope `production`).

---

## Part A — GitLab: เตรียม project (ยังไม่ต้องมี server)

### A1. เช็คสิทธิ์

- เปิด `https://gitlab2.viriyah.co.th/central-software/central-payment-gateway`
- Project information → Members → role ตัวเองต้องเป็น **Maintainer/Owner** — ถ้าไม่ใช่
  ขอสิทธิ์ก่อน ทำต่อไม่ได้

### A2. Protect branch

- เมนูซ้าย: **Settings → Repository** → section **Protected branches** → กด Expand
- ช่อง Branch: พิมพ์ `develop` → Allowed to merge: `Maintainers` →
  Allowed to push and merge: `Maintainers` → กด **Protect**
- ทำซ้ำกับ `main`
- เช็คผล: ตารางต้องมีทั้ง `develop` และ `main`

เหตุผล: CI/CD variables ที่ติ๊ก Protected จะถูกส่งให้เฉพาะ pipeline บน ref ที่ protected —
ไม่ protect = job deploy ไม่เห็นตัวแปร. (mirror push ด้วย token role Maintainer จึงผ่านได้.)

### A3. Protect tag

- หน้าเดิม section **Protected tags** → Expand
- ช่อง Tag: พิมพ์ `v*` (เลือก "Create wildcard v*") → Allowed to create: `Maintainers` → **Protect**

### A4. Project access token (สำหรับ mirror)

- **Settings → Access tokens** → **Add new token**

| ช่อง | ค่า |
|---|---|
| Token name | `github-mirror` (label เฉย ๆ) |
| Expiration date | กด x ลบออกถ้าลบได้; ถ้า instance บังคับ เลือกไกลสุด (มัก 1 ปี) แล้ว**จดวันหมดอายุ** — หมดแล้ว mirror จะ fail เงียบ ๆ ฝั่ง GitHub |
| Role | `Maintainer` — ต้องระดับนี้เพราะ mirror ต้อง force-push เข้า protected branch/tag; `Developer` โดน reject |
| Scopes | ติ๊ก `write_repository` ตัวเดียว |

- กด **Create project access token** → ค่า token โชว์**ครั้งเดียว** → copy ค้างไว้
  (อย่าเพิ่งปิดแท็บ — ใช้ใน Part B1). ห้าม paste token ลงไฟล์/chat/commit.

### A5. เช็ค Container Registry เปิดอยู่

- เมนูซ้าย: มองหา **Deploy → Container Registry** — ถ้ามี = เปิดแล้ว ข้ามไป Part B
- ถ้าไม่มี: **Settings → General → Visibility, project features, permissions** → Expand →
  เปิด toggle **Container Registry** → **Save changes**

---

## Part B — GitHub: ใส่ secret + merge + ทดสอบ mirror

### B1. ตั้ง secret

- ไป `https://github.com/metrodiesign/pol-core` → **Settings → Secrets and variables → Actions**
- กด **New repository secret**
- Name: `GITLAB_MIRROR_TOKEN` (พิมพ์ให้ตรงเป๊ะ)
- Secret: paste token จาก A4 → **Add secret** → ปิดแท็บ GitLab ของ A4 ได้แล้ว

### B2. Merge PR #125

- เปิด `https://github.com/metrodiesign/pol-core/pull/125` → รอ CI เขียว → merge ตาม flow ปกติ

### B3. ทดสอบ mirror ครั้งแรก

- ตัว merge ใน B2 = push เข้า develop อยู่แล้ว → แท็บ **Actions** ของ GitHub →
  workflow run ชื่อ **Mirror to GitLab** → ต้องเขียว
- ถ้าแดง `Invalid username or token`: token ผิด — ทำ A4 + B1 ใหม่
- ถ้าแดง `pre-receive hook declined`: role token ไม่ถึง Maintainer หรือ protected branch
  ตั้ง Allowed to push ไม่รวม Maintainers — เช็ค A2/A4
- เช็คฝั่ง GitLab: หน้า Repository ต้องเห็น commit ล่าสุดตรงกับ GitHub

### B4. ดู pipeline แรกบน GitLab

- GitLab เมนูซ้าย: **Build → Pipelines** → ต้องมี pipeline ของ commit นั้นกำลังรัน
- รอจน `verify`, `dotnet`, `package` เขียว (ครั้งแรกช้าสุดเพราะยังไม่มี cache — 15-30 นาที)
- job `deploy-uat` โผล่เป็นปุ่มเล่น (manual) ท้าย pipeline — **ยังไม่ต้องกด** ยังไม่มี server
- เช็ค image: **Deploy → Container Registry** → ต้องเห็น `api`, `worker`, `migrate`
  มี tag เป็น short SHA
- ถ้า `package` แดง `Cannot connect to the Docker daemon`: runner ไม่มี privileged —
  หยุด แล้วประสานสลับ job เป็น kaniko (ดู Part A6 Runner ด้านล่าง)
- ถ้าไม่มี runner รับ job (pending ค้าง): ติดต่อทีม infra ขอ runner ให้ project

Runner requirement (ให้ทีม infra): docker executor; job `package` ต้อง `privileged = true`
(DinD); egress ที่ต้องออกได้: `mcr.microsoft.com`, `registry-1.docker.io`, `api.nuget.org`,
registry ของ GitLab เอง, SSH (22) ไป deploy host.

**ถึงจุดนี้ CI ครบแล้ว — เหลือส่วน deploy (Part C-F)**

---

## Part C — สร้าง SSH key ของ UAT (ทำบนเครื่อง dev ตัวเอง)

**ห้ามใช้ key ส่วนตัวที่มีอยู่** — gen คู่ใหม่เฉพาะ CI. คนละคู่ต่อ environment
(หลุดใบเดียวไม่พังทั้งคู่).

### C1. gen keypair

```bash
cd ~/Desktop
ssh-keygen -t ed25519 -C "gitlab-ci-uat" -f ./gitlab_ci_uat -N ""
```

ได้ 2 ไฟล์: `gitlab_ci_uat` (private) + `gitlab_ci_uat.pub` (public)

### C2. สร้างค่า known_hosts

ต้องรู้ IP/hostname ของ UAT server ก่อน (ตัวอย่างนี้ใช้ `10.0.0.50` — แทนด้วยของจริง):

```bash
ssh-keyscan -H 10.0.0.50 > ./gitlab_ci_uat_known_hosts
cat ./gitlab_ci_uat_known_hosts
# ต้องมีบรรทัดขึ้นต้น |1| ตามด้วย ssh-ed25519/ssh-rsa — ห้ามว่าง
```

- ถ้าว่าง = เครื่องคุณถึง server ไม่ได้ (VPN/firewall) — รันจากเครื่องที่ถึงได้แทน
- กัน MITM: เทียบ fingerprint กับ `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`
  ที่รันบน server ตรง ๆ

---

## Part D — UAT server (ทำครั้งเดียวต่อ host)

สมมุติ server ผ่าน first install ตาม [deploy-self-host.md](deploy-self-host.md) ข้อ 0-3 แล้ว
(มี Docker, มี directory ที่มี `.env` + `secrets/`, ระบบรันอยู่) — ถ้ายัง ทำนั่นก่อน.
ตัวอย่างใช้ user `deploy` + path `/opt/pol-core` — แทนด้วยของจริง.

### D1. SSH เข้า server ด้วย user ที่จะให้ CI ใช้

```bash
ssh deploy@10.0.0.50
```

### D2. เช็ค/เพิ่ม group docker

```bash
groups              # ต้องเห็นคำว่า docker
# ถ้าไม่มี:
sudo usermod -aG docker $USER
exit                # ออกแล้ว ssh เข้าใหม่ให้ group ติด
```

### D3. ใส่ public key ของ CI

```bash
mkdir -p ~/.ssh && chmod 700 ~/.ssh
echo 'PASTE_เนื้อไฟล์_gitlab_ci_uat.pub_ตรงนี้' >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
```

(เนื้อไฟล์ .pub ดูจากเครื่องตัวเอง: `cat ~/Desktop/gitlab_ci_uat.pub` — บรรทัดเดียว
ขึ้นต้น `ssh-ed25519`)

### D4. เช็ค path deploy พร้อม

```bash
ls -la /opt/pol-core/.env /opt/pol-core/secrets/
docker compose -f /opt/pol-core/docker-compose.prod.yml ps   # ระบบเดิมรันอยู่
```

### D5. ทดสอบ key จากเครื่องตัวเอง (terminal ใหม่)

```bash
ssh -i ~/Desktop/gitlab_ci_uat deploy@10.0.0.50 'docker ps'
```

ต้องเข้าได้**ไม่ถามรหัส** + เห็นรายการ container — ผ่าน = ฝั่ง server จบ.

หมายเหตุ: host ไม่ต้องเข้าถึง GitHub/GitLab ทาง git อีก — image มาจาก registry;
repo ที่ clone ไว้ใช้แค่ first install / rollback แบบ manual (runbook เดิมข้อ 4-5).

---

## Part E — GitLab: deploy token + variables (scope `uat`)

### E1. Deploy token (สำหรับ host pull image — คนละใบต่อ environment)

- **Settings → Repository → Deploy tokens** → Expand → กรอก:

| ช่อง | ค่า |
|---|---|
| Name | `uat-registry-pull` (label เฉย ๆ; รอบ prod ค่อยสร้าง `prod-registry-pull`) |
| Expiration date | เว้นว่าง หรือตาม policy — หมดอายุแล้ว host จะ pull ไม่ได้ตอน deploy |
| Username | เว้นว่างได้ GitLab gen ให้ (รูปแบบ `gitlab+deploy-token-<N>`) |
| Scopes | ติ๊ก `read_registry` ตัวเดียว |

- กด **Create deploy token** → โชว์ 2 ค่า**ครั้งเดียว**: username + token → copy ทั้งคู่
  ไว้ใช้ใน E2 (ตัวที่ 6-7)
- deploy token ไม่มี role ให้เลือก (ต่างจาก access token A4 — คนละชนิด คนละเมนู).
  เหตุที่ไม่ใช้ `CI_JOB_TOKEN` บน host: job token ตายพร้อม job — host ต้อง re-pull ได้ภายหลัง.

### E2. CI/CD variables

**Settings → CI/CD → Variables** → Expand → **Add variable** ทีละตัว 7 รอบ.
ทุกตัวตั้งเหมือนกัน 2 อย่าง: ติ๊ก **Protect variable** + ช่อง **Environment scope**
เลือก/พิมพ์ `uat` (อย่าปล่อย All — รอบ prod จะ add ชื่อเดิมซ้ำด้วย scope `production`
แล้ว GitLab เลือกค่าให้ job ตาม environment เอง):

| # | Key | Type | Flags เพิ่ม | Value |
|---|---|---|---|---|
| 1 | `SSH_PRIVATE_KEY` | **File** | (mask ไม่ได้ — หลายบรรทัด) | ทั้งเนื้อไฟล์จาก `cat ~/Desktop/gitlab_ci_uat` — ตั้งแต่ `-----BEGIN OPENSSH PRIVATE KEY-----` ถึง `-----END OPENSSH PRIVATE KEY-----` รวม 2 บรรทัดนั้นด้วย |
| 2 | `SSH_KNOWN_HOSTS` | **File** | | ทั้งเนื้อไฟล์จาก `cat ~/Desktop/gitlab_ci_uat_known_hosts` |
| 3 | `DEPLOY_HOST` | Variable | | IP/hostname ของ UAT server เช่น `10.0.0.50` |
| 4 | `DEPLOY_USER` | Variable | | user SSH เช่น `deploy` |
| 5 | `DEPLOY_PATH` | Variable | | path deploy เช่น `/opt/pol-core` |
| 6 | `REGISTRY_DEPLOY_USER` | Variable | | username จาก E1 |
| 7 | `REGISTRY_DEPLOY_TOKEN` | Variable | ติ๊ก **Mask variable** | token จาก E1 |

- Type = dropdown "Variable / File" ตอน add — ตัวที่ 1-2 ต้องเลือก **File** เท่านั้น
  (pipeline ได้ path ไฟล์มา ตรงกับที่ `.gitlab-ci.yml` ใช้ `cp "$SSH_PRIVATE_KEY" ...`)
- (optional) `POL_SA_PASSWORD` — Variable, Protected + Masked, scope All — ใส่เฉพาะ
  ถ้าจะกดรัน job `integration` บน GitLab

### E3. เก็บกวาด — บนเครื่องตัวเอง

```bash
rm ~/Desktop/gitlab_ci_uat ~/Desktop/gitlab_ci_uat.pub ~/Desktop/gitlab_ci_uat_known_hosts
```

ถ้า private key เคยหลุด: ลบบรรทัดนั้นใน `authorized_keys` บน host แล้ว gen คู่ใหม่ทันที.

---

## Part F — ยิง deploy-uat จริง

### F1. เปิด pipeline

GitLab → **Build → Pipelines** → เปิด pipeline ล่าสุดของ `develop` (ที่ `package` เขียวแล้ว)

### F2. กดปุ่มเล่นที่ job `deploy-uat` แล้วเปิด log ดูสด

ต้องเห็นตามลำดับ:

1. `apk add openssh-client` ผ่าน
2. `scp` ไม่มี error
3. `docker login` → `Login Succeeded`
4. `pull` ลาก image 3 ตัว (migrate, api, worker)
5. `up -d` → recreate `migrate`, `api`, `worker`
6. `docker compose ps` → `api`/`worker` = `healthy` (หรือ `starting`), `migrate` = `Exited (0)`
7. `curl /health/ready` ตอบ JSON `{"status":"healthy"}` → job เขียว

### F3. เช็คของจริง

- เปิด URL ของ UAT ใช้งานดู
- บน server: `docker compose -f docker-compose.prod.yml -f docker-compose.registry.yml ps`

### F4. Rollback drill (ซ้อมเลยตอนนี้)

- เปิด pipeline ของ commit **ก่อนหน้า** บน develop → กด `deploy-uat` จากตรงนั้น →
  ระบบกลับเวอร์ชันเก่า (image เก่ายังอยู่ใน registry) → กดของ commit ล่าสุดกลับ

วน F1-F3 กับทุก merge จน flow นิ่ง — จบส่วน UAT.

---

## Part G — เปิด prod (ทำหลัง UAT นิ่งแล้ว)

1. วน **Part C** ใหม่: gen key คู่ใหม่ชื่อ `gitlab_ci_prod` + `ssh-keyscan` ของ prod host
   (ห้ามใช้ key ร่วมกับ UAT)
2. วน **Part D** บน prod host (user, group docker, authorized_keys, เช็ค `.env` + `secrets/`)
3. วน **Part E**: deploy token ใบใหม่ `prod-registry-pull` + add variables **ชื่อเดิมทั้ง 7 ตัว**
   อีกรอบ โดย Environment scope = `production` ค่าเป็นของ prod host
4. Deploy: **backup DB ก่อน** (runbook deploy ข้อ 4.1) → tag release ฝั่ง GitHub
   (rule เดิม: tag + changelog, ผ่าน UAT แล้ว):
   ```bash
   git tag v0.1.0 && git push origin v0.1.0
   ```
   → mirror → pipeline ของ tag build image `:v0.1.0` → กด play `deploy-prod` →
   verify แบบเดียวกับ F2-F3
5. Rollback prod = กด `deploy-prod` จาก pipeline ของ tag ก่อนหน้า; DB rollback ใช้
   runbook deploy ข้อ 5 เดิม

---

## Troubleshooting

| อาการ | ไปแก้ที่ |
|---|---|
| mirror workflow แดง: `Invalid username or token` | A4 + B1 — `GITLAB_MIRROR_TOKEN` ผิด/หมดอายุ สร้างใหม่ |
| mirror แดง: `pre-receive hook declined` / protected | A2/A4 — token role ไม่ใช่ Maintainer หรือ protection ไม่อนุญาต push |
| job deploy: `$DEPLOY_HOST` ว่าง / `Could not resolve hostname` | E2 — variable ไม่ติด scope (`uat`/`production`) หรือติ๊ก Protected แต่ ref ไม่ protected (A2/A3) |
| `Host key verification failed` | C2 — known_hosts ไม่ตรง host รัน `ssh-keyscan` ใหม่แล้วอัปเดต variable |
| `Permission denied (publickey)` | D3 — public key ไม่อยู่ใน `authorized_keys` หรือ permission ผิด (ต้อง 600) |
| `docker login` บน host → `denied` | E1 — deploy token หมดอายุ/scope ผิด (ต้อง `read_registry`) หรือ copy ผิดค่า |
| บน server: `permission denied ... docker.sock` | D2 — user ไม่อยู่ group docker |
| job `package`: `Cannot connect to the Docker daemon` | B4 — runner ไม่มี privileged เปิด privileged หรือสลับเป็น kaniko |
| pipeline pending ค้าง ไม่มี job รัน | B4 — ไม่มี runner รับ project ติดต่อทีม infra |
