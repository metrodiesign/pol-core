using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Governance.Application;
using Governance.Domain;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Mvc;
using Payments.Application.AdminControlPlane;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;

namespace Api.ControlPlane;

/// <summary>Canonical v1 Provider and payment-setting routes API-062..077. Existing payment-control routes
/// remain compatibility aliases; all mutations delegate to the Task 3 store and Governance owner.</summary>
internal static class CanonicalProviderConfigurationEndpoints
{
    public static void MapCanonicalProviderConfigurationEndpoints(this RouteGroupBuilder api)
    {
        var routes = api.AddEndpointFilter(HandleKnownErrors);
        MapProviderCatalog(routes);
        MapProviderAccounts(routes);
        MapPaymentSettings(routes);
        MapPaymentSettingRequests(routes);
    }

    private static void MapProviderCatalog(RouteGroupBuilder api)
    {
        api.MapGet("/payment-providers", (
            IPspAdapterFactory adapters) => Results.Ok(ProviderCatalog(adapters)))
            .RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("ListCanonicalPaymentProviders")
            .WithSummary("รายการ Payment Provider")
            .WithDescription("คืน catalog ของ provider และ method ที่ adapter รองรับ โดย catalog ไม่ได้แปลว่า merchant พร้อม live")
            .Produces<IReadOnlyList<CanonicalProviderView>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/payment-providers/{providerId:guid}/methods", (
            Guid providerId, IPspAdapterFactory adapters) =>
        {
            var provider = ProviderCatalog(adapters).SingleOrDefault(x => x.ProviderId == providerId);
            return provider is null ? Results.NotFound() : Results.Ok(provider.Methods);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("ListCanonicalProviderMethods")
            .WithSummary("รายการ method ของ Payment Provider")
            .WithDescription("คืน offered, adapterVerified และ enabled จาก catalog จริง")
            .Produces<IReadOnlyList<CanonicalProviderMethodView>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapProviderAccounts(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/provider-accounts", async (
            Guid merchantId,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            int page = 1,
            int limit = 25,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListConnectionsAsync(
                new PspConnectionQuery(page, limit, null, merchantId, null, null, Access(scope)), ct));
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("ListCanonicalProviderAccounts")
            .WithSummary("รายการ Provider Account ของ Merchant")
            .WithDescription("คืน configuration ภายใน Merchant scope แบบแบ่งหน้า โดยไม่คืน secret")
            .Produces<PagedResult<PspConnectionView>>();

        api.MapPost("/merchants/{merchantId:guid}/provider-accounts", async (
            Guid merchantId,
            CanonicalProviderAccountCreateRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var result = await store.CreateProviderAccountAsync(new CreatePspProviderAccountIntent(
                merchantId, body.ProviderId, body.DisplayName, body.Environment, body.Configuration,
                IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Created($"/api/v1/merchants/{merchantId:D}/provider-accounts/{result.Connection.PspConnectionId:D}",
                result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("201"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("CreateCanonicalProviderAccount")
            .WithSummary("สร้าง Provider Account configuration")
            .WithDescription("สร้าง configuration โดยไม่มี secret; account เริ่ม disabled และ credential ต้องส่งผ่าน write-only endpoint")
            .Accepts<CanonicalProviderAccountCreateRequest>("application/json")
            .Produces<PspConnectionView>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}", async (
            Guid merchantId,
            Guid providerAccountId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct);
            if (value is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, extensions: Extensions(http, "not_found"));
            VersionEtags.Set(http, value.Version);
            return Results.Ok(value);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("การตั้งค่า Provider").WithName("GetCanonicalProviderAccount")
            .WithSummary("อ่าน Provider Account")
            .WithDescription("คืน configuration, callback URL และ masked metadata โดยไม่คืน secret")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPatch("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}", async (
            Guid merchantId,
            Guid providerAccountId,
            CanonicalProviderAccountPatchRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var current = await store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct)
                ?? throw new NotFoundException("Provider account was not found.");
            var result = await store.UpdateConnectionAsync(new UpdatePspConnectionIntent(
                providerAccountId, merchantId, body.EnabledMethods ?? current.EnabledMethods,
                body.Configuration ?? current.Config, body.IsEnabled ?? current.IsEnabled,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Ok(result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("PatchCanonicalProviderAccount")
            .WithSummary("แก้ไข Provider Account")
            .WithDescription("แก้ configuration หรือ status โดยไม่รับ credential และต้องส่ง If-Match")
            .Accepts<CanonicalProviderAccountPatchRequest>("application/json")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        api.MapGet("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions", async (
            Guid merchantId,
            Guid providerAccountId,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var value = await store.ListCredentialVersionsAsync(providerAccountId, merchantId, Access(scope), ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("ListCanonicalCredentialVersions")
            .WithSummary("รายการ credential versions")
            .WithDescription("คืนเฉพาะ version, state, validity และ masked hint ไม่คืน plaintext หรือ envelope")
            .Produces<IReadOnlyList<PspCredentialVersionView>>();

        api.MapPost("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions", async (
            Guid merchantId,
            Guid providerAccountId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var body = await AdminControlEndpoints.ReadSecretBodyAsync<CanonicalCredentialVersionCreateRequest>(http, ct);
            if (body.SecretFields is null || body.SecretFields.Count == 0 || string.IsNullOrWhiteSpace(body.KeyId))
                throw new InvalidRequestException("Credential version secretFields and keyId are required.", "validation_failed");
            var current = await store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct)
                ?? throw new NotFoundException("Provider account was not found.");
            var result = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
                providerAccountId, merchantId, body.SecretFields, body.PspMerchantId,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), http.TraceIdentifier, Access(scope)), ct);
            VersionEtags.Set(http, current.Version + 1);
            return Results.Created($"/api/v1/merchants/{merchantId:D}/provider-accounts/{providerAccountId:D}/credential-versions/{result.CandidateVersionId:D}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .Accepts<CanonicalCredentialVersionCreateRequest>("application/json")
            .WithMetadata(new IfMatchMutationMarker("201"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("CreateCanonicalCredentialVersion")
            .WithSummary("สร้าง credential version แบบ write-only")
            .WithDescription("รับ secret fields เฉพาะใน request body, stage ใน vault และคืน masked metadata/approval reference โดยไม่คืน secret")
            .Produces<PspCredentialChangeResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        api.MapPost("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/connection-tests", async (
            Guid merchantId,
            Guid providerAccountId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var current = await store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct)
                ?? throw new NotFoundException("Provider account was not found.");
            var result = await store.TestConnectionAsync(new TestPspConnectionIntent(
                providerAccountId, merchantId, VersionEtags.Require(http), IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Ok(result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("TestCanonicalProviderConnection")
            .WithSummary("ทดสอบการเชื่อมต่อ Provider")
            .WithDescription("เรียก read-only adapter probe ฝั่ง server ไม่สร้าง charge และไม่คืน credential")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status502BadGateway);

        api.MapPost("/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable", async (
            Guid merchantId,
            Guid providerAccountId,
            CanonicalDisableProviderAccountRequest body,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason))
                throw new InvalidRequestException("Disable reason is required.", "reason_required");
            var current = await store.GetConnectionAsync(providerAccountId, merchantId, Access(scope), ct)
                ?? throw new NotFoundException("Provider account was not found.");
            var result = await store.UpdateConnectionAsync(new UpdatePspConnectionIntent(
                providerAccountId, merchantId, current.EnabledMethods, current.Config, false,
                VersionEtags.Require(http), IdempotencyKeys.Require(http), Access(scope)), ct);
            VersionEtags.Set(http, result.Connection.Version);
            return Results.Ok(result.Connection);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("DisableCanonicalProviderAccount")
            .WithSummary("หยุด Provider Account ฉุกเฉิน")
            .WithDescription("หยุดการเริ่ม Transaction ใหม่โดยคงข้อมูล pinned สำหรับ inquiry รายการเก่า และบังคับ reason")
            .Accepts<CanonicalDisableProviderAccountRequest>("application/json")
            .Produces<PspConnectionView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static void MapPaymentSettings(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/payment-settings", async (
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            ISimpleRoutingControlStore routing,
            CancellationToken ct) =>
        {
            var environment = await store.GetMerchantPaymentSettingsAsync(merchantId, Access(scope), ct);
            if (environment is null)
                return Results.NotFound();
            var methods = await store.ListMerchantMethodsAsync(merchantId, Access(scope), ct) ?? [];
            var simple = await routing.GetSimpleRoutingAsync(merchantId, Access(scope), ct);
            VersionEtags.Set(http, environment.Version);
            return Results.Ok(new CanonicalPaymentSettingsView(environment, methods, simple));
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("การตั้งค่า Provider").WithName("GetCanonicalPaymentSettings")
            .WithSummary("อ่าน effective payment settings")
            .WithDescription("คืน environment, effective methods, routing และ version จาก owner store เดิม")
            .Produces<CanonicalPaymentSettingsView>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapPaymentSettingRequests(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/payment-setting-requests", async (
            Guid merchantId,
            IAdminScope scope,
            IGovernanceStore governance,
            int page = 1,
            int limit = 25,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            var all = await governance.ListApprovalsAsync(new ApprovalQuery(
                1, 100, null, null, null, merchantId, null, null, GovernanceAccess(scope)), ct);
            var filtered = all.Items.Where(IsPaymentSetting).Skip((page - 1) * limit).Take(limit).ToArray();
            return Results.Ok(new PagedResult<CanonicalPaymentSettingRequestView>(
                filtered.Select(ToView).ToArray(), page, limit, all.Items.Count(IsPaymentSetting)));
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("ListCanonicalPaymentSettingRequests")
            .WithSummary("รายการคำขอ payment settings")
            .WithDescription("คืน payment setting approvals ใน Merchant scope พร้อม maker/checker/status และ correlation")
            .Produces<PagedResult<CanonicalPaymentSettingRequestView>>();

        api.MapGet("/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}", async (
            Guid merchantId,
            Guid requestId,
            IAdminScope scope,
            IGovernanceStore governance,
            CancellationToken ct) =>
        {
            var value = await governance.GetApprovalAsync(requestId, GovernanceAccess(scope), ct);
            return value is null || value.MerchantId != merchantId || !IsPaymentSetting(value)
                ? Results.NotFound()
                : Results.Ok(ToView(value));
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การตั้งค่า Provider").WithName("GetCanonicalPaymentSettingRequest")
            .WithSummary("อ่านคำขอ payment setting")
            .WithDescription("คืน proposed target, base version, maker/checker, decision และ execution outcome")
            .Produces<CanonicalPaymentSettingRequestView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/merchants/{merchantId:guid}/payment-setting-requests", async (
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IAdminPaymentsControlStore store,
            CancellationToken ct) =>
        {
            var body = await AdminControlEndpoints.ReadSecretBodyAsync<CanonicalPaymentSettingChangeRequest>(http, ct);
            if (body.BaseVersion < 1 || string.IsNullOrWhiteSpace(body.Reason))
                throw new InvalidRequestException("baseVersion and reason are required.", "validation_failed");
            var expectedVersion = VersionEtags.Require(http);
            if (body.BaseVersion != expectedVersion)
                throw new ConcurrencyConflictException("Payment setting base version is stale.");
            var access = Access(scope);
            if (body.ProviderAccountId is { } connectionId && body.SecretFields is { Count: > 0 })
            {
                var result = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
                    connectionId, merchantId, body.SecretFields, body.PspMerchantId, expectedVersion,
                    IdempotencyKeys.Require(http), http.TraceIdentifier, access), ct);
                return Results.Created($"/api/v1/merchants/{merchantId:D}/payment-setting-requests/{result.ApprovalId:D}", result.Request);
            }
            if (body.RulesetId is { } rulesetId)
            {
                var result = await store.RequestActivationAsync(new RequestRoutingActivationIntent(
                    rulesetId, merchantId, expectedVersion, IdempotencyKeys.Require(http),
                    http.TraceIdentifier, access), ct);
                return Results.Created($"/api/v1/merchants/{merchantId:D}/payment-setting-requests/{result.ApprovalId:D}", result.Request);
            }
            if (!string.IsNullOrWhiteSpace(body.Environment) && body.Connections is { Count: > 0 })
            {
                var result = await store.RequestEnvironmentChangeAsync(new RequestEnvironmentChangeIntent(
                    merchantId, body.Environment, body.OmiseWebhookRegistered,
                    body.Connections.Select(x => new EnvironmentChangeConnectionCredential(
                        x.ProviderAccountId, x.SecretFields ?? new Dictionary<string, string>(), x.PspMerchantId)).ToArray(),
                    expectedVersion, IdempotencyKeys.Require(http), http.TraceIdentifier, access), ct);
                return Results.Created($"/api/v1/merchants/{merchantId:D}/payment-setting-requests/{result.ApprovalId:D}", result.Request);
            }
            throw new InvalidRequestException("A provider account, ruleset or target environment is required.", "validation_failed");
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("201"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName("CreateCanonicalPaymentSettingRequest")
            .WithSummary("สร้างคำขอเปลี่ยน payment setting")
            .WithDescription("เก็บ base/proposed references และสร้าง Governance approval; secret fields ถูกอ่านแบบ write-only และไม่อยู่ใน response")
            .Accepts<CanonicalPaymentSettingChangeRequest>("application/json")
            .Produces<PaymentSettingRequestContract>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        MapDecision(api, "approve", ApprovalDecision.Approve, "ApproveCanonicalPaymentSettingRequest");
        MapDecision(api, "reject", ApprovalDecision.Reject, "RejectCanonicalPaymentSettingRequest");
    }

    private static void MapDecision(RouteGroupBuilder api, string segment, ApprovalDecision decision, string name)
    {
        api.MapPost($"/merchants/{{merchantId:guid}}/payment-setting-requests/{{requestId:guid}}/{segment}", async (
            Guid merchantId,
            Guid requestId,
            CanonicalPaymentSettingDecisionRequest body,
            HttpContext http,
            IAdminScope scope,
            IGovernanceStore governance,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || string.IsNullOrWhiteSpace(body.TargetVersion))
                throw new InvalidRequestException("reason and targetVersion are required.", "validation_failed");
            var existing = await governance.GetApprovalAsync(requestId, GovernanceAccess(scope), ct);
            if (existing is null || existing.MerchantId != merchantId || !IsPaymentSetting(existing))
                return Results.NotFound();
            var result = await governance.DecideAsync(new DecisionIntent(
                requestId, decision, body.Reason, VersionEtags.Require(http), body.TargetVersion,
                IdempotencyKeys.Require(http), http.TraceIdentifier, GovernanceAccess(scope)), ct);
            http.Response.Headers.ETag = $"\"v{result.Approval.Version}\"";
            return Results.Ok(ToView(result.Approval));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การตั้งค่า Provider").WithName(name)
            .WithSummary(decision == ApprovalDecision.Approve ? "อนุมัติ payment setting request" : "ปฏิเสธ payment setting request")
            .WithDescription("ใช้ Governance checker ที่ไม่ใช่ maker พร้อม If-Match, targetVersion, reason และ Idempotency-Key")
            .Produces<CanonicalPaymentSettingRequestView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static IReadOnlyList<CanonicalProviderView> ProviderCatalog(IPspAdapterFactory adapters) =>
        [Provider(adapters, PaymentCapabilityIds.TwoCTwoP, Code.TwoCTwoP, "2c2p", "2C2P"),
         Provider(adapters, PaymentCapabilityIds.Omise, Code.Omise, "omise", "Omise")];

    private static CanonicalProviderView Provider(
        IPspAdapterFactory adapters, Guid id, Code code, string wireCode, string name)
    {
        var adapter = adapters.For(code);
        var methods = new[]
        {
            (PaymentCapabilityIds.Card, PaymentMethods.Card, "Card"),
            (PaymentCapabilityIds.PromptPay, PaymentMethods.PromptPay, "PromptPay"),
            (PaymentCapabilityIds.Installment, PaymentMethods.Installment, "Installment"),
        };
        if (code == Code.Omise)
            methods = methods.Where(x => x.Item2 == PaymentMethods.Card).ToArray();
        return new CanonicalProviderView(id, wireCode, name, true, methods.Select(x =>
            new CanonicalProviderMethodView(x.Item1, x.Item2, x.Item3, true,
                adapter.SupportedMethods.Contains(x.Item2), true)).ToArray());
    }

    private static AdminPaymentsAccess Access(IAdminScope scope) => new(
        scope.Current.AdminId, scope.Current.AuthorizationVersion,
        scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static GovernanceAccess GovernanceAccess(IAdminScope scope) => new(
        scope.Current.AdminId, scope.Accessible.IsUnrestricted, scope.Accessible.Merchants,
        scope.Current.Permissions);

    private static bool IsPaymentSetting(ApprovalListItem value) =>
        value.TargetType is "psp-credential-version" or "merchant-environment" or "routing-ruleset";

    private static bool IsPaymentSetting(ApprovalDetail value) =>
        value.TargetType is "psp-credential-version" or "merchant-environment" or "routing-ruleset";

    private static CanonicalPaymentSettingRequestView ToView(ApprovalListItem value) => new(
        value.ApprovalId, value.MerchantId ?? Guid.Empty, Kind(value.TargetType), value.Status,
        value.MakerId, null, value.TargetType, value.TargetId, $"v{value.Version}", null,
        value.CreatedAt, null, value.Version, null);

    private static CanonicalPaymentSettingRequestView ToView(ApprovalDetail value) => new(
        value.ApprovalId, value.MerchantId ?? Guid.Empty, Kind(value.TargetType), value.Status,
        value.MakerId, value.CheckerId, value.TargetType, value.TargetId, value.TargetVersion,
        value.DecisionReason, value.CreatedAt, value.DecidedAt, value.Version, value.CorrelationId);

    private static string Kind(string targetType) => targetType switch
    {
        "psp-credential-version" => "credential",
        "merchant-environment" => "environment",
        "routing-ruleset" => "routing",
        _ => "unknown",
    };

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }

    private static async ValueTask<object?> HandleKnownErrors(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AdminPaymentsAccessDeniedException)
        {
            return Problem(context.HttpContext, StatusCodes.Status403Forbidden, "merchant_scope_forbidden");
        }
        catch (ConcurrencyConflictException)
        {
            return Problem(context.HttpContext, StatusCodes.Status412PreconditionFailed, "precondition_failed");
        }
        catch (PspConnectionTestFailedException)
        {
            return Problem(context.HttpContext, StatusCodes.Status502BadGateway, "psp_test_failed");
        }
        catch (GovernanceAccessDeniedException ex)
        {
            return Problem(context.HttpContext, StatusCodes.Status403Forbidden, ex.Code);
        }
        catch (AdminControlEndpoints.SecretBodyTooLargeException)
        {
            return Problem(context.HttpContext, StatusCodes.Status413PayloadTooLarge, "request_too_large");
        }
        catch (ConflictException ex)
        {
            return Problem(context.HttpContext, StatusCodes.Status409Conflict, ex.Code ?? "conflict");
        }
    }

    private static IResult Problem(HttpContext http, int status, string code) => Results.Problem(
        statusCode: status, extensions: Extensions(http, code));

    private static Dictionary<string, object?> Extensions(HttpContext http, string code) => new()
    {
        ["code"] = code,
        ["correlationId"] = http.TraceIdentifier,
    };
}

internal sealed record CanonicalProviderView(
    Guid ProviderId,
    string Code,
    string Name,
    bool Enabled,
    IReadOnlyList<CanonicalProviderMethodView> Methods);

internal sealed record CanonicalProviderMethodView(
    Guid MethodId,
    string Code,
    string Name,
    bool Offered,
    bool AdapterVerified,
    bool Enabled);

internal sealed record CanonicalProviderAccountCreateRequest(
    Guid ProviderId,
    [property: Required] string DisplayName,
    [property: Required] string Environment,
    JsonElement? Configuration);

internal sealed record CanonicalProviderAccountPatchRequest(
    IReadOnlyList<string>? EnabledMethods,
    JsonElement? Configuration,
    bool? IsEnabled);

internal sealed record CanonicalCredentialVersionCreateRequest(
    IReadOnlyDictionary<string, string>? SecretFields,
    [property: Required] string KeyId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidUntil,
    string? PspMerchantId);

internal sealed record CanonicalDisableProviderAccountRequest(
    [property: Required] string Reason);

internal sealed record CanonicalPaymentSettingsView(
    MerchantPaymentSettingsView Environment,
    IReadOnlyList<EffectivePaymentMethod> EffectiveMethods,
    SimpleRoutingView? Routing);

internal sealed record CanonicalPaymentRouteRequest(
    string MethodCode,
    Guid ProviderAccountId,
    Guid CredentialVersionId,
    int Priority,
    bool Enabled);

internal sealed record CanonicalPaymentSettingConnectionRequest(
    Guid ProviderAccountId,
    IReadOnlyDictionary<string, string>? SecretFields,
    string? PspMerchantId);

internal sealed record CanonicalPaymentSettingChangeRequest(
    long BaseVersion,
    [property: Required] string Environment,
    IReadOnlyList<CanonicalPaymentRouteRequest>? Routes,
    [property: Required] string Reason,
    Guid? ProviderAccountId,
    IReadOnlyDictionary<string, string>? SecretFields,
    string? PspMerchantId,
    IReadOnlyList<CanonicalPaymentSettingConnectionRequest>? Connections,
    bool OmiseWebhookRegistered,
    Guid? RulesetId);

internal sealed record CanonicalPaymentSettingDecisionRequest(
    [property: Required] string Reason,
    [property: Required] string TargetVersion);

internal sealed record CanonicalPaymentSettingRequestView(
    Guid RequestId,
    Guid MerchantId,
    string Kind,
    string Status,
    Guid MakerId,
    Guid? CheckerId,
    string TargetType,
    string TargetId,
    string TargetVersion,
    string? Reason,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    long Version,
    string? CorrelationId);
