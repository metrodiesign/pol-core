# งานแก้ warning local development
> Status: approved 2026-09-19

- [x] 1. ทำให้ `IntermediateOutputPath` ใช้ path separator แบบ Unix บน macOS/Linux
  Satisfies: F1, B1, B4. Verify: ตรวจค่า MSBuild property และรัน `dotnet watch` smoke check โดยไม่ให้เกิด static-web-assets path warning.

  Evidence:

  - test: `dotnet msbuild src/Api/Api.csproj -t:ResolveStaticWebAssetsConfiguration -getProperty:IntermediateOutputPath -getProperty:StaticWebAssetDevelopmentManifestPath` -> `IntermediateOutputPath=obj/Debug/net10.0/` และ manifest path ใช้ `/` ทั้งหมด
  - test: `dotnet watch --project src/Api/Api.csproj --launch-profile https --list` -> exit `0`; ไม่พบ `staticwebassets` หรือ `Failed to read`
  - test: `dotnet watch` runtime smoke โดยโหลด `.env` เข้า process โดยไม่พิมพ์ค่า -> `target_warning_lines=0`
  - viewports: n/a — logic-only
  - deviations: ไม่มี

- [x] 2. แก้ raw SQL terminal query ที่ทำให้ EF Core แสดง `EventId 10103`
  Satisfies: F2, B2, B3, B5. Verify: build/test และตรวจ runtime log ของ API หลัง background dispatcher ทำงาน.

  Evidence:

  - test: `dotnet build -warnaserror` -> Build succeeded, `0 Warning(s)`, `0 Error(s)`
  - test: `dotnet test tests/UnitTests/UnitTests.csproj --no-build` -> Passed `963`, Failed `0`
  - test: `dotnet test tests/ArchitectureTests/ArchitectureTests.csproj --no-build` -> Passed `315`, Failed `0`
  - test: `dotnet watch` runtime smoke โดยโหลด `.env` เข้า process โดยไม่พิมพ์ค่า -> `captured_lines=25`, `target_warning_lines=0` สำหรับ `10103`, `FirstWithoutOrderByAndFilterWarning`, `staticwebassets` และ `Failed to read`
  - viewports: n/a — logic-only
  - deviations: `dotnet test pol-core.slnx --filter "Category!=Integration" --no-restore` ไม่จบภายใน 240 วินาที เพราะ `IntegrationTests` มี process ค้าง; จึงรัน `UnitTests` และ `ArchitectureTests` แยกและผ่านครบ
