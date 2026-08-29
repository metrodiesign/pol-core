# Bugfix: sim guardrails — entrypoint ทับ config เงียบ, invariant ไม่ครอบสาย sim, integration check skip ได้แบบนับว่าผ่าน
> Status: approved 2026-08-04
> Superseded (บางส่วน): ประโยคใน D1 ที่ว่า `build_conn` hardcode principal `pol_app` สำหรับสาย sim
> ถูกแทนที่โดย spec `sim-db-separate-logins` (2026-08-05) — `docker/entrypoint.sh` เรียก `build_conn`
> ด้วย `hippo_app`/`mammoth_app` พร้อม password จาก file secret คนละไฟล์แล้ว ส่วน `Database=hippodb`
> ที่ hardcode และข้อสรุปของ D1 (เส้น cutover ปิดอยู่) ไม่เปลี่ยน — spec นี้ปิดแล้ว ไม่แก้ย้อนหลัง

สาม defect ที่ต่อกันเป็นสายเดียว: จุดที่ระบบเลือกแหล่งข้อมูลผิดโดยไม่ส่งเสียง, ด่านที่ควรจับเรื่องนั้นแต่ยิงใส่ที่ว่าง, และ gate ที่หายไปได้โดยรายงานว่าผ่าน ทั้งสามมาจาก `external-sim-separate-containers` (PR #177, `1d560d9`) และถูกบันทึกไว้ที่ `retrospectives/2026-08/04/15.16_sim-db-split.md:70-72`

## Current Behavior (Defect)

### D1 — entrypoint ทับ SpDocument ที่ operator ตั้งไว้ เงียบ ๆ

`docker/entrypoint.sh:38-45` ตัดสินใจจากตัวแปรผิดตัว: เงื่อนไข `if` ถามว่า input ของตัวเองมีค่าหรือไม่ (`HIPPO_DB_SERVER`) แทนที่จะถามว่า output ที่กำลังจะเขียนถูกจองไว้แล้วหรือยัง (`SpDocument__MotorConnectionString`) `export` จึงเป็นการเขียนทับแบบไม่มีเงื่อนไขบน key ที่เป็น config contract ของแอป

ผลคือค่าที่ operator ตั้งมาเพื่อชี้ upstream จริงหายไปทั้งเส้น แอปต่อ sim ได้สำเร็จ `exit=0` และ stderr ว่าง — ไม่มีสัญญาณใดบอกว่าเกิดการทับ

เอกสารสี่จุดสัญญาสิ่งที่โค้ดทำไม่ได้ และเป็นเอกสารที่จะพา operator เดินเข้าจุดนี้พอดี

| ไฟล์:บรรทัด | ข้อความที่ผิดจากความจริง |
|---|---|
| `src/Modules/Products/Products.Infrastructure/Sp/SpDocumentOptions.cs:12-13` | cutover = config override ของสองค่านี้ และไม่มีอย่างอื่น |
| `src/Hosts/Api/Program.cs:155-156` | ชี้ไป motordb/centerdb จริงคือ config override ไม่ใช่การแก้โค้ด |
| `docs/reference/products.md:139-141` | เชื่อม motordb/centerdb ของจริงเหลือแค่เปลี่ยนสองค่าทาง config |
| `docker/entrypoint.sh:31-37` | comment อธิบายเฉพาะกรณี unset ไม่พูดถึงกรณี pre-set |

ความจริงคือเส้น cutover ปิดอยู่สี่ชั้น: `docker-compose.prod.yml` ไม่มี `env_file:` และไม่มี key `SpDocument__*` ใน `environment:` ของ service `api` (ค่าจึงเข้า container ไม่ได้เลย), `:76,78` บังคับ `HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER` ด้วย `:?`, `docker/migrate-entrypoint.sh:12-13,73-83` บังคับ bootstrap sim ทั้งที่ `api` มี `depends_on: service_completed_successfully`, และ `build_conn` hardcode `Database=hippodb` กับ principal `pol_app`

D1 จึงยังไม่เคยกัดใครบน prod — มันคือกับดักที่รออยู่ตรงวินาทีที่ operator ทำตามเอกสาร

### D2 — invariant trust flag ยิงใส่ที่ว่าง

`docker/entrypoint.test.sh:119-124` เขียน invariant แบบไล่ตามชื่อตัวแปร ไม่ใช่ไล่ตามช่องทาง output ที่มีจริง ตัวแปรทั้งสามที่ถูกครอบ (`$out_fallback:71`, `$out_strict:77`, `$out_empty_ca:86`) รันโดยไม่ตั้ง `HIPPO_DB_SERVER`/`MAMMOTH_DB_SERVER` บรรทัดที่สองและสามของ stub output จึงว่างเปล่าเสมอ

`$out_hippo:108` และ `$out_mammoth:116` เป็นสองตัวเดียวที่มีเนื้อ `SpDocument__*` จริง และไม่ถูก invariant แตะเลย ส่วน assertion ที่มีของสองตัวนั้น (`:109`, `:117`) ตรวจแค่ prefix `Server=...;Database=...` ไม่แตะ TLS clause ท้ายสตริง

มี gap ข้างเคียงที่ยังไม่มีใครบันทึก: ไม่มี case ไหนรัน `HIPPO_DB_SERVER` คู่กับ `DB_CA_CERTIFICATE_FILE` สาย sim จึงไม่เคยถูกทดสอบใน branch `Encrypt=Strict` แม้แต่ครั้งเดียว

ปัญหาที่อยู่ใต้ D2: `docker/entrypoint.test.sh` **ไม่มีผู้เรียกในทั้ง repo** — job `verify` รันเฉพาะ `.claude/hooks/tests/*.test.sh` (`.github/workflows/ci.yml:31`) suite นี้เป็นแค่ manual step ของ task gate และ harness ปัจจุบันยัง assert exit code หรือ stderr ไม่ได้ (`run_entrypoint` ถูกเรียกในรูป `out=$(...)` จับเฉพาะ stdout และไม่มี `check_eq`)

### D3 — required check ที่หายไปได้โดยนับว่าผ่าน

GitHub นับ `skipped` เป็นสถานะที่ผ่าน (`success`, `skipped`, `neutral`) `dotnet-integration` จึงหลุดได้สองทางโดยไม่มีด่านไหนดัก

- `.github/workflows/ci.yml:141` แปลง "ไม่มี credential" เป็น boolean แทน error
- `.github/workflows/ci.yml:145` — `if:` ไม่มี `always()` ⇒ `integration-gate` ล้มเองด้วยเหตุใดก็ตาม ก็จบที่ skipped เหมือนกัน และ `integration gate (SQL secret present?)` ไม่อยู่ใน required contexts

หลักฐานในรีโป run `27911696393`: job `dotnet integration (live SQL 2025)` conclusion `skipped` แต่ check run ถูกสร้างด้วยชื่อตรง required context เป๊ะ และ run รวมเขียว

สิ่งที่หายไปเมื่อ job นี้ skip ไม่ใช่แค่ cross-instance invariant: `tests/Integration.Tests/SimCrossInstanceConsistencyTests.cs:20-60`, bootstrap self-check, migration lineage gate, assert-fresh-db และ 103 test ใต้ `tests/Integration.Tests/` ที่ SQLite in-memory จับไม่ได้ และ invariant DocumentNo เคยถูกบังคับที่ชั้น SQL bootstrap ทุก environment รวม prod ก่อนแยก instance — วันนี้ย้ายมาอยู่บนด่านที่ skip ได้เงียบ ๆ ทั้งก้อน

### Repro (รันได้จริง)

D1 — ใช้ harness เดียวกับ `docker/entrypoint.test.sh` (`run_entrypoint` ตั้ง required vars และ stub `dotnet` ให้แล้วที่ `:20-44`)

```sh
out=$(run_entrypoint HIPPO_DB_SERVER=hippo.internal \
  SpDocument__MotorConnectionString='Server=motordb.real,1433;Database=motordb;User Id=up;Password=x;Encrypt=True;TrustServerCertificate=False')
# actual:   MOTOR=Server=hippo.internal,1433;Database=hippodb;...  rc=0  stderr ว่าง
# expected: rc ไม่ใช่ 0 และ stderr ระบุชื่อ env var ทั้งสองตัว
```

D2 — mutation ที่ต้องแดงแต่ไม่แดง

```sh
sh docker/entrypoint.test.sh                       # baseline: pass=21 fail=0
# แก้ docker/entrypoint.sh:40 ให้ประกอบสตริงเองโดยไม่เรียก build_conn
# และใส่ TrustServerCertificate=True ลงไปตรง ๆ
sh docker/entrypoint.test.sh                       # actual: pass=21 fail=0 (ไม่ขยับ)
```

D3 — สถานะ check ของ commit ที่ job ถูก skip

```sh
gh api repos/:owner/:repo/commits/dc423ad9/check-runs \
  --jq '.check_runs[] | select(.name | startswith("dotnet integration")) | .conclusion'
# actual: skipped   (GitHub นับเป็นผ่าน merge ไม่ถูกบล็อก)
```

## Expected Behavior

- F-1 WHEN ตั้งทั้ง `SpDocument__MotorConnectionString` และ `HIPPO_DB_SERVER` เป็นค่าที่ไม่ว่าง THE SYSTEM SHALL เขียนข้อความที่ระบุชื่อ env var ทั้งสองตัวลง stderr แล้วออกด้วย exit code ที่ไม่ใช่ 0 โดยไม่ `exec` แอป
- F-2 WHEN ตั้งทั้ง `SpDocument__NonMotorConnectionString` และ `MAMMOTH_DB_SERVER` เป็นค่า ที่ไม่ว่าง THE SYSTEM SHALL ทำแบบเดียวกับ F1 สำหรับคู่ของตัวเอง
- F-3 WHILE ตั้ง `SpDocument__MotorConnectionString` โดยไม่ตั้ง `HIPPO_DB_SERVER` และยังตั้ง `MAMMOTH_DB_SERVER` อยู่ THE SYSTEM SHALL คงค่า Motor ที่ operator ตั้งไว้ ประกอบ NonMotor จาก sim ตามเดิม และออกด้วย exit code 0 — hybrid cutover ทีละฝั่งต้องทำได้
- F-4 IF ค่าใดในสองคู่เป็น empty string THEN THE SYSTEM SHALL ถือว่าไม่ได้ตั้ง และทำงานเหมือน กรณี unset ตามบรรทัดฐานเดิมของไฟล์ที่ `docker/entrypoint.sh:20`
- F-5 IF ข้อความ guard ถูกพิมพ์ลง stderr THEN THE SYSTEM SHALL NOT แสดง connection string, password หรือค่าของตัวแปรใด ๆ — ระบุได้เฉพาะชื่อตัวแปร
     ค่าของตัวแปรใด ๆ — ระบุได้เฉพาะชื่อตัวแปร
- F-6 WHEN อ่านเอกสารสี่จุดที่ระบุใน D1 THE SYSTEM SHALL อธิบายเส้น cutover ตามความจริง ครบทั้งสี่ชั้น (plumbing ใน compose, `:?` ที่ `docker-compose.prod.yml:76,78`, sim ที่ `migrate-entrypoint.sh` บังคับ, catalog/principal ที่ `build_conn` hardcode) แทนข้อความ "override สองค่า" ที่ทำไม่ได้จริง
- F-7 WHEN สตริง sim ตัวใดถูกแก้ให้มี `TrustServerCertificate=True` THE SYSTEM SHALL ทำให้ `docker/entrypoint.test.sh` แดง — invariant ต้องครอบ `$out_hippo` และ `$out_mammoth`
- F-8 WHEN ตั้ง `DB_CA_CERTIFICATE_FILE` พร้อม `HIPPO_DB_SERVER` หรือ `MAMMOTH_DB_SERVER` THE SYSTEM SHALL ประกอบสตริง sim ด้วย `Encrypt=Strict` ครบชุดเหมือนสาย App และมี assertion ครอบ branch นั้น
- F-9 WHEN test ต้องตรวจ exit code หรือ stderr THE SYSTEM SHALL มี helper ให้ใช้ใน `docker/entrypoint.test.sh` ตามต้นแบบที่ `docker/migrate-entrypoint.test.sh:120,130-132`
- F-10 WHEN `docker/entrypoint.test.sh` หรือ `docker/migrate-entrypoint.test.sh` fail THE SYSTEM SHALL ทำให้ CI job `verify` แดง
- F-11 IF secret `MSSQL_SA_PASSWORD` ไม่ถูกตั้งค่า THEN THE SYSTEM SHALL รายงาน job `dotnet integration (live SQL 2025)` เป็น `failure` ไม่ใช่ `skipped` และบล็อก merge เข้า develop กับ main
     SHALL รายงานเป็น `failure` ไม่ใช่ `skipped` และบล็อก merge เข้า develop กับ main
- F-12 IF job อื่นที่ `dotnet-integration` พึ่งพาล้มเหลว THEN THE SYSTEM SHALL ไม่ปล่อยให้ check ตัวนั้นถูกรายงานว่าผ่าน — ทำได้ด้วยการไม่มี `needs:` และ `if:` บน job นั้นเลย

## Unchanged Behavior

- B-1 WHEN ตั้ง `HIPPO_DB_SERVER` โดยไม่ตั้ง `SpDocument__MotorConnectionString` THE SYSTEM SHALL CONTINUE TO ประกอบ `Server=<host>,<port>;Database=hippodb` ผ่าน `build_conn` ตามเดิม (`docker/entrypoint.test.sh:108-109`)
- B-2 WHEN ตั้ง `MAMMOTH_DB_PORT` เป็นค่า custom THE SYSTEM SHALL CONTINUE TO ใช้ port นั้น ในสตริง และใช้ค่า default 1433 เมื่อไม่ได้ตั้ง (`:116-117`)
- B-3 WHEN ไม่ตั้งทั้ง `HIPPO_DB_SERVER` และ `MAMMOTH_DB_SERVER` THE SYSTEM SHALL CONTINUE TO ปล่อย `SpDocument__*` ว่างและ boot สำเร็จ แล้วตอบ 503 ที่ชั้น gateway ตาม REQ-5.7 (`:111-114`, `SpDocumentGateway.cs:38-48`)
- B-4 WHEN ตั้ง `DB_CA_CERTIFICATE_FILE` THE SYSTEM SHALL CONTINUE TO ใช้ `Encrypt=Strict` พร้อม `ServerCertificate=` และ `HostNameInCertificate=` สำหรับสาย App (`:77-83`)
- B-5 WHEN `DB_CA_CERTIFICATE_FILE` เป็น empty string THE SYSTEM SHALL CONTINUE TO ตกไป fallback branch (`:86-87`)
- B-6 WHEN password มี backslash escape THE SYSTEM SHALL CONTINUE TO เก็บค่าแบบ byte-for-byte และไม่ตัด TLS clause ทิ้ง (`:96-102`)
- B-7 WHEN guard ถูกเพิ่มเข้าไป THE SYSTEM SHALL CONTINUE TO ประกอบ `ConnectionStrings__App` ที่ `docker/entrypoint.sh:29` โดยไม่ถูกกระทบ
- B-8 WHEN รัน `docker compose -f docker-compose.prod.yml config` ด้วยชุด placeholder ของ CI THE SYSTEM SHALL CONTINUE TO render สำเร็จ (`.github/workflows/ci.yml:115-128`, `.gitlab-ci.yml:191-193`)
- B-9 WHEN container `migrate` รัน THE SYSTEM SHALL CONTINUE TO bootstrap และ seed `hippodb`/`mammothdb` แล้วจบด้วย exit 0 ก่อน `api` ขึ้น (`migrate-entrypoint.sh:73-83`, `docker-compose.prod.yml:127-129`)
- B-10 WHEN dev รัน local ด้วย `.env` ที่ตั้ง `SpDocument__*` ตรง THE SYSTEM SHALL CONTINUE TO ใช้ค่านั้น — `docker/entrypoint.sh` ไม่อยู่ในเส้นทางนี้ (`docker-compose.yml` ไม่มี service `api`)
- B-11 WHEN job `dotnet-integration` รัน THE SYSTEM SHALL CONTINUE TO รักษาลำดับเดิม: generate `POL_APP_PASSWORD` → bootstrap สามไฟล์ → `ef database update` → lineage gate → assert-fresh-db → test (`.github/workflows/ci.yml:210-267`)
- B-12 WHEN แก้ workflow THE SYSTEM SHALL CONTINUE TO ใช้ชื่อ `name:` เดิมของทุก job ที่อยู่ใน required contexts — เปลี่ยนชื่อโดยไม่แก้ branch protection ทำให้ PR ค้าง Expected ถาวร
- B-13 WHEN push เข้า develop THE SYSTEM SHALL CONTINUE TO รัน `verify`, `dotnet`, `docker-build` ด้วยพฤติกรรมเดิม
- B-14 WHEN `docker/migrate-entrypoint.test.sh` ถูกเรียกจาก CI THE SYSTEM SHALL CONTINUE TO ผ่านทุก case ที่มีอยู่เดิมโดยไม่ต้องแก้ตัว test

## Scope constraints

ไม่มี do-not-modify list — user ยืนยันว่าแก้ได้ทุกไฟล์ที่จำเป็น รายการด้านล่างคือสิ่งที่จงใจ
ไม่ทำในรอบนี้ ไม่ใช่ข้อห้ามถาวร

- **เปิด plumbing ให้ cutover**: ไม่เพิ่ม key `SpDocument__*` เข้า `docker-compose.prod.yml`
  และไม่คลาย `:?` — สร้าง config path ที่ยังไม่มี consumer และยังติดชั้น migrate อยู่ดี
  ไปทำวัน cutover จริงที่ต้อง verify ครบสี่ชั้นพร้อมกัน
- **`.gitlab-ci.yml`**: mirror workflow ล้มด้วย HTTP 403 มา 52 run ติดไม่เคยสำเร็จ และ
  `:11-14` ไม่มี MR pipeline ⇒ บล็อก merge ไม่ได้เชิงโครงสร้าง แตะวันนี้ไม่ได้ผลใด
- **`POL_DESIGN_SQL` ที่ `migrate-entrypoint.sh:89-93`**: ทับค่า pre-set แบบเดียวกัน แต่เป็น
  input ของ `dotnet ef` ไม่ใช่ config contract ที่เอกสารสัญญาว่า override ได้ และผู้เรียกทุกราย
  ตั้งเองแล้วเรียก `dotnet ef` ตรง ไม่ผ่านสคริปต์นี้
