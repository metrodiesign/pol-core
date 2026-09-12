using Accounts.Application;
using Accounts.Domain;
using Access.Domain;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Api.IdentityAccess;

internal static class CanonicalAccessEndpoints
{
    public static void Map(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1")
            .RequireAuthorization("admin")
            .WithTags("บัญชีและสิทธิ์");

        admin.MapGet("/accounts", ListAccounts)
            .RequirePermission(Keys.UserManage).WithMetadata(new SfsQueryParamsMarker(100))
            .WithName("ListAccounts").WithSummary("ค้นหา business accounts")
            .WithDescription("ค้น Employee, Agent และ SYSTEM account ภายในขอบเขต Admin; ไม่คืน token หรือ secret");
        admin.MapGet("/accounts/{accountId:guid}", GetAccount)
            .RequirePermission(Keys.UserManage).WithMetadata(new EtagResponseMarker("200"))
            .WithName("GetAccount").WithSummary("อ่าน business account")
            .WithDescription("คืน account และ identity linkage ที่ปิดข้อมูลอ่อนไหว พร้อม ETag authorization version");
        admin.MapPatch("/accounts/{accountId:guid}", PatchAccount)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("PatchAccount").WithSummary("แก้ไขสถานะ business account")
            .WithDescription("แก้ displayName/status เท่านั้น; ห้ามเปลี่ยน account type หรือ stable identity; ต้องส่ง If-Match และ Idempotency-Key");
        admin.MapPost("/accounts/{accountId:guid}/session-revocations", RevokeAccountSessions)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithName("RevokeAccountSessions").WithSummary("เพิกถอน sessions ของ account")
            .WithDescription("เพิกถอน BFF sessions ทุกอุปกรณ์และ bump authorization version แบบ idempotent");

        admin.MapGet("/permissions", ListPermissions)
            .RequirePermission(Keys.UserRoles).WithName("ListCanonicalPermissions")
            .WithSummary("อ่าน permission catalog")
            .WithDescription("คืน Platform และ Shared permission groups จาก IAM owner เดียว");
        admin.MapGet("/roles", ListRoles)
            .RequirePermission(Keys.UserRoles).WithMetadata(new SfsQueryParamsMarker(100))
            .WithName("ListCanonicalRoles").WithSummary("ค้นหา Platform roles")
            .WithDescription("คืน role ที่มองเห็นใน Platform scope ด้วย SFS");
        admin.MapPost("/roles", CreateRole)
            .RequireCsrf().RequirePermission(Keys.UserRoles)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithName("CreateCanonicalRole").WithSummary("สร้าง Platform role")
            .WithDescription("สร้าง role ผ่าน Iam handler เดิม ตรวจ permission side และบันทึก audit correlation");
        admin.MapGet("/roles/{roleId:guid}", GetRole)
            .RequirePermission(Keys.UserRoles).WithMetadata(new EtagResponseMarker("200"))
            .WithName("GetCanonicalRole").WithSummary("อ่าน Platform role")
            .WithDescription("อ่าน role ตาม immutable role id ภายใน Platform visibility");
        admin.MapPut("/roles/{roleId:guid}", UpdateRole)
            .RequireCsrf().RequirePermission(Keys.UserRoles)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("UpdateCanonicalRole").WithSummary("แก้ Platform role")
            .WithDescription("แก้ role ผ่าน Iam handler เดิม ตรวจ version, seed anchor และ permission side");

        admin.MapGet("/accounts/{accountId:guid}/merchant-access", ListMerchantAccess)
            .RequirePermission(Keys.UserManage).WithName("ListMerchantAccess")
            .WithSummary("รายการ Merchant access ของ account")
            .WithDescription("คืน active/revoked access, roles, branches และ payment methods โดยใช้ Account ownership");
        admin.MapPut("/accounts/{accountId:guid}/merchant-access/{merchantId:guid}", ReplaceMerchantAccess)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("ReplaceMerchantAccess").WithSummary("แทนที่ Merchant access")
            .WithDescription("บังคับ Merchant/role/branch binding และ authorization version; stale version เป็น 412 semantics");
        admin.MapDelete("/accounts/{accountId:guid}/merchant-access/{merchantId:guid}", RevokeMerchantAccess)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("204", EmitsEtag: false), new IdempotencyMutationMarker())
            .WithName("RevokeMerchantAccess").WithSummary("เพิกถอน Merchant access")
            .WithDescription("เพิกถอน access ที่เลือกโดยไม่ลบประวัติ และ bump authorization version");
        admin.MapGet("/accounts/{accountId:guid}/platform-access", GetPlatformAccess)
            .RequirePermission(Keys.UserManage).WithMetadata(new EtagResponseMarker("200"))
            .WithName("GetPlatformAccess").WithSummary("อ่าน Platform access")
            .WithDescription("คืน Platform access เฉพาะ Employee account พร้อม role ids และ version");
        admin.MapPut("/accounts/{accountId:guid}/platform-access", ReplacePlatformAccess)
            .RequireCsrf().RequirePermission(Keys.UserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithName("ReplacePlatformAccess").WithSummary("แทนที่ Platform access")
            .WithDescription("อนุญาตเฉพาะ Employee และ role scope ที่ grant ได้; stale version ไม่เขียนทับ");
    }

    private static async Task<IResult> ListAccounts(
        HttpContext http, IIdentityAccessAdminStore store, CancellationToken ct)
    {
        var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
        var query = new MutablePagedQuery
        {
            Page = parsed.Page,
            Limit = parsed.Limit,
            Filters = parsed.Filters,
            Sort = parsed.Sort,
            Search = parsed.Search,
        };
        return Results.Ok(await store.ListAccountsAsync(query, ct));
    }

    private static async Task<IResult> GetAccount(
        Guid accountId, HttpContext http, IIdentityAccessAdminStore store, CancellationToken ct)
    {
        var result = await store.GetAccountAsync(accountId, ct);
        if (result is null)
            return Results.NotFound();
        VersionEtags.Set(http, result.AuthorizationVersion);
        return Results.Ok(result);
    }

    private static async Task<IResult> PatchAccount(
        Guid accountId, AccountPatchRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork, CancellationToken ct)
    {
        var key = IdempotencyKeys.Require(http);
        (AccountAdminView Value, bool Replayed) result;
        try
        {
            result = await store.UpdateAccountIdempotentAsync(scope.Current.AdminId, key,
                new AccountAdminUpdate(accountId, body.DisplayName, body.Status, VersionEtags.Require(http)), ct);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        if (!result.Replayed)
        {
            audit.Append(Audit.For(AuditAction.AccountUpdated, scope.Current.AdminId, http.TraceIdentifier,
                DateTime.UtcNow, targetAdminId: accountId));
            await unitOfWork.SaveChangesAsync(ct);
        }
        VersionEtags.Set(http, result.Value.AuthorizationVersion);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> RevokeAccountSessions(
        Guid accountId, HttpContext http, IAdminScope scope, IIdentityAccessAdminStore store,
        IAuditWriter audit, [FromKeyedServices("admin")] IUnitOfWork unitOfWork, CancellationToken ct)
    {
        var result = await store.RevokeSessionsIdempotentAsync(scope.Current.AdminId,
            IdempotencyKeys.Require(http), accountId, ct);
        if (result)
        {
            audit.Append(Audit.For(AuditAction.AccountSessionsRevoked, scope.Current.AdminId,
                http.TraceIdentifier, DateTime.UtcNow, targetAdminId: accountId));
            await unitOfWork.SaveChangesAsync(ct);
        }
        return Results.Accepted();
    }

    private static async Task<IResult> ListPermissions(IMediator mediator, CancellationToken ct)
    {
        var catalog = await mediator.Send(new GetPermissionCatalogQuery(Scope.Platform), ct);
        return Results.Ok(new CanonicalPermissionCatalog(
            catalog.Groups.Select(x => new CanonicalPermissionGroup(x.Key, x.Name)).ToArray(),
            catalog.Permissions.Select(x => new CanonicalPermission(x.Key, x.Name, x.Resource)).ToArray()));
    }

    private static async Task<IResult> ListRoles(
        HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct)
    {
        var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
        var result = await mediator.Send(new ListRolesQuery
        {
            Context = RoleSideContextResolver.ForAdmin(scope),
            Page = parsed.Page,
            Limit = parsed.Limit,
            Filters = parsed.Filters,
            Sort = parsed.Sort,
            Search = parsed.Search,
        }, ct);
        return Results.Ok(new PagedResult<CanonicalRole>(
            result.Items.Select(ToRole).ToArray(), result.Page, result.Limit, result.Total));
    }

    private static async Task<IResult> CreateRole(
        CanonicalRoleCreateRequest body, HttpContext http, IAdminScope scope, IMediator mediator, CancellationToken ct)
    {
        _ = IdempotencyKeys.Require(http);
        var result = await mediator.Send(new CreateRoleCommand(
            RoleSideContextResolver.ForAdmin(scope), body.Code, body.Name, body.Description, body.Color,
            body.Status, body.Permissions ?? [], http.TraceIdentifier), ct);
        VersionEtags.Set(http, result.Version);
        return Results.Created($"/api/v1/roles/{result.Id:D}", ToRole(result));
    }

    private static async Task<IResult> GetRole(
        Guid roleId, HttpContext http, IAdminScope scope, IRoleStore roles, IRoleAssignmentCounter counter,
        CancellationToken ct)
    {
        var item = await ResolveRoleAsync(roleId, scope, roles, counter, ct);
        if (item is null)
            return Results.NotFound();
        VersionEtags.Set(http, item.Version);
        return Results.Ok(ToRole(item));
    }

    private static async Task<IResult> UpdateRole(
        Guid roleId, CanonicalRoleUpdateRequest body, HttpContext http, IAdminScope scope,
        IRoleStore roles, IRoleAssignmentCounter counter, IMediator mediator, CancellationToken ct)
    {
        var item = await ResolveRoleAsync(roleId, scope, roles, counter, ct);
        if (item is null)
            return Results.NotFound();
        _ = IdempotencyKeys.Require(http);
        var expected = VersionEtags.Require(http);
        if (item.Version != expected)
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        var result = await mediator.Send(new UpdateRoleCommand(
            RoleSideContextResolver.ForAdmin(scope), item.Code, body.Name, body.Description, body.Color,
            body.Status, body.Permissions ?? [], http.TraceIdentifier, expected), ct);
        VersionEtags.Set(http, result.Version);
        return Results.Ok(ToRole(result));
    }

    private static async Task<IResult> ListMerchantAccess(
        Guid accountId, IIdentityAccessQuery identities, IIdentityAccessAdminStore store, CancellationToken ct)
    {
        var values = await identities.ListMerchantAccessAsync(accountId, ct);
        var expanded = new List<MerchantAccessAdminView>();
        foreach (var value in values)
        {
            var item = await store.GetMerchantAccessAsync(accountId, value.MerchantId, ct);
            if (item is not null)
                expanded.Add(item);
        }
        return Results.Ok(expanded);
    }

    private static async Task<IResult> ReplaceMerchantAccess(
        Guid accountId, Guid merchantId, MerchantAccessReplaceRequest body, HttpContext http,
        IAdminScope scope, IIdentityAccessAdminStore store, IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork, CancellationToken ct)
    {
        (MerchantAccessAdminView Value, bool Replayed) result;
        try
        {
            result = await store.ReplaceMerchantAccessIdempotentAsync(scope.Current.AdminId,
                IdempotencyKeys.Require(http), new MerchantAccessReplace(accountId, merchantId, body.DataScope,
                    body.RoleIds ?? [], body.BranchIds ?? [], body.PaymentMethods ?? [], VersionEtags.Require(http)), ct);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        if (!result.Replayed)
        {
            audit.Append(Audit.For(AuditAction.MerchantAccessChanged, scope.Current.AdminId, http.TraceIdentifier,
                DateTime.UtcNow, merchantId: merchantId));
            await unitOfWork.SaveChangesAsync(ct);
        }
        VersionEtags.Set(http, result.Value.Version);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> RevokeMerchantAccess(
        Guid accountId, Guid merchantId, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork, CancellationToken ct)
    {
        bool result;
        try
        {
            result = await store.RevokeMerchantAccessIdempotentAsync(scope.Current.AdminId,
                IdempotencyKeys.Require(http), accountId, merchantId, VersionEtags.Require(http), ct);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        if (result)
        {
            audit.Append(Audit.For(AuditAction.MerchantAccessChanged, scope.Current.AdminId, http.TraceIdentifier,
                DateTime.UtcNow, merchantId: merchantId));
            await unitOfWork.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> GetPlatformAccess(
        Guid accountId, HttpContext http, IIdentityAccessAdminStore store, CancellationToken ct)
    {
        var result = await store.GetPlatformAccessAsync(accountId, ct);
        if (result is null)
            return Results.NotFound();
        VersionEtags.Set(http, result.Version);
        return Results.Ok(result);
    }

    private static async Task<IResult> ReplacePlatformAccess(
        Guid accountId, PlatformAccessReplaceRequest body, HttpContext http, IAdminScope scope,
        IIdentityAccessAdminStore store, IAuditWriter audit,
        [FromKeyedServices("admin")] IUnitOfWork unitOfWork, CancellationToken ct)
    {
        (PlatformAccessAdminView Value, bool Replayed) result;
        try
        {
            result = await store.ReplacePlatformAccessIdempotentAsync(scope.Current.AdminId,
                IdempotencyKeys.Require(http), new PlatformAccessReplace(accountId, body.Status, body.RoleIds ?? [],
                    VersionEtags.Require(http)), ct);
        }
        catch (ConcurrencyConflictException)
        {
            return Results.Problem(statusCode: StatusCodes.Status412PreconditionFailed,
                extensions: new Dictionary<string, object?> { ["code"] = "precondition_failed" });
        }
        if (!result.Replayed)
        {
            audit.Append(Audit.For(AuditAction.PlatformAccessChanged, scope.Current.AdminId, http.TraceIdentifier,
                DateTime.UtcNow, targetAdminId: accountId));
            await unitOfWork.SaveChangesAsync(ct);
        }
        VersionEtags.Set(http, result.Value.Version);
        return Results.Ok(result.Value);
    }

    private static async Task<RoleListItem?> ResolveRoleAsync(
        Guid roleId, IAdminScope scope, IRoleStore roles, IRoleAssignmentCounter counter, CancellationToken ct)
    {
        var result = await roles.ListAsync(RoleSideContextResolver.ForAdmin(scope),
            new MutablePagedQuery { Page = 1, Limit = int.MaxValue }, ct);
        var item = result.Items.FirstOrDefault(x => x.Id == roleId);
        return item is null ? null : item with { UserCount = await counter.CountAsync(RoleSideContextResolver.ForAdmin(scope), roleId, ct) };
    }

    private static CanonicalRole ToRole(RoleListItem role) => new(
        role.Id, role.Code, role.Name, role.Description, role.Color,
        role.Status, role.Shared, role.PermissionKeys, role.Version, role.UserCount);

    private sealed record MutablePagedQuery : PagedQuery;

}

internal sealed record AccountPatchRequest(string DisplayName, AccountStatus Status);
internal sealed record AccountSessionRevokeRequest(string? Reason);
internal sealed record MerchantAccessReplaceRequest(
    DataScope DataScope, IReadOnlyList<Guid>? RoleIds, IReadOnlyList<Guid>? BranchIds, IReadOnlyList<string>? PaymentMethods);
internal sealed record PlatformAccessReplaceRequest(PlatformAccessStatus Status, IReadOnlyList<Guid>? RoleIds);
internal sealed record CanonicalPermissionCatalog(
    IReadOnlyList<CanonicalPermissionGroup> Groups, IReadOnlyList<CanonicalPermission> Permissions);
internal sealed record CanonicalPermissionGroup(string Key, string Label);
internal sealed record CanonicalPermission(string Key, string Label, string Resource);
internal sealed record CanonicalRole(
    Guid Id, string Code, string Name, string? Description, string? Color, RoleStatus Status,
    bool Shared, IReadOnlyList<string> Permissions, long Version, int UserCount);
internal sealed record CanonicalRoleCreateRequest(
    string Code, string Name, string? Description, string? Color, RoleStatus Status, IReadOnlyList<string>? Permissions);
internal sealed record CanonicalRoleUpdateRequest(
    string Name, string? Description, string? Color, RoleStatus Status, IReadOnlyList<string>? Permissions);
