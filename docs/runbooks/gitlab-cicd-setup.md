# Runbook: ตั้งค่า GitLab CI/CD channel (ครั้งแรก)

คู่มือตั้งค่าครบทุกฝั่งสำหรับระบบ deploy ผ่าน GitLab องค์กร
(`gitlab2.viriyah.co.th/central-software/central-payment-gateway`) ตามที่วางไว้ใน PR #125.

ภาพรวม: โค้ดหลัก + PR + merge gate อยู่ GitHub (`metrodiesign/pol-core`) เหมือนเดิม.
GitHub Actions (`.github/workflows/mirror-gitlab.yml`) push-mirror `develop`/`main`/tag `v*`
ไป GitLab อัตโนมัติ → GitLab pipeline (`.gitlab-ci.yml`) รัน gate เดิม + build/push image เข้า
Container Registry → job `deploy-prod` (manual, เฉพาะ tag `v*`) SSH ไป prod host แล้ว
`docker compose pull` + `up -d --no-build`.

```
GitHub (code of record)          GitLab (CI/CD channel)              Prod host
  PR -> merge develop/main  --mirror-->  pipeline: verify -> dotnet
  tag vX.Y.Z                --mirror-->  -> package (push registry)
                                         -> deploy-prod (manual)  --ssh-->  compose pull + up
```

ลำดับทำ: ฝั่ง GitLab ข้อ 1-3 ก่อน → merge PR #125 → ทดสอบ mirror + pipeline →
ค่อยทำ GitLab ข้อ 4-5 + Prod host → ทดสอบ deploy จริงด้วย tag.

---

## ฝั่ง GitLab (ต้องเป็น Maintainer/Owner ของ project)

### 1. Protect branch + tag

Settings → Repository:

- **Protected branches**: เพิ่ม `develop` และ `main`
- **Protected tags**: เพิ่ม wildcard `v*`

จำเป็นเพราะ CI/CD variables ที่ตั้ง Protected จะถูกส่งให้เฉพาะ pipeline ที่รันบน ref
ที่ protected เท่านั้น — ไม่ protect = job deploy ไม่เห็นตัวแปร.

### 2. Project access token (สำหรับ mirror)

Settings → Access tokens → Add new token:

| ช่อง | ค่า |
|---|---|
| Token name | `github-mirror` (label เฉย ๆ) |
| Expiration date | เว้นว่างถ้าเลือกได้; ถ้า instance บังคับ ตั้งไกลสุด (มัก 1 ปี) แล้ว**จดวันหมดอายุ** — หมดแล้ว mirror จะ fail เงียบ ๆ ฝั่ง GitHub |
| Role | `Maintainer` — ต้องระดับนี้เพราะ mirror ต้อง force-push เข้า protected branch/tag; `Developer` โดน reject |
| Scopes | ติ๊ก `write_repository` ตัวเดียว |

กด Create → copy token ทันที (โชว์ครั้งเดียว) → เอาไปใส่ฝั่ง GitHub (ดูหัวข้อ GitHub ด้านล่าง).
ห้าม paste token ลงไฟล์/chat/commit.

### 3. เปิด Container Registry

Settings → General → Visibility, project features, permissions:

- toggle **Container Registry** ให้เปิด (บาง instance เปิด default — เช็คว่าเมนู
  Deploy → Container Registry โผล่ใน sidebar)

### 4. Deploy token (สำหรับ prod host pull image)

Settings → Repository → Deploy tokens → Add token:

| ช่อง | ค่า |
|---|---|
| Name | `prod-registry-pull` (label เฉย ๆ) |
| Expiration date | เว้นว่าง หรือตาม policy — หมดอายุแล้ว host จะ pull ไม่ได้ตอน deploy |
| Username | เว้นว่างได้ GitLab gen ให้ (รูปแบบ `gitlab+deploy-token-<N>`) |
| Scopes | ติ๊ก `read_registry` ตัวเดียว |

กด Create → จะโชว์ **username + token ครั้งเดียว** — copy ทั้งคู่ไปใส่ CI/CD variables ข้อ 5
(`REGISTRY_DEPLOY_USER` = username, `REGISTRY_DEPLOY_TOKEN` = token).

หมายเหตุ: deploy token ไม่มี role ให้เลือก (ต่างจาก access token ข้อ 2 — คนละชนิด คนละเมนู).
เหตุที่ใช้ deploy token บน host แทน `CI_JOB_TOKEN`: job token ตายพร้อม job — host ต้อง
re-pull image ได้ภายหลังด้วย.

### 5. CI/CD variables

Settings → CI/CD → Variables → Add variable ทีละตัว. ทุกตัวติ๊ก **Protected**;
masked ตามตาราง:

| Key | Type | Flags | ค่า |
|---|---|---|---|
| `SSH_PRIVATE_KEY` | **File** | Protected (masked ไม่ได้ — หลายบรรทัด) | เนื้อไฟล์ private key ทั้งก้อน ตั้งแต่ `-----BEGIN OPENSSH PRIVATE KEY-----` ถึง `-----END...-----` รวมบรรทัดจบ (วิธีสร้าง: ดูหัวข้อ "สร้าง SSH key" ด้านล่าง) |
| `SSH_KNOWN_HOSTS` | **File** | Protected | ผลจาก `ssh-keyscan -H <DEPLOY_HOST>` ทั้งก้อน |
| `DEPLOY_HOST` | Variable | Protected | hostname/IP ของ prod server |
| `DEPLOY_USER` | Variable | Protected | user SSH บน prod server (ต้องอยู่ group `docker`) |
| `DEPLOY_PATH` | Variable | Protected | path บน host ที่มี `.env` + `secrets/` (compose files จะถูก scp มาวางที่นี่) |
| `REGISTRY_DEPLOY_USER` | Variable | Protected | username ของ deploy token ข้อ 4 |
| `REGISTRY_DEPLOY_TOKEN` | Variable | Protected + Masked | ค่า deploy token ข้อ 4 |
| `POL_SA_PASSWORD` | Variable | Protected + Masked | (optional) ใช้เฉพาะจะรัน job `integration` บน GitLab |

Type ของสองตัวแรกต้องเป็น **File** (dropdown ตอน add) — pipeline ได้ path ไฟล์มา
ตรงกับที่ `.gitlab-ci.yml` ใช้ `cp "$SSH_PRIVATE_KEY" ~/.ssh/id_ed25519`.

### 6. Runner

Settings → CI/CD → Runners (หรือถามทีม infra):

- ต้องมี runner แบบ **docker executor** ที่ project นี้ใช้ได้
- job `package` ต้องการ `privileged = true` ใน config ของ runner (docker-in-docker) —
  ถ้า policy ห้าม privileged: สลับ job `package` เป็น kaniko แทน (แก้ `.gitlab-ci.yml`)
- egress ที่ runner ต้องออกได้: `mcr.microsoft.com`, `registry-1.docker.io`,
  `api.nuget.org`, registry ของ GitLab instance เอง, และ SSH (22) ไป `DEPLOY_HOST`

---

## ฝั่ง GitHub (repo `metrodiesign/pol-core`)

### 1. ตั้ง secret สำหรับ mirror

Settings → Secrets and variables → Actions → **New repository secret**:

- Name: `GITLAB_MIRROR_TOKEN`
- Secret: paste ค่า project access token จาก GitLab ข้อ 2

### 2. Merge PR #125

ไฟล์ mirror workflow + `.gitlab-ci.yml` + `docker-compose.registry.yml` + runbook
อยู่ใน PR #125 — merge เข้า develop ตาม flow ปกติ.

### 3. ทดสอบ mirror

push/merge อะไรเข้า `develop` หนึ่งครั้ง → GitHub Actions tab → workflow
**Mirror to GitLab** ต้องเขียว → เปิด GitLab ดู commit เดียวกันต้องโผล่ และ pipeline
เริ่มรันเอง (stage verify → dotnet → package รันได้โดยยังไม่ต้องมี deploy variables —
job `deploy-prod` โผล่เฉพาะ pipeline ของ tag `v*`).

---

## สร้าง SSH key สำหรับ deploy

ทำจากเครื่องที่ปลอดภัย (เครื่อง dev ตัวเองได้). **ห้ามใช้ key ส่วนตัวที่มีอยู่** —
gen คู่ใหม่เฉพาะ CI:

```bash
# 1. gen keypair ใหม่ ไม่มี passphrase
ssh-keygen -t ed25519 -C "gitlab-ci-deploy" -f ./gitlab_ci_deploy -N ""

# 2. สร้างค่า known_hosts (รันจากเครื่องที่ถึง prod host ได้)
ssh-keyscan -H <DEPLOY_HOST> > ./gitlab_ci_known_hosts
# เช็คว่าไฟล์ไม่ว่าง มีบรรทัด ssh-ed25519/ssh-rsa; กัน MITM ให้เทียบ fingerprint กับ
# ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub ที่รันบน host ตรง ๆ
```

- `gitlab_ci_deploy` (private) → GitLab variable `SSH_PRIVATE_KEY` (File)
- `gitlab_ci_deploy.pub` (public) → ใส่บน prod host (หัวข้อถัดไป)
- `gitlab_ci_known_hosts` → GitLab variable `SSH_KNOWN_HOSTS` (File)

ใส่ค่าใน GitLab ครบแล้ว **ลบไฟล์ทิ้งจากเครื่อง**:

```bash
rm ./gitlab_ci_deploy ./gitlab_ci_deploy.pub ./gitlab_ci_known_hosts
```

ถ้า private key เคยหลุด: ลบบรรทัดนั้นใน `authorized_keys` บน host แล้ว gen คู่ใหม่ทันที.

---

## ฝั่ง Prod host (ทำครั้งเดียว)

สมมุติ host ผ่านการ first install ตาม [deploy-self-host.md](deploy-self-host.md)
ข้อ 0-3 แล้ว (มี Docker, clone repo, `.env`, `./secrets/`, ระบบรันอยู่).

```bash
# 1. user สำหรับ CI ต้องอยู่ group docker (ใช้ user เดิมที่ deploy อยู่แล้วก็ได้)
sudo usermod -aG docker <DEPLOY_USER>

# 2. authorize public key ของ CI
#    (paste เนื้อ gitlab_ci_deploy.pub ต่อท้าย)
cat >> /home/<DEPLOY_USER>/.ssh/authorized_keys   # paste แล้ว Ctrl-D
chmod 600 /home/<DEPLOY_USER>/.ssh/authorized_keys

# 3. เช็คว่า DEPLOY_PATH พร้อม: มี .env + secrets/ ครบตาม runbook เดิม
ls -la <DEPLOY_PATH>/.env <DEPLOY_PATH>/secrets/
```

สิ่งที่ job `deploy-prod` จะทำบน host (อ้างอิง — ไม่ต้องทำเอง):
scp `docker-compose.prod.yml` + `docker-compose.registry.yml` มาวางใน `DEPLOY_PATH`,
`docker login` registry ด้วย deploy token, `docker compose pull migrate api worker`,
`up -d --no-build` (ลำดับ sql-healthy → migrate-exit-0 → api/worker บังคับด้วย
`depends_on` ใน compose อยู่แล้ว), แล้ว `curl /health/ready`.

หมายเหตุ: host **ไม่ต้อง** เข้าถึง GitHub/GitLab ทาง git อีก — image มาจาก registry;
repo ที่ clone ไว้ใช้แค่ first install / rollback แบบ manual (runbook เดิมข้อ 4-5).

---

## ทดสอบ end-to-end ครั้งแรก

1. merge PR #125 + ตั้ง `GITLAB_MIRROR_TOKEN` → push เข้า develop → mirror เขียว,
   pipeline GitLab เขียว (verify/dotnet/package), image `api`/`worker`/`migrate`
   tag short-SHA โผล่ใน Deploy → Container Registry
2. เทียบผล job `verify` + `dotnet` ฝั่ง GitLab กับ GitHub run ที่ SHA เดียวกัน — ต้องตรงกัน
3. (optional) กด play job `integration` หนึ่งครั้งเพื่อพิสูจน์ SQL service wiring
   (ต้องตั้ง `POL_SA_PASSWORD` ก่อน)
4. **backup DB ก่อน** (runbook deploy ข้อ 4.1) → tag release:
   ```bash
   git tag v0.1.0 && git push origin v0.1.0
   ```
   → mirror → pipeline ของ tag build image `:v0.1.0` → job `deploy-prod` ขึ้นสถานะ
   manual → กด play → ดู log: `migrate` exit 0, `docker compose ps` healthy,
   `/health/ready` ตอบ 200
5. rollback drill: เปิด pipeline ของ tag ก่อนหน้า → กด `deploy-prod` จากตรงนั้น →
   ระบบกลับเวอร์ชันเก่า (image เก่ายังอยู่ใน registry)

## Troubleshooting

| อาการ | สาเหตุที่พบบ่อย |
|---|---|
| mirror workflow แดง: `Invalid username or token` | `GITLAB_MIRROR_TOKEN` ผิด/หมดอายุ — สร้าง access token ใหม่แล้วอัปเดต secret |
| mirror แดง: `pre-receive hook declined` / protected | token role ไม่ใช่ Maintainer หรือ branch/tag protection ไม่อนุญาต push — เช็ค GitLab ข้อ 1-2 |
| job deploy ไม่เห็นตัวแปร (`$DEPLOY_HOST` ว่าง) | variable ติ๊ก Protected แต่ ref ไม่ protected — เช็ค Protected tags `v*` |
| `Host key verification failed` | `SSH_KNOWN_HOSTS` ไม่ตรง host — รัน `ssh-keyscan` ใหม่ |
| `Permission denied (publickey)` | public key ยังไม่อยู่ใน `authorized_keys` ของ `DEPLOY_USER` หรือ permission ไฟล์ผิด (ต้อง 600) |
| host pull image ไม่ได้: `denied` | deploy token หมดอายุ/scope ผิด — ต้อง `read_registry` |
| job `package` ตาย: `Cannot connect to the Docker daemon` | runner ไม่มี privileged — เปิด privileged หรือสลับเป็น kaniko |
