# Handoff: Offices 403 after one-based migration

> From: Codex   To: human review   Date: 2026-08-09

## Task Summary

แก้ local `GET /api/v1/offices?page=1&limit=25` ที่คืน `403` ให้ bootstrap admin ตาม `bugfix.md` F-1 ถึง F-4 และ B-1 ถึง B-7 โดยคง endpoint policy `admin` + `user.manage`. เพิ่ม HTTP regression tests และ repair เฉพาะ admin `AEDA3369-394B-466B-9888-B51454486B7D`; ไม่แก้ production authorization code, seed หรือ migration.

## Current Status

Done. Current source backend รันที่ port `5100`; target มี active Platform `platform_admin` และ effective `user.manage`; re-login แล้ว Offices request คืน `200`.

## Files Changed

- `.ai/specs/bugfix-offices-403/bugfix.md` — created — root cause, F/B IDs และ hard scope
- `.ai/specs/bugfix-offices-403/tasks.md` — created — tasks กับ Evidence blocks
- `.ai/specs/bugfix-offices-403/HANDOFF.md` — created — durable repair/verification record
- `tests/Hosts.Tests/OfficeAuthorizationEndpointTests.cs` — created — real HTTP authorization regression coverage

## Important Decisions

- คง gate `admin` + `user.manage`: business spec กำหนด master-data CRUD ใช้ permission นี้ และ seed/mapping ถูกต้องแล้ว.
- ไม่แก้ `Program.cs`, permission vocabulary, role seed หรือ migration: defect มาจาก stale pre-migration Kestrel เขียน zero-based persisted enum ลง DB ที่ migrate เป็น one-based แล้ว.
- Repair เฉพาะ target bootstrap admin: ไม่ grant role แก่ admin อื่น และไม่เพิ่ม bypass ตาม Tier.
- ไม่แก้ session เก่า: current code reject zero-based session ด้วย `401`; re-login สร้าง one-based session ใหม่.

## Constraints

- ห้ามลด authorization, ลบ permission gate หรือเพิ่ม permission ให้ทุก account.
- SQL ด้านล่างใช้เฉพาะ local development DB `VCentralPay`; production/staging ไม่อยู่ใน scope.
- Password ต้องมาจาก environment/secret manager และห้ามแสดงหรือ commit.
- Deploy รอบต่อไปต้อง migrate และ restart backend เป็นชุดเดียว.

## Tests Run

- `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --filter "FullyQualifiedName~OfficeAuthorizationEndpointTests" -m:1 /nodeReuse:false -p:UseSharedCompilation=false --nologo -v minimal` -> Passed 7/7
- `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings, 0 errors
- `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 dotnet test pol-core.slnx --no-build --filter "Category!=Integration" -m:1 /nodeReuse:false -p:UseSharedCompilation=false --nologo -v minimal` -> exit 0; Hosts 451/451, Architecture 233/233 และทุก module suite ผ่าน
- `dotnet test pol-core.slnx --filter "Category=Integration" --nologo -v minimal` โดย inject credentials จาก environment โดยไม่แสดงค่า -> Passed 144/144
- `scripts/spec-trace.sh bugfix-offices-403` -> exit 0
- `dotnet format pol-core.slnx --verify-no-changes --no-restore --include tests/Hosts.Tests/OfficeAuthorizationEndpointTests.cs` -> exit 0
- `dotnet format pol-core.slnx --verify-no-changes --no-restore` -> exit 2 จาก pre-existing whitespace violations นอก scope; ตัวอย่าง `SessionCookies.cs`, `UserSessionCookies.cs`, `SfsOpenApi.cs`, `UserSfs.cs`, `RoleSfs.cs`, `WriteGuardSealTests.cs`
- Browser หลัง re-login ที่ `http://localhost:5200/admin-settings/offices` -> request `GET /api/v1/offices?page=1&limit=25` คืน `200`; UI แสดง 8 รายการ
- `curl -sS -o /dev/null -w '%{http_code}\n' 'http://localhost:5100/api/v1/offices?page=1&limit=25'` โดยไม่มี session -> `401`; proxy port `5200` -> `401`
- DB repair รอบแรก -> `ProfileChanged=1, RoleAssigned=1`; รอบสอง -> `ProfileChanged=0, RoleAssigned=0`
- DB post-check -> target `Tier=2`, `Status=1`, `AuthorizationVersion=1`, `RoleCode=platform_admin`, `RoleScope=1`, `RoleStatus=1`, `HasEffectiveUserManage=1`; `OtherAdminRoleAssignments=0`; audits สาม action อย่างละหนึ่ง

## Current-environment Provisioning

Preflight ต้องผ่านทุกข้อก่อน commit. ส่ง password ผ่าน environment variable; ห้ามใส่ค่าใน command history หรือไฟล์. สำหรับ local container ปัจจุบัน ใช้ `read -s` แล้วรัน command ด้านล่าง, paste SQL block, เพิ่ม `GO`, กด Ctrl-D, แล้ว `unset POL_SA_PASSWORD`.

```bash
read -s 'POL_SA_PASSWORD?Local SQL sa password: '
export POL_SA_PASSWORD
docker exec -i -e SQLCMDPASSWORD pol-db /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -d VCentralPay -U sa -C -b
unset POL_SA_PASSWORD
```

```sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @TargetAdminId uniqueidentifier = 'AEDA3369-394B-466B-9888-B51454486B7D';
    DECLARE @CorrelationId nvarchar(128) = N'offices-403-repair-20260809';
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @RoleId uniqueidentifier;
    DECLARE @Tier int;
    DECLARE @Status int;
    DECLARE @TargetFound bit = 0;
    DECLARE @ProfileChanged bit = 0;
    DECLARE @RoleAssigned bit = 0;

    IF NOT EXISTS (
        SELECT 1
        FROM [dbo].[__EFMigrationsHistory]
        WHERE [MigrationId] = N'20260808161508_OneBasedPersistedEnumStorage')
        THROW 51000, 'Required one-based migration is not applied.', 1;

    IF (
        SELECT COUNT_BIG(*)
        FROM [iam].[Roles]
        WHERE [Code] = N'platform_admin'
          AND [MerchantId] IS NULL
          AND [Scope] = 1
          AND [Status] = 1) <> 1
        THROW 51001, 'Expected exactly one active Platform platform_admin role.', 1;

    SELECT @RoleId = [Id]
    FROM [iam].[Roles] WITH (UPDLOCK, HOLDLOCK)
    WHERE [Code] = N'platform_admin'
      AND [MerchantId] IS NULL
      AND [Scope] = 1
      AND [Status] = 1;

    IF NOT EXISTS (
        SELECT 1
        FROM [iam].[RolePermissions] AS rp
        INNER JOIN [iam].[Permissions] AS p
            ON p.[Key] = rp.[PermissionKey]
           AND p.[Status] = 1
        INNER JOIN [iam].[PermissionGroups] AS pg
            ON pg.[Key] = p.[GroupKey]
           AND pg.[Status] = 1
           AND pg.[Scope] = 1
        WHERE rp.[RoleId] = @RoleId
          AND rp.[PermissionKey] = N'user.manage')
        THROW 51002, 'platform_admin does not have active Platform user.manage.', 1;

    SELECT
        @TargetFound = 1,
        @Tier = [Tier],
        @Status = [Status]
    FROM [admin].[Users] WITH (UPDLOCK, HOLDLOCK)
    WHERE [Id] = @TargetAdminId;

    IF @TargetFound = 0
        THROW 51003, 'Target bootstrap admin was not found.', 1;

    IF NOT ((@Tier = 1 AND @Status = 0) OR (@Tier = 2 AND @Status = 1))
        THROW 51004, 'Target admin state does not match stale or already-repaired state.', 1;

    UPDATE [admin].[Users]
    SET [Tier] = 2,
        [Status] = 1,
        [UpdatedAt] = @Now
    WHERE [Id] = @TargetAdminId
      AND [Tier] = 1
      AND [Status] = 0;

    IF @@ROWCOUNT = 1
        SET @ProfileChanged = 1;

    IF NOT EXISTS (
        SELECT 1
        FROM [admin].[RoleAssignments] WITH (UPDLOCK, HOLDLOCK)
        WHERE [AdminUserId] = @TargetAdminId
          AND [RoleId] = @RoleId)
    BEGIN
        INSERT INTO [admin].[RoleAssignments]
            ([Id], [AdminUserId], [RoleId], [AssignedById], [AssignedAt])
        VALUES
            (NEWID(), @TargetAdminId, @RoleId, @TargetAdminId, @Now);
        SET @RoleAssigned = 1;
    END;

    IF @ProfileChanged = 1 OR @RoleAssigned = 1
    BEGIN
        UPDATE [admin].[Users]
        SET [AuthorizationVersion] = [AuthorizationVersion] + 1,
            [UpdatedAt] = @Now
        WHERE [Id] = @TargetAdminId;
    END;

    IF @ProfileChanged = 1
       AND NOT EXISTS (
           SELECT 1 FROM [admin].[UserAudits]
           WHERE [Action] = N'tier-changed'
             AND [TargetAdminId] = @TargetAdminId
             AND [CorrelationId] = @CorrelationId)
        INSERT INTO [admin].[UserAudits]
            ([Id], [Action], [ActorType], [ActorId], [TargetAdminId], [MerchantId], [TargetRoleId], [CorrelationId], [OccurredAt])
        VALUES
            (NEWID(), N'tier-changed', N'admin', @TargetAdminId, @TargetAdminId, NULL, NULL, @CorrelationId, @Now);

    IF @ProfileChanged = 1
       AND NOT EXISTS (
           SELECT 1 FROM [admin].[UserAudits]
           WHERE [Action] = N'reactivate'
             AND [TargetAdminId] = @TargetAdminId
             AND [CorrelationId] = @CorrelationId)
        INSERT INTO [admin].[UserAudits]
            ([Id], [Action], [ActorType], [ActorId], [TargetAdminId], [MerchantId], [TargetRoleId], [CorrelationId], [OccurredAt])
        VALUES
            (NEWID(), N'reactivate', N'admin', @TargetAdminId, @TargetAdminId, NULL, NULL, @CorrelationId, @Now);

    IF @RoleAssigned = 1
       AND NOT EXISTS (
           SELECT 1 FROM [admin].[UserAudits]
           WHERE [Action] = N'role-assigned'
             AND [TargetAdminId] = @TargetAdminId
             AND [TargetRoleId] = @RoleId
             AND [CorrelationId] = @CorrelationId)
        INSERT INTO [admin].[UserAudits]
            ([Id], [Action], [ActorType], [ActorId], [TargetAdminId], [MerchantId], [TargetRoleId], [CorrelationId], [OccurredAt])
        VALUES
            (NEWID(), N'role-assigned', N'admin', @TargetAdminId, @TargetAdminId, NULL, @RoleId, @CorrelationId, @Now);

    COMMIT TRANSACTION;

    SELECT @ProfileChanged AS [ProfileChanged], @RoleAssigned AS [RoleAssigned];
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
```

Expected first run: `1,1`; repeat run: `0,0`. ทุก preflight/error rollback transaction อัตโนมัติ. ไม่มี migration จึงไม่มี migration rollback. หลัง commit ไม่ควรย้อนเป็น zero-based state; รอบนี้ Codex ไม่ได้สร้าง DB snapshot สำหรับ post-commit rollback. หากต้องย้อนจริง ให้ rebuild local DB หรือใช้ snapshot ที่ผู้ดูแลมีอยู่.

## Known Issues

- Full-solution format gate ยังแดงจาก baseline whitespace files นอก scope; scoped new test file ผ่าน.
- Backend process เป็น local development process; terminal/session ที่ถือ process ต้องคงทำงาน.

## Next Recommended Agent

Human review. ไม่ต้องแก้ production codeเพิ่ม.

## Next Steps

1. Review untracked spec/test files แล้ว commit ผ่าน reviewed feature branch ตาม repo workflow.
2. หาก restart backendอีกครั้ง ให้ build current HEAD ก่อน start; user ต้อง re-login เมื่อ session เก่าถูก reject.
