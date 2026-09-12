using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Merchants.Application.AdminControlPlane;

namespace Api.ControlPlane;

/// <summary>
/// Canonical v1 Merchant master surface. The existing AdminControl endpoints remain compatibility aliases;
/// this class owns only the approved API-054..061 method/path contracts and delegates every write to the
/// Task 3 merchant master store.
/// </summary>
internal static class CanonicalMerchantConfigurationEndpoints
{
    public static void MapCanonicalMerchantConfigurationEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.AddEndpointFilter(HandleKnownErrors);
        MapMerchant(routes);
        MapBranches(routes);
        MapSales(routes);
    }

    private static void MapMerchant(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}", async (
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.GetMerchantAsync(merchantId, Access(scope), ct);
            if (value is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, extensions: Extensions(http, "not_found"));
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("ร้านค้า")
            .WithName("GetCanonicalMerchant")
            .WithSummary("อ่าน Merchant")
            .WithDescription("คืน Merchant ภายใน Admin scope พร้อม status, metadata และ ETag")
            .Produces<MerchantDetailView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/merchants/{merchantId:guid}", async (
            Guid merchantId,
            CanonicalMerchantPatchRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            if (body.MerchantId is { } supplied && supplied != merchantId)
                throw new InvalidRequestException("MerchantId must match the route.", "validation_failed");
            var result = await store.PatchMerchantAsync(new AdminMerchantPatch(
                merchantId, body.Name, body.Note, body.EnabledChannels, body.Metadata, body.Status,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า")
            .WithName("PatchCanonicalMerchant")
            .WithSummary("แก้ไข Merchant")
            .WithDescription("แก้ข้อมูลหรือสถานะ Merchant โดยคง code และต้องส่ง If-Match กับ Idempotency-Key")
            .Accepts<CanonicalMerchantPatchRequest>("application/json")
            .Produces<MerchantDetailView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static void MapBranches(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/branches", async (
            Guid merchantId,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            int page = 1,
            int limit = 25,
            string? search = null,
            string? status = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListBranchesAsync(
                new BranchListQuery(page, limit, search, status, merchantId, Access(scope)), ct));
        }).RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithTags("ร้านค้า")
            .WithName("ListCanonicalMerchantBranches")
            .WithSummary("รายการสาขา Merchant")
            .WithDescription("คืน Branch master ภายใน Merchant scope แบบแบ่งหน้าและกรองได้")
            .Produces<PagedResult<BranchView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/merchants/{merchantId:guid}/branches", async (
            Guid merchantId,
            CreateBranchRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.CreateBranchAsync(new CreateBranchIntent(
                merchantId, body.Code, body.Name, IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Created($"/api/v1/merchants/{merchantId:D}/branches/{result.Value.BranchId:D}", result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new EtagResponseMarker("201"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า")
            .WithName("CreateCanonicalMerchantBranch")
            .WithSummary("สร้างสาขา Merchant")
            .WithDescription("สร้าง Branch ที่มี code ไม่ซ้ำภายใน Merchant และคืน ETag")
            .Accepts<CreateBranchRequest>("application/json")
            .Produces<BranchView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPatch("/merchants/{merchantId:guid}/branches/{branchId:guid}", async (
            Guid merchantId,
            Guid branchId,
            PatchBranchRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.UpdateBranchAsync(new UpdateBranchIntent(
                branchId, merchantId, body.Name, body.Status, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า")
            .WithName("PatchCanonicalMerchantBranch")
            .WithSummary("แก้ไขสาขา Merchant")
            .WithDescription("แก้ชื่อหรือสถานะ Branch โดย code และ MerchantId เปลี่ยนไม่ได้ และต้องส่ง If-Match")
            .Accepts<PatchBranchRequest>("application/json")
            .Produces<BranchView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static void MapSales(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/sales", async (
            Guid merchantId,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            int page = 1,
            int limit = 25,
            string? search = null,
            string? status = null,
            Guid? branchId = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListSalesAsync(
                new SaleListQuery(page, limit, search, status, branchId, merchantId, Access(scope)), ct));
        }).RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithTags("ร้านค้า")
            .WithName("ListCanonicalMerchantSales")
            .WithSummary("รายการ Sale ของ Merchant")
            .WithDescription("คืน Sale master และ Home Branch ภายใน Merchant scope แบบแบ่งหน้าและกรองได้")
            .Produces<PagedResult<SaleView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/merchants/{merchantId:guid}/sales", async (
            Guid merchantId,
            CreateSaleRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.CreateSaleAsync(new CreateSaleIntent(
                merchantId, body.BranchId, body.Code, body.Name,
                IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Created($"/api/v1/merchants/{merchantId:D}/sales/{result.Value.SaleId:D}", result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new EtagResponseMarker("201"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า")
            .WithName("CreateCanonicalMerchantSale")
            .WithSummary("สร้าง Sale ของ Merchant")
            .WithDescription("สร้าง Sale และผูก Home Branch ที่อยู่ Merchant เดียวกัน โดย code ต้องไม่ซ้ำ")
            .Accepts<CreateSaleRequest>("application/json")
            .Produces<SaleView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPatch("/merchants/{merchantId:guid}/sales/{saleId:guid}", async (
            Guid merchantId,
            Guid saleId,
            PatchSaleRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.UpdateSaleAsync(new UpdateSaleIntent(
                saleId, merchantId, body.BranchId, body.Name, body.Status, body.Reason,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า")
            .WithName("PatchCanonicalMerchantSale")
            .WithSummary("แก้ไข Sale ของ Merchant")
            .WithDescription("แก้ข้อมูล Sale หรือย้าย Home Branch ด้วย trusted master source โดยต้องส่ง reason และ If-Match เมื่อย้าย")
            .Accepts<PatchSaleRequest>("application/json")
            .Produces<SaleView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static AdminMerchantAccess Access(IAdminScope scope) => new(
        scope.Current.AdminId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static async ValueTask<object?> HandleKnownErrors(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AdminMerchantAccessDeniedException)
        {
            return Problem(context.HttpContext, StatusCodes.Status403Forbidden, "merchant_scope_forbidden");
        }
        catch (ConcurrencyConflictException)
        {
            return Problem(context.HttpContext, StatusCodes.Status412PreconditionFailed, "precondition_failed");
        }
        catch (ConflictException ex)
        {
            return Problem(context.HttpContext, StatusCodes.Status409Conflict, ex.Code ?? "conflict");
        }
    }

    private static IResult Problem(HttpContext http, int status, string code) => Results.Problem(
        statusCode: status,
        extensions: Extensions(http, code));

    private static Dictionary<string, object?> Extensions(HttpContext http, string code) => new()
    {
        ["code"] = code,
        ["correlationId"] = http.TraceIdentifier,
    };

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }
}

internal sealed record CanonicalMerchantPatchRequest(
    Guid? MerchantId,
    string? Name,
    string? Note,
    IReadOnlyList<string>? EnabledChannels,
    JsonElement? Metadata,
    string? Status);

internal sealed record CreateBranchRequest(
    [property: Required] string Code,
    [property: Required] string Name);

internal sealed record PatchBranchRequest(string? Name, string? Status);

internal sealed record CreateSaleRequest(
    Guid BranchId,
    [property: Required] string Code,
    [property: Required] string Name);

internal sealed record PatchSaleRequest(
    Guid? BranchId,
    string? Name,
    string? Status,
    string? Reason);
