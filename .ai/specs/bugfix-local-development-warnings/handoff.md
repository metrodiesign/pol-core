# Handoff: ลด warning local development
> From: Pi   To: any   Date: 2026-09-19

## Task Summary
แก้ warning ระหว่างรัน `dotnet watch` ตาม spec `bugfix-local-development-warnings` โดยครอบคลุม F1/F2 และพฤติกรรมเดิม B1-B5

## Current Status
เสร็จแล้ว: ปรับ Unix `IntermediateOutputPath` และแก้ raw SQL terminal query ที่ทำให้ EF Core แสดง `EventId 10103` แล้ว งานใน `tasks.md` เป็น `[x]` พร้อม Evidence

## Files Changed
- `Directory.Build.targets` — เพิ่มการ normalize `IntermediateOutputPath` เป็น `/` บน Unix (untracked)
- `src/Infrastructure/Persistence/Persistence.ControlPlane/Notifications/WebhookDeliveryDispatcher.cs` — materialize raw SQL ก่อนเลือกแถว
- `src/Infrastructure/Persistence/Persistence.ControlPlane/IdentityAccess/IdentityAccessStore.cs` — materialize raw SQL ก่อนตรวจ branch owner
- `.ai/specs/bugfix-local-development-warnings/bugfix.md` — บันทึก defect, expected และ unchanged behavior (untracked)
- `.ai/specs/bugfix-local-development-warnings/tasks.md` — งานและ Evidence (untracked)
- `.ai/specs/bugfix-local-development-warnings/handoff.md` — สถานะส่งต่องาน (untracked)

## Important Decisions
- บน Unix แก้เฉพาะ `IntermediateOutputPath`; Windows คงค่า SDK เดิม
- คง `TOP`, `WHERE`, `ORDER BY`, row-lock และ merchant ownership check เดิม แล้วเลือกผลหลัง `ToListAsync`
- ไม่เปลี่ยน local API origin `https://localhost:5001`

## Constraints
- ห้ามเปลี่ยน contract ของ port `5001` หรือ filter/lock semantics ของ raw SQL
- ห้ามพิมพ์ค่า secret จาก `.env`
- ยังไม่มีการ commit หรือ push

## Tests Run
- `dotnet build -warnaserror` -> Build succeeded, `0 Warning(s)`, `0 Error(s)`
- `dotnet test tests/UnitTests/UnitTests.csproj --no-build` -> Passed 963, Failed 0
- `dotnet test tests/ArchitectureTests/ArchitectureTests.csproj --no-build` -> Passed 315, Failed 0
- `dotnet watch --project src/Api/Api.csproj --launch-profile https --list` -> exit 0, ไม่พบ static-web-assets warning
- `dotnet watch` runtime smoke โดยโหลด `.env` เข้า process โดยไม่พิมพ์ค่า -> `target_warning_lines=0`
- `scripts/spec-trace.sh bugfix-local-development-warnings` -> ข้ามตามที่เป็น bugfix spec
- `.ai/bin/gate-task.sh` ด้วย build + UnitTests + ArchitectureTests -> `verdict: allow`

## Known Issues
- `dotnet test pol-core.slnx --filter "Category!=Integration" --no-restore` ไม่จบภายใน 240 วินาที เพราะ `IntegrationTests` มี process ค้าง; test project ที่เกี่ยวข้องโดยตรงผ่านครบแล้ว

## Next Recommended Agent
ให้มนุษย์ review diff ก่อน commit

## Next Steps
1. ตรวจ `git diff` และไฟล์ untracked ใน `.ai/specs/bugfix-local-development-warnings/`
2. restart `dotnet watch --project src/Api/Api.csproj run` แล้วตรวจ log จริงอีกครั้ง
3. หากผ่าน ให้ commit ตาม workflow ของ repo โดยไม่ push ตรงไป `main`/`develop`
