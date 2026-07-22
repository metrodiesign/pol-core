# Runbook: ตั้งค่า GitLab CI/CD channel (จับมือทำทีละขั้น)

คู่มือตั้งค่าครบทุกฝั่งสำหรับระบบ deploy ผ่าน GitLab องค์กร
(`gitlab2.viriyah.co.th/central-software/vcentralpayapi`) ตามที่วางไว้ใน PR #125.
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

### เช็คก่อนเริ่ม (prerequisites)

ต้องมีครบก่อนเริ่ม Part A ไม่งั้นจะติดกลางทาง:

- สิทธิ์ **Maintainer/Owner** บน GitLab project (เช็คจริงใน A1)
- สิทธิ์ **Admin** บน GitHub repo `metrodiesign/pol-core` (สำหรับใส่ secret ใน B1)
- UAT server พร้อมใช้แล้วตาม [deploy-self-host.md](deploy-self-host.md) ข้อ 0-3 (Docker ติดตั้ง,
  มี `.env` + `secrets/`, ระบบรันอยู่ด้วย `docker-compose.prod.yml` เดิม)
- รู้ IP/hostname ของ UAT server และมี SSH เข้าได้ด้วย user ที่มีสิทธิ์ใช้ `docker`
- เครื่อง dev ตัวเองมี `ssh-keygen` และ `ssh-keyscan` (มากับ OpenSSH — macOS/Linux มีในตัว,
  Windows ใช้ Git Bash หรือ WSL)
- คุยกับทีม infra ไว้ล่วงหน้าเรื่อง GitLab Runner (ดู Part A6) — ถ้าไม่มี runner รับ project
  จะไปต่อ Part B4 ไม่ได้

---

## Part A — GitLab: เตรียม project (ยังไม่ต้องมี server)

### A1. เช็คสิทธิ์

- เปิด `https://gitlab2.viriyah.co.th/central-software/vcentralpayapi`
- เมนูซ้าย **Manage → Members** (หรือ **Project information → Members** แล้วแต่เวอร์ชัน UI) →
  พิมพ์ username ตัวเองในช่องค้นหาบนตาราง → ดูคอลัมน์ **Max role**
- ต้องเป็น **Maintainer** หรือ **Owner** เท่านั้น — `Developer`/`Reporter` ทำ Part A2-A5 ไม่ได้
  (ปุ่ม Protect/token generation จะเป็นสีเทาหรือมองไม่เห็นเมนูเลย)
- ถ้าไม่ใช่: ขอให้ Owner ปัจจุบันเข้าไปหน้าเดียวกัน → กด **Invite members** หรือแก้ role ให้ตัวเอง
  เป็น Maintainer ก่อน แล้วค่อยกลับมาทำต่อ

### A2. Protect branch

- เมนูซ้าย: **Settings → Repository** → section **Protected branches** → กด Expand
- ช่อง Branch: พิมพ์ `develop` → Allowed to merge: `Maintainers` →
  Allowed to push and merge: `Maintainers` → กด **Protect**
- ทำซ้ำกับ `main`
- เช็คผล: ตารางด้านล่าง section ต้องมีแถว `develop` และ `main` ครบ 2 แถว คอลัมน์
  "Allowed to push and merge" / "Allowed to merge" ต้องขึ้น `Maintainers` ทั้งคู่ — ถ้ายังเห็น
  `Developers + Maintainers` แปลว่ากด dropdown ผิดตัว ให้แก้ที่แถวนั้นซ้ำ

เหตุผล: CI/CD variables ที่ติ๊ก Protected จะถูกส่งให้เฉพาะ pipeline บน ref ที่ protected —
ไม่ protect = job deploy ไม่เห็นตัวแปร. (mirror push ด้วย token role Maintainer จึงผ่านได้.)

### A3. Protect tag

- หน้าเดิม (**Settings → Repository**) เลื่อนลงหา section **Protected tags** → Expand
- ช่อง Tag: พิมพ์ `v*` → กด dropdown ใต้ช่องพิมพ์ เลือกตัวเลือกที่ขึ้นต้นด้วย wildcard
  (ระบุ "Create wildcard v*") — อย่ากด Enter เฉย ๆ เพราะจะกลายเป็น tag ชื่อ `v*` ตรงตัวแทน wildcard
- Allowed to create: `Maintainers` → กด **Protect**
- เช็คผล: ตาราง Protected tags มีแถว `v*` โผล่มา

### A4. Project access token (สำหรับ mirror)

- **Settings → Access tokens** → กด **Add new token**
- กรอกฟอร์มตามนี้:

| ช่อง | ค่า |
|---|---|
| Token name | `github-mirror` (label เฉย ๆ) |
| Expiration date | กด x ลบออกถ้าลบได้; ถ้า instance บังคับ เลือกไกลสุด (มัก 1 ปี) แล้ว**จดวันหมดอายุ** — หมดแล้ว mirror จะ fail เงียบ ๆ ฝั่ง GitHub |
| Role | `Maintainer` — ต้องระดับนี้เพราะ mirror ต้อง force-push เข้า protected branch/tag; `Developer` โดน reject |
| Scopes | ติ๊ก `write_repository` ตัวเดียว (อย่าติ๊ก `api` หรือ scope อื่นเกินจำเป็น) |

- กด **Create project access token** → หน้าจะเปลี่ยนเป็น banner สีเขียวโชว์ค่า token เต็ม
  (ขึ้นต้น `glpat-`) — โชว์**ครั้งเดียว** ปิดหน้าแล้วดูซ้ำไม่ได้ → copy ค้างไว้ก่อน (ใช้ใน Part B1)
- ห้าม paste token ลงไฟล์/chat/commit — ถ้าพลาด paste หลุดที่ไหน ให้ revoke token นั้นทันทีที่
  หน้า Access tokens (ปุ่ม **Revoke**) แล้วสร้างใหม่
- ปฏิทินเตือนวันหมดอายุ: ตั้งเตือนล่วงหน้า ~1 สัปดาห์ก่อนวันที่จดไว้ข้างบน เพื่อสร้าง token ใหม่
  ทัน ก่อน mirror จะเริ่ม fail

### A5. เช็ค Container Registry เปิดอยู่

- เมนูซ้าย: มองหา **Deploy → Container Registry** — ถ้าคลิกแล้วเห็นหน้าเปล่า ๆ พร้อมคำแนะนำ
  `docker login` = เปิดแล้ว ข้ามไป Part A6
- ถ้าไม่เห็นเมนู **Deploy → Container Registry** เลย: **Settings → General** →
  section **Visibility, project features, permissions** → กด Expand →
  หา toggle **Container Registry** → เปิด → เลื่อนลงสุดหน้ากด **Save changes**
- กลับไปเช็คเมนูซ้ายอีกรอบ ต้องเห็น **Deploy → Container Registry** โผล่มาแล้ว

### A6. Runner requirements (ประสานทีม infra ล่วงหน้า)

Job `package` ต้องรันด้วย Docker executor + `privileged = true` (Docker-in-Docker) เพื่อ build
image ได้ — ถ้า project ยังไม่มี runner ที่ตั้ง privileged ไว้ ต้องแจ้งทีม infra **ก่อน** ถึง Part
B4 ไม่งั้น pipeline จะค้าง pending หรือ job แดง:

- Executor: `docker`
- `privileged = true` สำหรับ job ที่ใช้ DinD (ทีม infra เป็นคนกำหนดที่ `config.toml` ของ runner)
- Egress ที่ runner ต้องออกได้: `mcr.microsoft.com`, `registry-1.docker.io`, `api.nuget.org`,
  registry ของ GitLab เอง (`gitlab2.viriyah.co.th`), SSH (port 22) ไป deploy host ทั้ง UAT/prod
- ถ้า policy องค์กรห้าม privileged runner: แจ้ง infra ให้เปลี่ยน job `package` เป็น build ด้วย
  kaniko แทน (ไม่ต้อง privileged) — เป็นการแก้ `.gitlab-ci.yml` เพิ่มเติม ไม่ครอบคลุมใน runbook นี้

เช็คก่อนว่าจะเลือกทางไหน: **Settings → CI/CD → Runners** แบ่ง 2 ส่วน — **Instance runners**
(shared runner ระดับองค์กร ถ้า admin ตั้งไว้ให้) กับ **Project runners** (runner เฉพาะ project นี้)

#### เคส 1 — มี Instance runner โชว์อยู่ (สถานะ online)

- เปิด toggle **"Enable instance runners for this project"** (ชื่ออาจต่างกันเล็กน้อยแล้วแต่
  เวอร์ชัน) — Maintainer ทำเองได้เลย ไม่ต้องรอ infra
- เช็คผล: retry job ที่ค้างอยู่ ต้องถูก pick up ภายในไม่กี่วินาที
- ข้อจำกัด: ถ้า instance runner ไม่ได้ตั้ง privileged ไว้ (มักปิดไว้เพราะ security) job `package`
  จะยังพังด้วย `Cannot connect to the Docker daemon` — เจอแบบนั้นข้ามไปเคส 2

#### เคส 2 — ไม่มี Instance runner เลย ต้อง register project runner เอง

**ขั้นที่ 1 — เตรียมเครื่อง**

หาเครื่อง (VM/server) ในเครือข่ายองค์กรที่: ติดตั้ง Docker แล้ว, เข้าถึง `gitlab2.viriyah.co.th`
ได้, egress ออกได้ตามรายการด้านบน (`mcr.microsoft.com`, `registry-1.docker.io`,
`api.nuget.org`, registry ของ GitLab เอง, SSH ไป UAT/prod host). ใช้เครื่องเดียวกับ UAT/prod host
ได้ถ้า resource พอ แต่แนะนำแยกเครื่อง — runner ทำงาน privileged (ขั้นที่ 5) ถ้าแชร์เครื่องกับ
workload อื่นมีความเสี่ยง container escape

**ขั้นที่ 2 — สร้าง runner บน GitLab ก่อน (เอา token)**

- **Settings → CI/CD → Runners** → กด **New project runner**
- Operating system: Linux (หรือตาม OS เครื่องจริง)
- Tags: ใส่หรือเว้นว่างก็ได้ (ถ้า `.gitlab-ci.yml` ไม่ได้ระบุ tags บังคับ ให้ติ๊ก
  **"Run untagged jobs"** ด้วย ไม่งั้น job จะไม่ถูก assign)
- กด **Create runner** → หน้าจะโชว์คำสั่ง `gitlab-runner register` พร้อม authentication token
  (ขึ้นต้น `glrt-`) — copy ไว้

**ขั้นที่ 3 — ติดตั้ง gitlab-runner บนเครื่อง (SSH เข้าไปก่อน)**

```bash
curl -L "https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh" | sudo bash
sudo apt-get install gitlab-runner
```

(เครื่องเป็น RHEL/CentOS ใช้ `.rpm.sh` แทน `.deb.sh` — เช็ค distro ด้วย `cat /etc/os-release`)

**ขั้นที่ 4 — register runner ด้วย token จากขั้นที่ 2**

```bash
sudo gitlab-runner register \
  --url "https://gitlab2.viriyah.co.th" \
  --token "glrt-เนื้อtokenจากขั้นที่2" \
  --executor "docker" \
  --docker-image "mcr.microsoft.com/dotnet/sdk:10.0"
```

รันแบบ interactive ก็ได้ (ไม่ใส่ flag แล้วตอบคำถามทีละอัน) — ค่าที่ตอบสำคัญคือ executor ต้อง
เป็น `docker`

**ขั้นที่ 5 — เปิด privileged + mount TLS cert volume (จำเป็นสำหรับ job `package` ที่ build image ด้วย DinD)**

```bash
sudo nano /etc/gitlab-runner/config.toml
```

หา section `[runners.docker]` ของ runner ที่เพิ่ง register แล้วเพิ่ม/แก้บรรทัด:

```toml
[runners.docker]
  privileged = true
  volumes = ["/certs/client", "/cache"]
```

`volumes` ต้องมี `/certs/client` ด้วย — job `package` ตั้ง `DOCKER_TLS_CERTDIR: "/certs"`
ให้ dind daemon เปิด TLS แล้วเขียน client cert ไว้ที่ `/certs/client`; ถ้า runner mount แค่
`/cache` (ค่า default ตอน register ใหม่) container ของ job จะมองไม่เห็น cert เชื่อม daemon
ไม่ได้ job จะแดงด้วย error ต่อ Docker daemon/TLS ทั้งที่ privileged แล้ว

เซฟแล้ว restart:

```bash
sudo gitlab-runner restart
```

**ขั้นที่ 6 — เช็คผล**

- **Settings → CI/CD → Runners** → runner ที่เพิ่ง register ต้องขึ้นจุด**เขียว online**
- กลับไป pipeline ที่ค้าง → กด **Retry** job ที่แดง/pending → ต้องเห็น job เริ่มรันทันที
- ถ้ายังติด `Cannot connect to the Docker daemon` ทั้งที่ privileged แล้ว เช็คก่อนว่า
  `volumes` ใน `[runners.docker]` มี `/certs/client` ครบตามขั้นที่ 5 (ลืมบ่อยสุด) — ถ้าครบแล้ว
  ค่อยไล่เรื่อง docker socket permission บนเครื่อง runner เอง ดู log เพิ่มบนเครื่องนั้นตรง ๆ

---

## Part B — GitHub: ใส่ secret + merge + ทดสอบ mirror

### B1. ตั้ง secret

- ไป `https://github.com/metrodiesign/pol-core` → **Settings → Secrets and variables → Actions**
  → แท็บ **Repository secrets** (แท็บบนสุด ข้าง Environment secrets)
- กด **New repository secret**
- Name: `GITLAB_MIRROR_TOKEN` (พิมพ์ให้ตรงเป๊ะ ตัวพิมพ์ใหญ่/underscore ต้องตรง —
  `.github/workflows/mirror-gitlab.yml` อ้างชื่อนี้ตรง ๆ)
- Secret: paste token จาก A4 → **Add secret**
- เช็คผล: กลับมาหน้า Repository secrets ต้องเห็นแถว `GITLAB_MIRROR_TOKEN` (ค่าไม่โชว์ ขึ้นแค่
  "Updated X ago" เป็นปกติ — GitHub ไม่มีทางดูค่าเดิมซ้ำ ถ้าพิมพ์ผิดต้อง **Update** ทับใหม่)
- ปิดแท็บ GitLab ของ A4 ได้แล้ว

### B2. Merge PR #125

- เปิด `https://github.com/metrodiesign/pol-core/pull/125` → รอ CI เขียวครบทุก required check
  → merge ตาม flow ปกติของ repo (ห้าม push ตรง, ห้าม force push, ห้าม skip check)

### B3. ทดสอบ mirror ครั้งแรก

- ตัว merge ใน B2 = push เข้า develop อยู่แล้ว → แท็บ **Actions** ของ GitHub repo →
  เมนูซ้ายเลือก workflow ชื่อ **Mirror to GitLab** → run ล่าสุดต้องเขียว (วงกลม checkmark
  สีเขียว ไม่ใช่ X แดงหรือวงกลมเหลืองกำลังรัน)
- คลิกเข้า run นั้น → step "Push mirror" → ขยายดู log ถ้าต้องการยืนยันว่า push ไปถึง GitLab จริง
- ถ้าแดง `Invalid username or token`: token ผิด/หมดอายุ — ทำ A4 + B1 ใหม่ (สร้าง token ใหม่
  แล้ว **Update** secret เดิม)
- ถ้าแดง `pre-receive hook declined`: role token ไม่ถึง Maintainer หรือ protected branch
  ตั้ง Allowed to push ไม่รวม Maintainers — เช็ค A2/A4
- เช็คฝั่ง GitLab: เมนูซ้าย **Code → Commits** (หรือหน้า Repository) ต้องเห็น commit ล่าสุด
  hash ตรงกับ GitHub เป๊ะ

### B4. ดู pipeline แรกบน GitLab

- GitLab เมนูซ้าย: **Build → Pipelines** → ต้องมี pipeline ของ commit นั้นกำลังรัน (สถานะ
  วงกลมหมุนสีน้ำเงิน)
- คลิกเข้า pipeline นั้นดู stage เรียงเป็นคอลัมน์ → รอจน `verify`, `dotnet`, `package` เขียว
  (ครั้งแรกช้าสุดเพราะยังไม่มี cache — 15-30 นาที ปกติ)
- job `deploy-uat` โผล่เป็นปุ่มสามเหลี่ยม (manual) ท้าย pipeline — **ยังไม่ต้องกด** ยังไม่มี server
  ตั้งค่าเสร็จ (ทำใน Part F)
- เช็ค image: **Deploy → Container Registry** → ต้องเห็น 3 repository ย่อย: `api`, `worker`,
  `migrate` แต่ละอันมี tag เป็น short SHA ของ commit
- ถ้า `package` แดง `Cannot connect to the Docker daemon`: runner ไม่มี privileged หรือ
  `volumes` ไม่มี `/certs/client` — หยุด แล้วประสานทีม infra ตาม **Part A6**
- ถ้าไม่มี runner รับ job (pending ค้างไม่ขยับ): ติดต่อทีม infra ขอ runner ให้ project
  (ดู requirement ใน Part A6)

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

อธิบาย flag: `-t ed25519` = อัลกอริทึมที่ปลอดภัยกว่า RSA และ key สั้นกว่า (ใช้ได้ถ้า server
รองรับ OpenSSH ใหม่พอ — ถ้า server เก่ามากจนไม่รองรับ ค่อยเปลี่ยนเป็น `-t rsa -b 4096`),
`-C` = comment ติดใน public key ไว้บอกว่า key นี้ใช้ทำอะไร, `-f` = ชื่อไฟล์ output,
`-N ""` = ไม่ตั้ง passphrase (จำเป็นเพราะ CI รันแบบ non-interactive ใส่ passphrase ไม่ได้)

ได้ 2 ไฟล์ในโฟลเดอร์เดียวกัน:
- `gitlab_ci_uat` — private key (ห้ามหลุด, ห้าม commit, ใช้ครั้งเดียวตอน paste เข้า E2 แล้วลบทิ้ง)
- `gitlab_ci_uat.pub` — public key (เอาไปวางที่ server ได้ ไม่ลับ)

เช็คสิทธิ์ไฟล์ private ให้แน่ใจว่าอ่านได้เฉพาะตัวเอง (ปกติ `ssh-keygen` ตั้งให้อัตโนมัติ แต่เช็คซ้ำ
กันเหนียว):

```bash
chmod 600 ./gitlab_ci_uat
ls -l ./gitlab_ci_uat   # ต้องขึ้น -rw------- (600)
```

### C2. สร้างค่า known_hosts

ต้องรู้ IP/hostname ของ UAT server ก่อน (ตัวอย่างนี้ใช้ `10.0.0.50` — แทนด้วยของจริงทุกที่ที่เห็น
ค่านี้ในไฟล์):

```bash
ssh-keyscan -H 10.0.0.50 > ./gitlab_ci_uat_known_hosts
cat ./gitlab_ci_uat_known_hosts
# ต้องมีบรรทัดขึ้นต้น |1| ตามด้วย ssh-ed25519/ssh-rsa — ห้ามว่าง
```

- ถ้าว่าง = เครื่องคุณถึง server ไม่ได้ (VPN/firewall) — รันจากเครื่องที่ถึงได้แทน (เช่น bastion
  host หรือเครื่องที่อยู่ network เดียวกับ server)
- ถ้า server เปลี่ยน SSH host key ในอนาคต (reinstall OS ฯลฯ) ต้องรัน `ssh-keyscan` ใหม่แล้ว
  อัปเดตค่าตัวแปร `SSH_KNOWN_HOSTS` ใน E2 ด้วย ไม่งั้น deploy job จะพังด้วย
  `Host key verification failed`
- กัน MITM (แนะนำทำ โดยเฉพาะรอบ prod): เทียบ fingerprint ที่ได้จาก `ssh-keygen -lf
  ./gitlab_ci_uat_known_hosts` กับ fingerprint จริงที่รันตรงบน server:
  ```bash
  ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
  ```
  ค่าที่ได้ (SHA256 hash) ต้องตรงกัน ถ้าไม่ตรง **อย่าใช้ค่านั้น** — อาจเจอ MITM หรือ scan ผิดเครื่อง

---

## Part D — UAT server (ทำครั้งเดียวต่อ host)

สมมุติ server ผ่าน first install ตาม [deploy-self-host.md](deploy-self-host.md) ข้อ 0-3 แล้ว
(มี Docker, มี directory ที่มี `.env` + `secrets/`, ระบบรันอยู่) — ถ้ายัง ทำนั่นก่อน.
ตัวอย่างใช้ user `deploy` + path `/opt/pol-core` — แทนด้วยของจริง.

### D1. SSH เข้า server ด้วย user ที่จะให้ CI ใช้

```bash
ssh deploy@10.0.0.50
```

ใช้ user เดิมที่มีอยู่แล้วก็ได้ ไม่ต้องสร้าง user ใหม่เฉพาะ CI — ขอแค่ user นี้อยู่ใน group
`docker` และมีสิทธิ์เขียนที่ `/opt/pol-core`

### D2. เช็ค/เพิ่ม group docker

```bash
groups              # ต้องเห็นคำว่า docker ในรายการ
docker ps            # ทดสอบตรง ๆ — รันได้โดยไม่ขึ้น "permission denied" = ผ่านแล้ว
# ถ้าไม่มี:
sudo usermod -aG docker $USER
exit                # ออกแล้ว ssh เข้าใหม่ให้ group ติด (group ใหม่มีผลตอน login session ใหม่เท่านั้น)
```

### D3. ใส่ public key ของ CI

```bash
mkdir -p ~/.ssh && chmod 700 ~/.ssh
echo 'PASTE_เนื้อไฟล์_gitlab_ci_uat.pub_ตรงนี้' >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
```

(เนื้อไฟล์ .pub ดูจากเครื่องตัวเอง: `cat ~/Desktop/gitlab_ci_uat.pub` — บรรทัดเดียว
ขึ้นต้น `ssh-ed25519` ลงท้ายด้วย comment `gitlab-ci-uat` — copy ทั้งบรรทัดรวม comment ท้าย
ไม่ต้องตัดออก)

เช็คผล:

```bash
cat ~/.ssh/authorized_keys   # ต้องเห็นบรรทัดที่เพิ่งเพิ่ม ไม่ซ้ำ ไม่มีบรรทัดว่างแทรกกลาง
ls -ld ~/.ssh ~/.ssh/authorized_keys   # ~/.ssh ต้อง 700, authorized_keys ต้อง 600
```

permission ผิด (เช่น `authorized_keys` เป็น 644 หรือ group-writable) = SSH daemon จะปฏิเสธ key
เงียบ ๆ แล้วขึ้น `Permission denied (publickey)` โดยไม่บอกสาเหตุตรง ๆ

### D4. เช็ค path deploy พร้อม

```bash
ls -la /opt/pol-core/.env /opt/pol-core/secrets/
docker compose -f /opt/pol-core/docker-compose.prod.yml ps   # ระบบเดิมรันอยู่
df -h /opt   # เช็คพื้นที่ดิสก์เหลือพอสำหรับ image ใหม่ (pull ซ้อนของเก่าไว้ชั่วคราว)
```

ถ้า `.env`/`secrets/` ยังไม่มี หรือ `docker compose ps` ไม่เห็น container อะไรเลย = first install
ยังไม่เสร็จ กลับไปทำ [deploy-self-host.md](deploy-self-host.md) ก่อน อย่าฝืนไปต่อ Part F

### D5. ทดสอบ key จากเครื่องตัวเอง (terminal ใหม่)

```bash
ssh -i ~/Desktop/gitlab_ci_uat deploy@10.0.0.50 'docker ps'
```

ต้องเข้าได้**ไม่ถามรหัส** + เห็นรายการ container — ผ่าน = ฝั่ง server จบ.

ถ้ายังถามรหัส/passphrase หรือขึ้น `Permission denied (publickey)`: วนกลับไปเช็ค D3 (เนื้อไฟล์
public key วางถูกไฟล์/ถูก user หรือยัง, permission ถูกไหม) ก่อนไปต่อ Part E — ไม่งั้น CI จะพัง
ด้วยอาการเดียวกันตอนกด deploy จริง

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

- กด **Create deploy token** → หน้าเปลี่ยนเป็น banner โชว์ 2 ค่า**ครั้งเดียว**: username + token
  → copy ทั้งคู่แปะไว้ที่อื่นชั่วคราวก่อน (เช่น note ที่จะลบทิ้งทันทีหลังใช้) ไว้ใช้ใน E2
  (ตัวที่ 6-7) — ปิดหน้านี้แล้วดู token ซ้ำไม่ได้ ต้อง revoke สร้างใหม่ถ้าพลาด
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

รายละเอียดตอนกรอกฟอร์ม Add variable แต่ละตัว:

- Type = dropdown "Variable / File" อยู่บนสุดของฟอร์ม — ตัวที่ 1-2 ต้องเลือก **File** เท่านั้น
  (pipeline ได้ path ไฟล์มา ตรงกับที่ `.gitlab-ci.yml` ใช้ `cp "$SSH_PRIVATE_KEY" ...`; ถ้าเผลอ
  เลือก Variable แทน File ค่าจะถูกยัดเป็น env var ตัวเดียวแทนไฟล์ ทำให้ step `cp`/`chmod`
  ใน job หา path ไม่เจอ)
- ช่อง Value เป็น textarea รองรับหลายบรรทัดอยู่แล้ว — paste ทั้งก้อนของ private key/known_hosts
  ตรง ๆ ได้เลย ไม่ต้องแก้ขึ้นบรรทัดใหม่เป็น `\n` เอง
- Environment scope: คลิกช่อง (ไม่ใช่ dropdown ธรรมดา) แล้วพิมพ์ `uat` เอง ถ้า project ยังไม่เคย
  มี environment ชื่อนี้มาก่อน ระบบจะเสนอให้สร้างใหม่จากค่าที่พิมพ์ — เลือกได้เลย
- กด **Add variable** ทีละตัว แล้วเช็คว่าตารางด้านล่างมีแถวใหม่ก่อนเริ่มตัวถัดไป — เผื่อกด submit
  ไม่ติด (เช่น scope ไม่ผ่าน validation)
- ครบ 7 ตัวแล้ว เช็ครวมทีเดียว: ตาราง Variables ต้องมี 7 แถว ทุกแถวคอลัมน์ **Environment scope**
  ขึ้น `uat` (ไม่ใช่ `All`), คอลัมน์ Protected ติ๊กครบทุกแถว, แถว `REGISTRY_DEPLOY_TOKEN`
  คอลัมน์ Masked ติ๊กด้วย
- (optional) `POL_SA_PASSWORD` — Variable, Protected + Masked, scope All — ใส่เฉพาะ
  ถ้าจะกดรัน job `integration` บน GitLab

### E3. เก็บกวาด — บนเครื่องตัวเอง

```bash
rm ~/Desktop/gitlab_ci_uat ~/Desktop/gitlab_ci_uat.pub ~/Desktop/gitlab_ci_uat_known_hosts
```

ลบทั้ง 3 ไฟล์ให้ครบ (private, public, known_hosts) — เก็บ private key ไว้บนเครื่อง dev ไม่มี
ประโยชน์อะไรอีกเพราะ paste เข้า GitLab variable แล้ว มีแต่เพิ่มความเสี่ยงหลุด

ถ้า private key เคยหลุด (เช่น commit เข้า git โดยไม่ตั้งใจ, แชร์ผ่าน chat): ลบบรรทัดนั้นใน
`authorized_keys` บน host ทันที (`nano ~/.ssh/authorized_keys` ลบบรรทัดออก) แล้ว gen คู่ใหม่ทั้งชุด
ตั้งแต่ Part C ใหม่ — ห้ามเอา key เดิมกลับมาใช้ต่อ

---

## Part F — ยิง deploy-uat จริง

### F1. เปิด pipeline

GitLab → **Build → Pipelines** → เปิด pipeline ล่าสุดของ `develop` (ที่ `package` เขียวแล้ว)

### F2. กดปุ่มเล่นที่ job `deploy-uat` แล้วเปิด log ดูสด

คลิกปุ่มสามเหลี่ยม (play) ข้าง job `deploy-uat` → คลิกชื่อ job เข้าไปดู log แบบ stream สด
(หน้าจะ auto-scroll ตามบรรทัดใหม่) ต้องเห็นตามลำดับ:

1. `apk add openssh-client` ผ่าน (ติดตั้ง SSH client ใน container ของ job)
2. `scp` ไม่มี error (ก็อปปี้ ไฟล์ compose/config ที่จำเป็นขึ้น host)
3. `docker login` → ขึ้นบรรทัด `Login Succeeded` ตรงตัว (ถ้าขึ้น `denied` ดู Troubleshooting)
4. `pull` ลาก image 3 ตัว (migrate, api, worker) — เห็น layer progress bar ของแต่ละ image
5. `up -d` → ขึ้น `Recreating`/`Creating` ตามด้วยชื่อ container `migrate`, `api`, `worker`
6. `docker compose ps` → คอลัมน์ State ของ `api`/`worker` ต้องเป็น `healthy` (หรือ `starting`
   ระหว่างรอ healthcheck รอบแรก), `migrate` ต้องเป็น `Exited (0)` (แปลว่า migration รันจบสำเร็จ
   แล้วออกเอง — ไม่ใช่ crash)
7. `curl /health/ready` ตอบ JSON `{"status":"healthy"}` → job จบด้วยสถานะเขียว

ถ้า log หยุดค้างขั้นไหนเกิน 2-3 นาทีโดยไม่ขยับ (โดยเฉพาะขั้น 4 pull หรือขั้น 6 รอ healthy):
เปิด terminal อีกอันต่อเข้า host ตรง ๆ (`ssh deploy@10.0.0.50`) รัน
`docker compose -f docker-compose.prod.yml -f docker-compose.registry.yml logs -f api` ดู error
จริงแบบสด ระหว่างรอ job ฝั่ง GitLab

### F3. เช็คของจริง

- เปิด URL ของ UAT ใช้งานดูจริงผ่าน browser (ไม่ใช่แค่เชื่อ log เขียว)
- บน server: `docker compose -f docker-compose.prod.yml -f docker-compose.registry.yml ps`
  ต้องเห็น container ทั้งหมด state ตรงกับที่ log บอกไว้ใน F2 ข้อ 6
- เทียบ image tag ที่รันอยู่จริงกับ commit ที่ deploy: `docker compose -f docker-compose.prod.yml
  -f docker-compose.registry.yml images` คอลัมน์ TAG ต้องเป็น short SHA เดียวกับ pipeline ที่กด

### F4. Rollback drill (ซ้อมเลยตอนนี้)

- เปิด pipeline ของ commit **ก่อนหน้า** บน develop → กด `deploy-uat` จากตรงนั้น →
  ระบบกลับเวอร์ชันเก่า (image เก่ายังอยู่ใน registry ไม่ต้อง build ใหม่) → ทำซ้ำ F2-F3 ยืนยันว่า
  เวอร์ชันเก่ากลับมาใช้งานได้จริง → แล้วกดของ commit ล่าสุดกลับให้ตรงกับที่ควรอยู่

วน F1-F3 กับทุก merge จน flow นิ่ง — จบส่วน UAT.

---

## Part G — เปิด prod (ทำหลัง UAT นิ่งแล้ว)

**สำคัญ — คำสั่งใน Part C/D/E ทุกบล็อกเขียนไว้ด้วยชื่อไฟล์ของรอบ UAT ตรง ๆ**
(`gitlab_ci_uat`, `gitlab_ci_uat.pub`, `gitlab_ci_uat_known_hosts`) — ตอนวนรอบ prod ต้อง
**แทนที่ทุกจุด** ที่เห็นชื่อเหล่านี้ด้วยชุด prod (`gitlab_ci_prod`, `gitlab_ci_prod.pub`,
`gitlab_ci_prod_known_hosts`) ก่อน copy-paste รัน — รวมถึงใน E2 ตอน `cat` เอาเนื้อไฟล์ไปใส่ตัวแปร
(`cat ~/Desktop/gitlab_ci_uat` ต้องเปลี่ยนเป็น `cat ~/Desktop/gitlab_ci_prod`) และใน E3 ตอน `rm`
เก็บกวาด. ถ้าลืมแทนที่แล้ว copy คำสั่งเดิมทั้งดุ้น จะได้ผลอย่างใดอย่างหนึ่ง: คำสั่งล้มเหลวเพราะไฟล์
UAT ถูกลบไปแล้วตาม E3 ของรอบก่อน, หรือแย่กว่านั้นคือดัน paste **private key ของ UAT** เข้าตัวแปร
`SSH_PRIVATE_KEY` scope `production` โดยไม่รู้ตัว (ถ้ายังไม่ได้ลบไฟล์ UAT ทิ้ง).

1. วน **Part C** ใหม่ (แทน `gitlab_ci_uat` ด้วย `gitlab_ci_prod` ทุกคำสั่ง): gen key คู่ใหม่ชื่อ
   `gitlab_ci_prod` + `ssh-keyscan` ของ prod host เก็บเป็น `gitlab_ci_prod_known_hosts`
   (ห้ามใช้ key ร่วมกับ UAT — คนละไฟล์ คนละชื่อ ป้องกันหลุดใบเดียวพังทั้งสอง environment)
2. วน **Part D** บน prod host ทั้งหมด (แทน `gitlab_ci_uat` ด้วย `gitlab_ci_prod` ทุกคำสั่งเช่นกัน —
   D1 SSH เข้า user จริงของ prod, D2 group docker, D3
   authorized_keys ด้วย public key `gitlab_ci_prod.pub`, D4 เช็ค `.env` + `secrets/` ของ prod,
   D5 ทดสอบ key ด้วย `ssh -i ~/Desktop/gitlab_ci_prod ...` จากเครื่องตัวเองก่อนไปต่อ)
3. วน **Part E** (ตาราง E2 คอลัมน์ Value ที่เขียนไว้ว่า `cat ~/Desktop/gitlab_ci_uat` /
   `cat ~/Desktop/gitlab_ci_uat_known_hosts` ต้องอ่านเป็น `gitlab_ci_prod` /
   `gitlab_ci_prod_known_hosts` แทน): deploy token ใบใหม่ชื่อ `prod-registry-pull` (E1) +
   add variables **ชื่อเดิม
   ทั้ง 7 ตัว** อีกรอบใน E2 โดย Environment scope พิมพ์ `production` แทน `uat`, ค่าทุกตัวเป็นของ
   prod host (private key ใหม่จากข้อ 1, known_hosts ใหม่, `DEPLOY_HOST`/`DEPLOY_USER`/
   `DEPLOY_PATH` ของ prod, deploy token username/token จากข้อนี้) — เช็คตาราง Variables รวมท้ายสุด
   ต้องมี 14 แถว (7 ตัวเดิม scope `uat` + 7 ตัวใหม่ scope `production`)
4. Deploy: **backup DB ก่อน** (runbook deploy ข้อ 4.1) → tag release ฝั่ง GitHub
   (rule เดิม: tag + changelog, ผ่าน UAT แล้ว):
   ```bash
   git tag v0.1.0 && git push origin v0.1.0
   ```
   → mirror workflow รันอัตโนมัติ (เช็คแท็บ Actions เหมือน B3) → pipeline ของ tag บน GitLab
   build image `:v0.1.0` (เช็ค Container Registry เหมือน B4) → กด play `deploy-prod` →
   verify แบบเดียวกับ F2-F3 (log 7 ขั้นตอน + เช็ค URL จริง + เช็ค image tag ตรง `v0.1.0`)
5. Rollback prod = กด `deploy-prod` จาก pipeline ของ tag ก่อนหน้า (เหมือน F4 แต่เลือกจาก tag
   แทน commit); DB rollback ใช้ runbook deploy ข้อ 5 เดิม
6. ข้อห้ามยืนยันซ้ำ (มาตรฐานทีมเดิม): ห้าม deploy prod ศุกร์เย็น/ก่อนวันหยุดยาว ยกเว้น hotfix
   ฉุกเฉิน, ทุก release ต้องมี rollback plan พร้อมก่อนกด, ต้องผ่าน UAT (Part F) ก่อนเสมอ

---

## Troubleshooting

| อาการ | ไปแก้ที่ |
|---|---|
| mirror workflow แดง: `Invalid username or token` | A4 + B1 — `GITLAB_MIRROR_TOKEN` ผิด/หมดอายุ สร้างใหม่ |
| mirror แดง: `pre-receive hook declined` / protected | A2/A4 — token role ไม่ใช่ Maintainer หรือ protection ไม่อนุญาต push |
| job deploy: `$DEPLOY_HOST` ว่าง / `Could not resolve hostname` | E2 — variable ไม่ติด scope (`uat`/`production`) หรือติ๊ก Protected แต่ ref ไม่ protected (A2/A3) |
| `Host key verification failed` | C2 — known_hosts ไม่ตรง host รัน `ssh-keyscan` ใหม่แล้วอัปเดต variable |
| `Permission denied (publickey)` | D3 — public key ไม่อยู่ใน `authorized_keys` หรือ permission ผิด (ต้อง 600) หรือ paste key ไม่ครบบรรทัด |
| `docker login` บน host → `denied` | E1 — deploy token หมดอายุ/scope ผิด (ต้อง `read_registry`) หรือ copy ผิดค่า (สลับ username กับ token) |
| บน server: `permission denied ... docker.sock` | D2 — user ไม่อยู่ group docker หรือยังไม่ได้ ssh เข้าใหม่หลัง `usermod` |
| job `package`: `Cannot connect to the Docker daemon` | A6 — runner ไม่มี privileged (เปิด privileged หรือสลับเป็น kaniko) หรือ `volumes` ขาด `/certs/client` |
| pipeline pending ค้าง ไม่มี job รัน | A6 — ไม่มี runner รับ project ติดต่อทีม infra |
| job `deploy-uat`/`deploy-prod` ค้างที่ `pull`/`up -d` นานผิดปกติ | F2 — ต่อ SSH เข้า host ตรงดู `docker compose logs -f` แบบสด เช็ค disk เต็ม (D4) หรือ image ใหญ่ผิดปกติ |
| `docker compose ps` เห็น `api`/`worker` state `unhealthy` ค้าง | เข้า host ดู `docker compose logs api` หา exception จริง ก่อนจะ rollback (F4) |
| deploy สำเร็จแต่ `curl /health/ready` ไม่ตอบ | เช็คว่า container `migrate` จบด้วย `Exited (0)` จริง (ไม่ใช่ exit code อื่น) — migration ค้างจะบล็อก readiness |
