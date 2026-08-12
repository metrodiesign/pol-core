using System.Text.Json.Serialization;
using Admins.Application;
using Api.Admins;
using Api.Iam;
using Api.Merchants;
using BuildingBlocks.Application;
using Iam.Application.Permissions;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Mediator;
using Merchants.Application.Users;
using Merchants.Application.AdminControlPlane;
using Merchants.Domain.Users;
using Microsoft.Extensions.Options;

namespace Api.ControlPlane;

internal static class AdminMerchantIdentityEndpoints
{
    public static void MapAdminMerchantIdentityEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup(string.Empty).AddEndpointFilter(HandleKnownErrors);
        MapUsers(routes);
        MapRoles(routes);
    }

    private static void MapUsers(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit", async (
            Guid merchantId, Guid merchantUserId, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IActorScope actorScope,
            IMediator mediator, CancellationToken ct) =>
        {
            RequireReadAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            using var binding = actorScope.Begin(merchantId, scope.Current.AdminId);
            var result = await mediator.Send(new GetMerchantUserEditQuery(
                merchantUserId, merchantId, scope.Current.AdminId, http.TraceIdentifier), ct);
            if (result is null)
                return Results.NotFound();
            VersionEtags.Set(http, result.Version);
            return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("GetMerchantUserEditAdmin")
            .WithSummary("อ่านข้อมูลผู้ใช้ร้านค้าสำหรับแก้ไขโดย Admin")
            .WithDescription("คืนเฉพาะ editable profile ของผู้ใช้ใน merchant ที่ Active และอยู่ใน Admin scope พร้อม ETag หากไม่พบ -> 404")
            .Produces<MerchantUserEditView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/merchants/{merchantId:guid}/user-invitations", async (
            Guid merchantId, AdminMerchantInvitationRequest body, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IActorScope actorScope,
            IOptions<UserInvitationOptions> options, IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            using var binding = actorScope.Begin(merchantId, scope.Current.AdminId);
            var result = await mediator.Send(new CreateInvitationCommand(
                body.Email, merchantId, scope.Current.AdminId, http.TraceIdentifier, options.Value.TtlHours,
                InvitationActorAudience.Admin, body.RoleCodes ?? [], IdempotencyKeys.Require(http)), ct);
            return Results.Created($"/api/v1/merchants/users/invitations/{result.InvitationId}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("InviteMerchantUserAdmin")
            .WithSummary("เชิญผู้ใช้เข้าร้านค้าโดย Admin")
            .WithDescription("สร้าง tenant-bound invitation ด้วยอีเมลและ roleCodes สำหรับ merchant ที่ Active ใน Admin scope แล้ว enqueue การส่งลิงก์ ไม่คืน raw token ต้องส่ง Idempotency-Key")
            .Produces<CreateInvitationResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/merchants/{merchantId:guid}/users/{merchantUserId:guid}", async (
            Guid merchantId, Guid merchantUserId, AdminMerchantUserUpdateRequest body, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IActorScope actorScope,
            IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            using var binding = actorScope.Begin(merchantId, scope.Current.AdminId);
            var result = await mediator.Send(new UpdateMerchantUserCommand(
                merchantUserId, merchantId, scope.Current.AdminId, body.FirstName, body.LastName,
                body.SaleCode, body.LicenseNumber, body.Phone, http.TraceIdentifier,
                VersionEtags.Require(http)), ct);
            VersionEtags.Set(http, result.Version);
            return Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserManage)
            .WithMetadata(new IfMatchMutationMarker("204"))
            .WithTags("ผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("UpdateMerchantUserAdmin")
            .WithSummary("แก้ไขผู้ใช้ร้านค้าโดย Admin")
            .WithDescription("แก้ firstName, lastName, saleCode, licenseNumber และ phone ของผู้ใช้ใน merchant ที่ Active และอยู่ใน Admin scope ต้องส่ง If-Match")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapRoles(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/roles", async (
            Guid merchantId, HttpContext http, IAdminScope scope, IAdminMerchantDirectory merchants,
            IMediator mediator, CancellationToken ct) =>
        {
            RequireReadAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
            var page = await mediator.Send(new ListRolesQuery
            {
                Context = RoleSideContext.Merchant(merchantId),
                Page = parsed.Page,
                Limit = parsed.Limit,
                Filters = parsed.Filters,
                Sort = parsed.Sort,
                Search = parsed.Search,
            }, ct);
            return Results.Ok(new PagedResult<AdminMerchantRoleResponse>(
                page.Items.Select(ToWire).ToArray(), page.Page, page.Limit, page.Total));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesView)
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("ListAdminMerchantRoles")
            .WithSummary("รายการบทบาทของร้านค้า")
            .WithDescription("คืน shared roles และ custom roles ที่มองเห็นได้ใน merchant ที่ Active พร้อม permissions และ user count รองรับ SFS สูงสุด 100 แถวต่อหน้า")
            .Produces<PagedResult<AdminMerchantRoleResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/merchants/{merchantId:guid}/roles/{code}", async (
            Guid merchantId, string code, HttpContext http, IAdminScope scope,
            IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
        {
            RequireReadAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            var role = await mediator.Send(new GetRoleQuery(RoleSideContext.Merchant(merchantId), code), ct);
            if (role is null)
                return Results.NotFound();
            VersionEtags.Set(http, role.Version);
            return Results.Ok(ToWire(role));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("GetAdminMerchantRole")
            .WithSummary("อ่านบทบาทของร้านค้า")
            .WithDescription("คืน role, permissions, user count, shared flag และ ETag ภายใน merchant ที่ Active หากไม่พบหรือมองไม่เห็น -> 404")
            .Produces<AdminMerchantRoleResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/merchants/{merchantId:guid}/permissions", async (
            Guid merchantId, IAdminScope scope, IAdminMerchantDirectory merchants,
            IMediator mediator, CancellationToken ct) =>
        {
            RequireReadAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            var catalog = await mediator.Send(new GetPermissionCatalogQuery(Scope.Merchant), ct);
            return Results.Ok(new AdminMerchantPermissionCatalogResponse(
                catalog.Groups.Select(x => new AdminMerchantPermissionGroupResponse(x.Key, x.Name)).ToArray(),
                catalog.Permissions.Select(x =>
                    new AdminMerchantPermissionResponse(x.Key, x.Name, x.Resource)).ToArray()));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesView)
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("ListAdminMerchantPermissions")
            .WithSummary("แคตตาล็อกสิทธิ์ฝั่งร้านค้า")
            .WithDescription("คืน permission groups และ permission keys เฉพาะ Scope.Merchant สำหรับสร้าง role ของ merchant ที่ Active")
            .Produces<AdminMerchantPermissionCatalogResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/merchants/{merchantId:guid}/roles", async (
            Guid merchantId, AdminMerchantRoleCreateRequest body, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            var role = await mediator.Send(new CreateRoleCommand(
                RoleSideContext.Merchant(merchantId), body.Code, body.Name, body.Description, body.Color,
                ParseStatus(body.Status), body.Permissions ?? [], http.TraceIdentifier), ct);
            VersionEtags.Set(http, role.Version);
            return Results.Created(
                $"/api/v1/merchants/{merchantId}/roles/{Uri.EscapeDataString(role.Code)}", ToWire(role));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesManage)
            .WithMetadata(new EtagResponseMarker("201"))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("CreateAdminMerchantRole")
            .WithSummary("สร้างบทบาทของร้านค้า")
            .WithDescription("สร้าง custom Merchant role ด้วย code, name, status และ Merchant permission keys ภายใน merchant ที่ Active รหัสซ้ำหรือ permission ข้าม scope ถูกปฏิเสธ")
            .Produces<AdminMerchantRoleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/merchants/{merchantId:guid}/roles/{code}", async (
            Guid merchantId, string code, AdminMerchantRoleUpdateRequest body, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            var role = await mediator.Send(new UpdateRoleCommand(
                RoleSideContext.Merchant(merchantId), code, body.Name, body.Description, body.Color,
                ParseStatus(body.Status), body.Permissions ?? [], http.TraceIdentifier,
                VersionEtags.Require(http)), ct);
            VersionEtags.Set(http, role.Version);
            return Results.Ok(ToWire(role));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesManage)
            .WithMetadata(new IfMatchMutationMarker("200"))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("UpdateAdminMerchantRole")
            .WithSummary("แก้ไขบทบาทของร้านค้า")
            .WithDescription("แก้ name, description, color, status และ permissions ของ custom role โดย code เปลี่ยนไม่ได้ ต้องส่ง If-Match; shared/anchor role แก้ไม่ได้ -> 409")
            .Produces<AdminMerchantRoleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/merchants/{merchantId:guid}/roles/{code}", async (
            Guid merchantId, string code, HttpContext http, IAdminScope scope,
            IAdminMerchantDirectory merchants, IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            await mediator.Send(new DeleteRoleCommand(
                RoleSideContext.Merchant(merchantId), code, http.TraceIdentifier,
                VersionEtags.Require(http)), ct);
            return Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesManage)
            .WithMetadata(new IfMatchMutationMarker("204", EmitsEtag: false))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("DeleteAdminMerchantRole")
            .WithSummary("ลบบทบาทของร้านค้า")
            .WithDescription("ลบ custom role ภายใน merchant โดยต้องส่ง If-Match; shared/anchor role หรือ role ที่ยังมีผู้ใช้ผูกอยู่ลบไม่ได้ -> 409")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles", async (
            Guid merchantId, Guid merchantUserId, AdminMerchantUserRolesRequest body, HttpContext http,
            IAdminScope scope, IAdminMerchantDirectory merchants, IActorScope actorScope,
            IMediator mediator, CancellationToken ct) =>
        {
            RequireMutationAccess(scope, merchantId);
            await RequireActiveMerchantAsync(merchants, merchantId, ct);
            using var binding = actorScope.Begin(merchantId, scope.Current.AdminId);
            var result = await mediator.Send(new SetRolesCommand(
                merchantUserId, body.RoleCodes ?? [], merchantId, scope.Current.AdminId,
                http.TraceIdentifier, VersionEtags.Require(http)), ct);
            VersionEtags.Set(http, result.Version);
            return Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantRolesManage)
            .WithMetadata(new IfMatchMutationMarker("204"))
            .WithTags("บทบาทผู้ใช้ร้านค้า (ผู้ดูแลระบบ)").WithName("SetAdminMerchantUserRoles")
            .WithSummary("กำหนดบทบาทให้ผู้ใช้ร้านค้าโดย Admin")
            .WithDescription("แทน roleCodes ทั้งชุดของผู้ใช้ใน merchant ที่ Active โดยรับเฉพาะ role ที่มองเห็นและ Active ต้องส่ง If-Match")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static AdminMerchantRoleResponse ToWire(RoleListItem role) => new(
        role.Id, role.Code, role.Name, role.Description, role.Color,
        role.Status == RoleStatus.Active ? "active" : "inactive",
        role.PermissionKeys, role.UserCount, role.Shared, role.Version);

    private static RoleStatus ParseStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "active" => RoleStatus.Active,
        "inactive" => RoleStatus.Inactive,
        _ => throw new InvalidRequestException("Role status must be active or inactive.", "validation_failed"),
    };

    private static void RequireReadAccess(IAdminScope scope, Guid merchantId)
    {
        if (!scope.Accessible.Allows(merchantId))
            throw new NotFoundException("Merchant was not found.");
    }

    private static void RequireMutationAccess(IAdminScope scope, Guid merchantId)
    {
        if (!scope.Accessible.Allows(merchantId))
            throw new AdminMerchantAccessDeniedException("Merchant is outside the Admin scope.");
    }

    private static async Task RequireActiveMerchantAsync(
        IAdminMerchantDirectory merchants, Guid merchantId, CancellationToken cancellationToken)
    {
        if (!await merchants.IsActiveMerchantAsync(merchantId, cancellationToken))
            throw new NotFoundException("Active merchant was not found.");
    }

    private static async ValueTask<object?> HandleKnownErrors(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AdminMerchantAccessDeniedException)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "merchant_scope_forbidden",
                    ["correlationId"] = context.HttpContext.TraceIdentifier,
                });
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminMerchantInvitationRequest(string Email, IReadOnlyList<string>? RoleCodes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminMerchantUserUpdateRequest(
    string FirstName, string LastName, string? SaleCode, string? LicenseNumber, string? Phone);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminMerchantRoleCreateRequest(
    string Code, string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string>? Permissions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminMerchantRoleUpdateRequest(
    string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string>? Permissions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdminMerchantUserRolesRequest(IReadOnlyList<string>? RoleCodes);

internal sealed record AdminMerchantRoleResponse(
    Guid Id, string Code, string Name, string? Description, string? Color, string Status,
    IReadOnlyList<string> Permissions, int UserCount, bool Shared, long Version);

internal sealed record AdminMerchantPermissionCatalogResponse(
    IReadOnlyList<AdminMerchantPermissionGroupResponse> Groups,
    IReadOnlyList<AdminMerchantPermissionResponse> Permissions);
internal sealed record AdminMerchantPermissionGroupResponse(string Key, string Label);
internal sealed record AdminMerchantPermissionResponse(string Key, string Label, string Resource);
