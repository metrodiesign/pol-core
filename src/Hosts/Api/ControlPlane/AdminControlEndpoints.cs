using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Merchants.Application.AdminControlPlane;
using Payments.Application.AdminControlPlane;
using Payments.Domain.Routing;

namespace Api.ControlPlane;

internal static class AdminControlEndpoints
{
    public static void MapAdminControlEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.MapGroup(string.Empty).AddEndpointFilter(HandleKnownErrors);
        MapMerchants(routes);
        MapOriginators(routes);
        MapPaymentCapabilities(routes);
        MapPspConnections(routes);
        MapRouting(routes);
    }

    private static void MapPaymentCapabilities(RouteGroupBuilder api)
    {
        api.MapGet("/payments/methods/{method}", async (
            string method, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetMethodAsync(method, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentMethodCapability")
            .WithSummary("อ่านสถานะ payment method")
            .WithDescription("คืนสถานะเปิดใช้งานของ payment method ระดับ platform พร้อม ETag")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/methods/{method}", async (
            string method, SetPaymentCapabilityRequest body, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
        {
            var result = await store.SetMethodAsync(new SetGlobalPaymentCapabilityIntent(
                method, null, method, null, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentMethodCapability")
            .WithSummary("กำหนดสถานะ payment method")
            .WithDescription("เปิดหรือปิด payment method ระดับ platform โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/providers/{providerCode}", async (
            string providerCode, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetProviderAsync(providerCode, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentProviderCapability")
            .WithSummary("อ่านสถานะ payment provider")
            .WithDescription("คืนสถานะเปิดใช้งานของ payment provider พร้อม ETag")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/providers/{providerCode}", async (
            string providerCode, SetPaymentCapabilityRequest body, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
        {
            var result = await store.SetProviderAsync(new SetGlobalPaymentCapabilityIntent(
                providerCode, providerCode, null, null, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentProviderCapability")
            .WithSummary("กำหนดสถานะ payment provider")
            .WithDescription("เปิดหรือปิด payment provider โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/providers/{providerCode}/methods/{method}", async (
            string providerCode, string method, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetProviderMethodAsync(
                providerCode, method, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentProviderMethodCapability")
            .WithSummary("อ่าน method ของ payment provider")
            .WithDescription("คืนสถานะที่ provider รองรับ payment method พร้อม ETag")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/providers/{providerCode}/methods/{method}", async (
            string providerCode, string method, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IGlobalPaymentCapabilityControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetProviderMethodAsync(new SetGlobalPaymentCapabilityIntent(
                method, providerCode, method, null, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentProviderMethodCapability")
            .WithSummary("กำหนด method ของ payment provider")
            .WithDescription("เปิดหรือปิด payment method ของ provider โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/providers/{providerCode}/methods/{method}/options/{option}", async (
            string providerCode, string method, string option, HttpContext http, IAdminScope scope,
            IGlobalPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetProviderMethodOptionAsync(
                providerCode, method, option, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentProviderMethodOptionCapability")
            .WithSummary("อ่าน option ของ provider method")
            .WithDescription("คืนสถานะ payment option ที่ provider method รองรับ พร้อม ETag")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/providers/{providerCode}/methods/{method}/options/{option}", async (
            string providerCode, string method, string option, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IGlobalPaymentCapabilityControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetProviderMethodOptionAsync(new SetGlobalPaymentCapabilityIntent(
                option, providerCode, method, option, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentProviderMethodOptionCapability")
            .WithSummary("กำหนด option ของ provider method")
            .WithDescription("เปิดหรือปิด payment option ของ provider method โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<GlobalPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/psp-connections/{connectionId:guid}/methods/{method}", async (
            Guid connectionId, string method, HttpContext http, IAdminScope scope,
            IAccountPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetAccountMethodAsync(
                connectionId, method, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentAccountMethodCapability")
            .WithSummary("อ่าน method ของ PSP connection")
            .WithDescription("คืนสถานะ payment method ของ PSP connection ภายใน Admin scope พร้อม ETag")
            .Produces<AccountPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/psp-connections/{connectionId:guid}/methods/{method}", async (
            Guid connectionId, string method, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IAccountPaymentCapabilityControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetAccountMethodAsync(new SetAccountPaymentCapabilityIntent(
                connectionId, method, null, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentAccountMethodCapability")
            .WithSummary("กำหนด method ของ PSP connection")
            .WithDescription("เปิดหรือปิด payment method ของ PSP connection โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<AccountPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}", async (
            Guid connectionId, string method, string option, HttpContext http, IAdminScope scope,
            IAccountPaymentCapabilityControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetAccountMethodOptionAsync(
                connectionId, method, option, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetPaymentAccountMethodOptionCapability")
            .WithSummary("อ่าน option ของ PSP connection")
            .WithDescription("คืนสถานะ payment option ของ PSP connection ภายใน Admin scope พร้อม ETag")
            .Produces<AccountPaymentCapabilityView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}", async (
            Guid connectionId, string method, string option, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IAccountPaymentCapabilityControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetAccountMethodOptionAsync(new SetAccountPaymentCapabilityIntent(
                connectionId, method, option, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetPaymentAccountMethodOptionCapability")
            .WithSummary("กำหนด option ของ PSP connection")
            .WithDescription("เปิดหรือปิด payment option ของ PSP connection โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<AccountPaymentCapabilityView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/merchants/{merchantId:guid}/methods", async (
            Guid merchantId, IAdminScope scope, IAdminPaymentsControlStore store, CancellationToken ct) =>
        {
            var value = await store.ListMerchantMethodsAsync(merchantId, PaymentsAccess(scope), ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithTags("Payment capability").WithName("ListMerchantPaymentMethods")
            .WithSummary("รายการ effective method ของร้านค้า")
            .WithDescription("คืน payment method ที่ร้านค้าใช้ได้หลังรวม platform, provider และ account policy")
            .Produces<IReadOnlyList<Payments.Application.Capabilities.EffectivePaymentMethod>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/payments/merchants/{merchantId:guid}/methods/{method}", async (
            Guid merchantId, string method, HttpContext http, IAdminScope scope,
            IAdminPaymentsControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetMerchantMethodAsync(
                merchantId, method, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetMerchantPaymentMethodPolicy")
            .WithSummary("อ่าน payment method policy ของร้านค้า")
            .WithDescription("คืน policy ของ payment method สำหรับร้านค้าใน Admin scope พร้อม ETag")
            .Produces<MerchantPaymentMethodView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/merchants/{merchantId:guid}/methods/{method}", async (
            Guid merchantId, string method, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetMerchantMethodAsync(new SetMerchantPaymentCapabilityIntent(
                merchantId, method, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetMerchantPaymentMethodPolicy")
            .WithSummary("กำหนด payment method policy ของร้านค้า")
            .WithDescription("เปิดหรือปิด payment method ของร้านค้าโดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<MerchantPaymentMethodView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods", async (
            Guid merchantId, Guid userId, HttpContext http, IAdminScope scope,
            IAdminPaymentsControlStore store, CancellationToken ct) =>
        {
            var value = await store.ListMerchantUserMethodsAsync(
                merchantId, userId, PaymentsAccess(scope), ct);
            if (value is null)
                return Results.NotFound();
            VersionEtags.Set(http, value.Sum(x => x.Version));
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("ListMerchantUserPaymentMethods")
            .WithSummary("รายการ method ของ Merchant User")
            .WithDescription("คืน payment method policy ทั้งหมดของ Merchant User ภายในร้านค้าที่ Admin เข้าถึงได้")
            .Produces<IReadOnlyList<MerchantUserPaymentMethodView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}", async (
            Guid merchantId, Guid userId, string method, HttpContext http, IAdminScope scope,
            IAdminPaymentsControlStore store, CancellationToken ct) =>
            CapabilityResult(http, await store.GetMerchantUserMethodAsync(
                merchantId, userId, method, PaymentsAccess(scope), ct)))
            .RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("Payment capability").WithName("GetMerchantUserPaymentMethodPolicy")
            .WithSummary("อ่าน method policy ของ Merchant User")
            .WithDescription("คืน payment method policy ของ Merchant User พร้อม ETag")
            .Produces<MerchantUserPaymentMethodView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}", async (
            Guid merchantId, Guid userId, string method, SetPaymentCapabilityRequest body,
            HttpContext http, IAdminScope scope, IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.SetMerchantUserMethodAsync(new SetMerchantUserPaymentCapabilityIntent(
                merchantId, userId, method, body.Enabled, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("Payment capability").WithName("SetMerchantUserPaymentMethodPolicy")
            .WithSummary("กำหนด method policy ของ Merchant User")
            .WithDescription("เปิดหรือปิด payment method ของ Merchant User โดยต้องส่ง If-Match และ Idempotency-Key")
            .Produces<MerchantUserPaymentMethodView>().ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet(
            "/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/resolution",
            async (Guid merchantId, Guid userId, string method, IAdminScope scope,
                IAdminPaymentsControlStore store, CancellationToken ct) =>
            {
                var value = await store.ResolveMerchantUserMethodAsync(
                    merchantId, userId, method, PaymentsAccess(scope), ct);
                return value is null ? Results.NotFound() : Results.Ok(value);
            }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserView)
            .WithTags("Payment capability").WithName("ResolveMerchantUserPaymentMethod")
            .WithSummary("ตรวจ effective method ของ Merchant User")
            .WithDescription("คืนผล intersection และ denial reason ของ payment method สำหรับ Merchant User")
            .Produces<UserPaymentMethodResolutionView>().ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet(
            "/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/options",
            async (Guid merchantId, Guid userId, string method, string provider, IAdminScope scope,
                IAdminPaymentsControlStore store, CancellationToken ct) =>
            {
                var value = await store.ResolveMerchantUserOptionsAsync(
                    merchantId, userId, method, provider, PaymentsAccess(scope), ct);
                return value is null ? Results.NotFound() : Results.Ok(value);
            }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantUserView)
            .WithTags("Payment capability").WithName("ResolveMerchantUserPaymentOptions")
            .WithSummary("รายการ effective option ของ Merchant User")
            .WithDescription("คืน payment option ที่ผ่าน provider และ account policy สำหรับ Merchant User")
            .Produces<IReadOnlyList<Payments.Application.Capabilities.EffectivePaymentOption>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapMerchants(RouteGroupBuilder api)
    {
        api.MapGet("/merchants", async (
            IAdminScope scope,
            IAdminMerchantControlStore store,
            int page = 1,
            int limit = 25,
            string? search = null,
            string? status = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListMerchantsAsync(
                new AdminMerchantListQuery(page, limit, search, status, MerchantAccess(scope)), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithTags("ร้านค้า (ผู้ดูแลระบบ)").WithName("ListMerchants")
            .WithSummary("รายการร้านค้า")
            .WithDescription("คืนร้านค้าภายใน Admin scope แบบแบ่งหน้า กรอง status และค้นจาก code หรือ name ได้")
            .Produces<PagedResult<AdminMerchantListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPut("/merchants/{merchantId:guid}", async (
            Guid merchantId,
            UpdateMerchantRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            EnsureMerchant(body.MerchantId, merchantId);
            var result = await store.UpdateMerchantAsync(new AdminMerchantMutation(
                merchantId, body.Name, body.Note, body.EnabledChannels ?? [], body.Metadata,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), MerchantAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า (ผู้ดูแลระบบ)").WithName("UpdateMerchant")
            .WithSummary("แก้ไขร้านค้า")
            .WithDescription("แก้ name, note, enabledChannels และ metadata ของร้านค้าใน Admin scope โดย merchantId ใน body ต้องตรง route และต้องส่ง If-Match กับ Idempotency-Key")
            .Produces<AdminMerchantListItem>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        MapMerchantStatus(api, "suspend", false, "SuspendMerchant");
        MapMerchantStatus(api, "reactivate", true, "ReactivateMerchant");
    }

    private static void MapMerchantStatus(RouteGroupBuilder api, string segment, bool activate, string name)
    {
        api.MapPost($"/merchants/{{merchantId:guid}}/{segment}", async (
            Guid merchantId,
            MerchantStatusRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            EnsureMerchant(body.MerchantId, merchantId);
            var result = await store.ChangeMerchantStatusAsync(new AdminMerchantStatusMutation(
                merchantId, activate, VersionEtags.Require(http), IdempotencyKeys.Require(http), MerchantAccess(scope)), ct);
            VersionEtags.Set(http, result.Value.Version);
            return Results.Ok(result.Value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ร้านค้า (ผู้ดูแลระบบ)").WithName(name)
            .WithSummary(activate ? "เปิดใช้งานร้านค้าอีกครั้ง" : "ระงับร้านค้า")
            .WithDescription("เปลี่ยนสถานะร้านค้าใน Admin scope โดย merchantId ใน body ต้องตรง route และต้องส่ง If-Match กับ Idempotency-Key")
            .Produces<AdminMerchantListItem>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapOriginators(RouteGroupBuilder api)
    {
        api.MapGet("/originators", async (
            IAdminScope scope,
            IAdminMerchantControlStore store,
            int page = 1,
            int limit = 25,
            string? search = null,
            Guid? merchantId = null,
            string? type = null,
            string? status = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListOriginatorsAsync(
                new OriginatorListQuery(page, limit, search, merchantId, type, status, MerchantAccess(scope)), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithTags("แหล่งที่มารายการ").WithName("ListOriginators")
            .WithSummary("รายการ Originator")
            .WithDescription("คืน Originator ภายใน Admin scope แบบแบ่งหน้า กรอง merchantId, type, status และค้นจาก code, name หรือ saleCode ได้")
            .Produces<PagedResult<OriginatorView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/originators/{originatorId:guid}", async (
            Guid originatorId,
            Guid? merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.GetOriginatorAsync(originatorId, merchantId, MerchantAccess(scope), ct);
            if (value is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantView)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("แหล่งที่มารายการ").WithName("GetOriginator")
            .WithSummary("อ่าน Originator")
            .WithDescription("คืน Originator และ ETag โดยใช้ merchantId ช่วยระบุ scope หากไม่พบหรือนอก Admin scope -> 404")
            .Produces<OriginatorView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/originators", async (
            CreateOriginatorRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.CreateOriginatorAsync(new CreateOriginatorIntent(
                body.MerchantId, body.Code, body.Name, body.Type, body.SaleCode,
                body.LinkedApiClientId, MerchantAccess(scope)), ct);
            VersionEtags.Set(http, value.Version);
            return Results.Created($"/api/v1/originators/{value.OriginatorId:D}", value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new EtagResponseMarker("201"))
            .WithTags("แหล่งที่มารายการ").WithName("CreateOriginator")
            .WithSummary("สร้าง Originator")
            .WithDescription("สร้าง Originator ประเภท branch, agent, broker, staff หรือ app ภายใน merchant ที่ Admin เข้าถึงได้ รองรับ saleCode และ linkedApiClientId")
            .Produces<OriginatorView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/originators/{originatorId:guid}", async (
            Guid originatorId,
            UpdateOriginatorRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.UpdateOriginatorAsync(new UpdateOriginatorIntent(
                originatorId, body.MerchantId, body.Name, body.Type, body.SaleCode,
                body.LinkedApiClientId, VersionEtags.Require(http), MerchantAccess(scope)), ct);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"))
            .WithTags("แหล่งที่มารายการ").WithName("UpdateOriginator")
            .WithSummary("แก้ไข Originator")
            .WithDescription("แก้ name, type, saleCode และ linkedApiClientId โดย code เปลี่ยนไม่ได้ ต้องส่ง merchantId และ If-Match")
            .Produces<OriginatorView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        MapOriginatorState(api, "enable", true, "EnableOriginator");
        MapOriginatorState(api, "disable", false, "DisableOriginator");

        api.MapDelete("/originators/{originatorId:guid}", async (
            Guid originatorId,
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            await store.DeleteOriginatorAsync(originatorId, merchantId, VersionEtags.Require(http),
                MerchantAccess(scope), ct);
            return Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("204", EmitsEtag: false))
            .WithTags("แหล่งที่มารายการ").WithName("DeleteOriginator")
            .WithSummary("ลบ Originator")
            .WithDescription("ลบ Originator ตาม originatorId และ merchantId โดยต้องส่ง If-Match รายการที่ยังถูกอ้างอิงลบไม่ได้ -> 409")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapOriginatorState(RouteGroupBuilder api, string segment, bool enable, string name)
    {
        api.MapPost($"/originators/{{originatorId:guid}}/{segment}", async (
            Guid originatorId,
            MerchantStatusRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminMerchantControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.SetOriginatorStateAsync(new OriginatorStateIntent(
                originatorId, body.MerchantId, enable, VersionEtags.Require(http), MerchantAccess(scope)), ct);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"))
            .WithTags("แหล่งที่มารายการ").WithName(name)
            .WithSummary(enable ? "เปิดใช้งาน Originator" : "ปิดใช้งาน Originator")
            .WithDescription("เปลี่ยนสถานะ Originator ภายใน merchant ที่ระบุ โดยต้องส่ง If-Match หากไม่พบหรือนอก Admin scope -> 404")
            .Produces<OriginatorView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapPspConnections(RouteGroupBuilder api)
    {
        api.MapGet("/payments/psp-connections", async (
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            int page = 1,
            int limit = 25,
            string? search = null,
            Guid? merchantId = null,
            string? psp = null,
            string? health = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListConnectionsAsync(
                new PspConnectionQuery(page, limit, search, merchantId, psp, health, PaymentsAccess(scope)), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การเชื่อมต่อ PSP").WithName("ListPspConnections")
            .WithSummary("รายการ PSP connection")
            .WithDescription("คืน PSP connection ภายใน Admin scope แบบแบ่งหน้า กรอง merchantId, psp, health และค้นหาได้ ไม่คืน credential")
            .Produces<PagedResult<PspConnectionView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/payments/psp-connections/{connectionId:guid}", async (
            Guid connectionId,
            Guid? merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.GetConnectionAsync(connectionId, merchantId, PaymentsAccess(scope), ct);
            if (value is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("การเชื่อมต่อ PSP").WithName("GetPspConnection")
            .WithSummary("อ่าน PSP connection")
            .WithDescription("คืน config, enabled methods, health, masked secret hints และ ETag โดยไม่คืน credential หากไม่พบหรือนอก Admin scope -> 404")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/payments/psp-connections", async (
            CreatePspConnectionRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.CreateConnectionAsync(new CreatePspConnectionIntent(
                body.MerchantId, body.Psp, body.EnabledMethods ?? [], body.Config,
                body.Secrets ?? new Dictionary<string, string>(), body.PspMerchantId,
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Created($"/api/v1/payments/psp-connections/{result.Connection.PspConnectionId:D}", result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .RequirePermission(Keys.MerchantManage)
            .WithMetadata(new EtagResponseMarker("201"), new IdempotencyMutationMarker())
            .WithTags("การเชื่อมต่อ PSP").WithName("CreatePspConnection")
            .WithSummary("สร้าง PSP connection")
            .WithDescription("สร้าง connection สำหรับ merchant ใน Admin scope เก็บ secrets ใน vault และไม่คืน plaintext ต้องส่ง Idempotency-Key; psp/config/method ไม่ถูกต้อง -> 400")
            .Produces<PspConnectionView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/payments/psp-connections/{connectionId:guid}", async (
            Guid connectionId,
            UpdatePspConnectionRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.UpdateConnectionAsync(new UpdatePspConnectionIntent(
                connectionId, body.MerchantId, body.EnabledMethods ?? [], body.Config, body.IsEnabled,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Ok(result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .RequirePermission(Keys.MerchantManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การเชื่อมต่อ PSP").WithName("UpdatePspConnection")
            .WithSummary("แก้ไข PSP connection")
            .WithDescription("แก้ enabled methods, config และสถานะเปิดใช้งานโดยไม่เปลี่ยน credential ต้องส่ง merchantId, If-Match และ Idempotency-Key")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/payments/psp-connections/{connectionId:guid}/test", async (
            Guid connectionId,
            MerchantStatusRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.TestConnectionAsync(new TestPspConnectionIntent(
                connectionId, body.MerchantId, VersionEtags.Require(http),
                IdempotencyKeys.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Ok(result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การเชื่อมต่อ PSP").WithName("TestPspConnection")
            .WithSummary("ทดสอบ PSP connection")
            .WithDescription("เรียก capability test ของ PSP adapter ด้วย credential ปัจจุบัน แล้วอัปเดต health และ ETag ต้องส่ง merchantId, If-Match และ Idempotency-Key; upstream ล้มเหลว -> 502")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        api.MapPost("/payments/psp-connections/{connectionId:guid}/credential-change-requests", async (
            Guid connectionId,
            PspCredentialChangeRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
                connectionId, body.MerchantId, body.Secrets ?? new Dictionary<string, string>(),
                body.PspMerchantId, VersionEtags.Require(http), IdempotencyKeys.Require(http),
                http.TraceIdentifier, PaymentsAccess(scope)), ct);
            return Results.Accepted(value: result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("202", EmitsEtag: false), new IdempotencyMutationMarker())
            .WithTags("การเชื่อมต่อ PSP").WithName("RequestPspCredentialChange")
            .WithSummary("ขอเปลี่ยน PSP credential")
            .WithDescription("stage credential version ใหม่ใน vault แล้วสร้างคำขอ maker-checker โดยยังไม่เปิดใช้ ต้องส่ง merchantId, If-Match และ Idempotency-Key")
            .Produces<PspCredentialChangeResult>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapRouting(RouteGroupBuilder api)
    {
        api.MapGet("/payments/routing-rulesets", async (
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            int page = 1,
            int limit = 25,
            Guid? merchantId = null,
            string? status = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListRulesetsAsync(
                new RoutingRulesetQuery(page, limit, merchantId, status, PaymentsAccess(scope)), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("กฎเส้นทาง PSP").WithName("ListRoutingRulesets")
            .WithSummary("รายการ PSP routing ruleset")
            .WithDescription("คืน ruleset ภายใน Admin scope แบบแบ่งหน้า กรอง merchantId และ status ได้")
            .Produces<PagedResult<RoutingRulesetView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/payments/routing-rulesets/{rulesetId:guid}", async (
            Guid rulesetId,
            Guid? merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.GetRulesetAsync(rulesetId, merchantId, PaymentsAccess(scope), ct);
            if (value is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("กฎเส้นทาง PSP").WithName("GetRoutingRuleset")
            .WithSummary("อ่าน PSP routing ruleset")
            .WithDescription("คืน ruleset พร้อมกฎเรียงตาม priority และ ETag หากไม่พบหรือนอก Admin scope -> 404")
            .Produces<RoutingRulesetView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/payments/routing-rulesets", async (
            RoutingRulesetRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.CreateRulesetAsync(new CreateRoutingRulesetIntent(
                body.MerchantId, body.Name, Inputs(body.Rules), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, value.Version);
            return Results.Created($"/api/v1/payments/routing-rulesets/{value.RulesetId:D}", value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("201"))
            .WithTags("กฎเส้นทาง PSP").WithName("CreateRoutingRulesetDraft")
            .WithSummary("สร้าง PSP routing draft")
            .WithDescription("สร้าง ruleset สถานะ draft สำหรับ merchant โดย validate method, amount range, originator, target/fallback connection และกฎ overlap")
            .Produces<RoutingRulesetView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/payments/routing-rulesets/{rulesetId:guid}", async (
            Guid rulesetId,
            RoutingRulesetRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.ReplaceRulesetAsync(new ReplaceRoutingRulesetIntent(
                rulesetId, body.MerchantId, body.Name, Inputs(body.Rules),
                VersionEtags.Require(http), PaymentsAccess(scope)), ct);
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"))
            .WithTags("กฎเส้นทาง PSP").WithName("ReplaceRoutingRulesetDraft")
            .WithSummary("แทนที่ PSP routing draft")
            .WithDescription("แทนชื่อและกฎทั้งหมดของ ruleset ที่ยังเป็น draft โดยต้องส่ง merchantId และ If-Match; กฎ overlap หรือ state ไม่ถูกต้อง -> 409")
            .Produces<RoutingRulesetView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/payments/routing-rulesets/{rulesetId:guid}", async (
            Guid rulesetId,
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            await store.DeleteRulesetAsync(rulesetId, merchantId, VersionEtags.Require(http), PaymentsAccess(scope), ct);
            return Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("204", EmitsEtag: false))
            .WithTags("กฎเส้นทาง PSP").WithName("DeleteRoutingRulesetDraft")
            .WithSummary("ลบ PSP routing draft")
            .WithDescription("ลบ ruleset ที่ยังเป็น draft โดยต้องส่ง merchantId และ If-Match; ruleset ที่ Active หรือรออนุมัติลบไม่ได้ -> 409")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/payments/routing-rulesets/{rulesetId:guid}/activation-requests", async (
            Guid rulesetId,
            MerchantStatusRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.RequestActivationAsync(new RequestRoutingActivationIntent(
                rulesetId, body.MerchantId, VersionEtags.Require(http), IdempotencyKeys.Require(http),
                http.TraceIdentifier, PaymentsAccess(scope)), ct);
            return Results.Accepted(value: value);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("202", EmitsEtag: false), new IdempotencyMutationMarker())
            .WithTags("กฎเส้นทาง PSP").WithName("RequestRoutingActivation")
            .WithSummary("ขอเปิดใช้ PSP routing ruleset")
            .WithDescription("สร้างคำขอ maker-checker เพื่อ activate ruleset ที่ validate แล้ว โดยยังไม่เปลี่ยน active routing ต้องส่ง merchantId, If-Match และ Idempotency-Key")
            .Produces<RoutingActivationResult>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
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
            return Problem(context.HttpContext, 403, "merchant_scope_forbidden");
        }
        catch (AdminPaymentsAccessDeniedException)
        {
            return Problem(context.HttpContext, 403, "merchant_scope_forbidden");
        }
        catch (RoutingOverlapException)
        {
            return Problem(context.HttpContext, 409, "routing_overlap");
        }
        catch (PspConnectionTestFailedException)
        {
            return Problem(context.HttpContext, 502, "psp_test_failed");
        }
        catch (PaymentCapabilityUnavailableException)
        {
            return Problem(context.HttpContext, 409, "payment_capability_unavailable");
        }
        catch (PaymentAuthorizationBusyException)
        {
            return Problem(context.HttpContext, 409, "payment_authorization_busy");
        }
    }

    private static IResult CapabilityResult<T>(HttpContext http, T? value) where T : class
    {
        if (value is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound);
        var version = value switch
        {
            GlobalPaymentCapabilityView global => global.Version,
            AccountPaymentCapabilityView account => account.Version,
            MerchantPaymentMethodView merchant => merchant.Version,
            MerchantUserPaymentMethodView user => user.Version,
            _ => throw new InvalidOperationException("Capability response does not expose a version."),
        };
        VersionEtags.Set(http, version);
        return Results.Ok(value);
    }

    private static IResult Problem(HttpContext http, int status, string code) => Results.Problem(
        statusCode: status,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = http.TraceIdentifier,
        });

    private static AdminMerchantAccess MerchantAccess(IAdminScope scope) => new(
        scope.Current.AdminId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static AdminPaymentsAccess PaymentsAccess(IAdminScope scope) => new(
        scope.Current.AdminId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }

    private static void EnsureMerchant(Guid bodyMerchantId, Guid routeMerchantId)
    {
        if (bodyMerchantId == Guid.Empty || bodyMerchantId != routeMerchantId)
            throw new InvalidRequestException("MerchantId must match the route.", "validation_failed");
    }

    private static IReadOnlyList<RoutingRuleInput> Inputs(IReadOnlyList<RoutingRuleRequest>? values) =>
        (values ?? []).Select(x => new RoutingRuleInput(
            x.Priority, x.Method, x.OriginatorId, ParseAmount(x.MinAmount), ParseAmount(x.MaxAmount),
            x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();

    private static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^(0|[1-9][0-9]*)(\\.[0-9]{1,4})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || !decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidRequestException("Routing amount must be a fixed decimal string.", "routing_invalid");
        return parsed;
    }
}

internal sealed record UpdateMerchantRequest(
    Guid MerchantId,
    [property: Required] string Name,
    string? Note,
    IReadOnlyList<string>? EnabledChannels,
    JsonElement? Metadata);

internal sealed record SetPaymentCapabilityRequest(bool Enabled);

internal sealed record MerchantStatusRequest(Guid MerchantId);

internal sealed record CreateOriginatorRequest(
    Guid MerchantId,
    [property: Required] string Code,
    [property: Required] string Name,
    [property: Required] string Type,
    string? SaleCode,
    Guid? LinkedApiClientId);

internal sealed record UpdateOriginatorRequest(
    Guid MerchantId,
    [property: Required] string Name,
    [property: Required] string Type,
    string? SaleCode,
    Guid? LinkedApiClientId);

internal sealed record CreatePspConnectionRequest(
    Guid MerchantId,
    [property: Required] string Psp,
    IReadOnlyList<string>? EnabledMethods,
    JsonElement? Config,
    IReadOnlyDictionary<string, string>? Secrets,
    string? PspMerchantId);

internal sealed record UpdatePspConnectionRequest(
    Guid MerchantId,
    IReadOnlyList<string>? EnabledMethods,
    JsonElement? Config,
    bool IsEnabled);

internal sealed record PspCredentialChangeRequest(
    Guid MerchantId,
    IReadOnlyDictionary<string, string>? Secrets,
    string? PspMerchantId);

internal sealed record RoutingRulesetRequest(
    Guid MerchantId,
    [property: Required] string Name,
    IReadOnlyList<RoutingRuleRequest>? Rules);

internal sealed record RoutingRuleRequest(
    int Priority,
    [property: Required] string Method,
    Guid? OriginatorId,
    string? MinAmount,
    string? MaxAmount,
    Guid TargetConnectionId,
    Guid? FallbackConnectionId,
    bool Enabled);
