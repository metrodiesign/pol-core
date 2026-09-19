# Bugfix: ลด warning local development
> Status: approved 2026-09-19

## Current Behavior (Defect)

เมื่อรัน `dotnet watch --project src/Api/Api.csproj run` บน macOS ระบบใช้ค่า `IntermediateOutputPath` ที่มี `\\` ปนอยู่ (`obj\\Debug/net10.0/`) ทำให้ `dotnet watch` หา `staticwebassets.development.json` ด้วย path ที่ไม่ตรงกับ filesystem และแสดง `Failed to read ... staticwebassets.development.json` แม้ build สำเร็จ

เมื่อ `WebhookDeliveryDispatcher` ใช้ `FromSqlRaw(...).FirstOrDefaultAsync(...)` และเมื่อ `IdentityAccessStore` ใช้ `SqlQueryRaw(...).FirstOrDefaultAsync(...)` ระบบให้ EF Core แปล terminal operator เป็น query ทั้งที่ raw SQL มี `TOP`/เงื่อนไขอยู่แล้ว และแสดง `Microsoft.EntityFrameworkCore.Query` warning `EventId 10103`.

## Expected Behavior

- F-1 เมื่อรัน project บน Unix ระบบต้องส่ง `IntermediateOutputPath` ที่ใช้ `/` ให้ `dotnet watch` เพื่อให้ path ของ static-web-assets manifest ตรงกับ filesystem และไม่แสดง warning จากการอ่าน path ผิด
- F-2 เมื่อ claim `WebhookDelivery` หรือ ตรวจ ownership ของ branch ผ่าน raw SQL ระบบต้อง materialize ผลลัพธ์ก่อนเลือกแถวใน memory เพื่อไม่ให้ EF Core สร้าง `First`/`FirstOrDefault` query warning โดยยังคง filter และลำดับการเลือกแถวเดิม

## Unchanged Behavior

- B-1 เมื่อ build บน Windows ระบบต้องคง output-path convention เดิมของ .NET SDK
- B-2 เมื่อ `WebhookDeliveryDispatcher` claim งาน ระบบต้องคงลำดับ `NextAttemptAt`, `CreatedAt`, `Id` และ row-lock semantics เดิม
- B-3 เมื่อแก้ merchant access ระบบต้องคงการตรวจว่า branch เป็นของ `replace.MerchantId` ก่อนบันทึกข้อมูล
- B-4 ระบบต้องคง local API origin `https://localhost:5001` และไม่เปลี่ยน launch profile
- B-5 ระบบต้องคง behavior ของ query อื่นที่ใช้ `FirstOrDefaultAsync` พร้อม filter หรือ `OrderBy` อยู่แล้ว
