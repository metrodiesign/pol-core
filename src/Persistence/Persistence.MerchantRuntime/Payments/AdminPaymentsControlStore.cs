using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime.Payments.Capabilities;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class AdminPaymentsControlStore(
    MerchantRuntimeDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    IVaultSecretStore vault,
    IPspSecretEnvelopeFactory envelopeFactory,
    IPspAdapterFactory adapterFactory,
    PaymentAuthorizationSqlLockManager? authorizationLocks = null,
    IEffectivePaymentCapabilityResolver? effectiveResolver = null)
    : IAdminPaymentsControlStore, IAccountPaymentCapabilityControlStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private PaymentAuthorizationSqlLockManager AuthorizationLocks { get; } =
        authorizationLocks ?? new PaymentAuthorizationSqlLockManager(db);
    private IEffectivePaymentCapabilityResolver EffectiveResolver => effectiveResolver
        ?? new EffectivePaymentCapabilityResolver(db, unitOfWork, AuthorizationLocks, adapterFactory);

    public async Task<PagedResult<PspConnectionView>> ListConnectionsAsync(
        PspConnectionQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } selected)
            EnsureAccess(query.Access, selected);
        var source = db.PspConnections.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Psp))
        {
            var psp = ParsePsp(query.Psp);
            source = source.Where(x => x.Psp == psp);
        }
        if (!string.IsNullOrWhiteSpace(query.Health))
        {
            var health = ParseHealth(query.Health);
            source = source.Where(x => x.Health == health);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.Id.ToString().Contains(search));
        }

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.OrderBy(x => x.MerchantId)
            .ThenBy(x => x.Psp).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        var items = new List<PspConnectionView>(rows.Count);
        foreach (var row in rows)
            items.Add(await ProjectConnectionAsync(row, cancellationToken));
        return new PagedResult<PspConnectionView>(items, query.Page, query.Limit, total);
    }

    public async Task<PspConnectionView?> GetConnectionAsync(
        Guid connectionId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == connectionId && (merchantId == null || x.MerchantId == merchantId), ct),
            cancellationToken);
        return row is null || !access.Allows(row.MerchantId)
            ? null
            : await ProjectConnectionAsync(row, cancellationToken);
    }

    public async Task<AccountPaymentCapabilityView?> GetAccountMethodAsync(
        Guid connectionId, string method, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        var connection = await FindConnectionForAccessAsync(connectionId, access, tracking: false, cancellationToken);
        if (connection?.PaymentProviderId is null)
            return null;
        var provider = await LoadProviderAsync(connection.Psp, cancellationToken);
        var catalog = await LoadProviderMethodAsync(provider, code, cancellationToken);
        if (catalog is null)
            return null;
        var row = await PlatformReadGuard.ReadAsync(ct => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.PspConnectionId == connectionId && x.MerchantId == connection.MerchantId
                && x.PaymentMethodId == catalog.PaymentMethodId, ct), cancellationToken);
        return AccountMethodView(connection, provider, catalog, row);
    }

    public async Task<AccountPaymentCapabilityView?> GetAccountMethodOptionAsync(
        Guid connectionId, string method, string option, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        var optionCode = NormalizeOption(option);
        var connection = await FindConnectionForAccessAsync(connectionId, access, tracking: false, cancellationToken);
        if (connection?.PaymentProviderId is null)
            return null;
        var provider = await LoadProviderAsync(connection.Psp, cancellationToken);
        var catalog = await LoadProviderMethodAsync(provider, code, cancellationToken);
        if (catalog?.PaymentProviderMethodId is null)
            return null;
        var accountMethod = await PlatformReadGuard.ReadAsync(ct => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.PspConnectionId == connectionId && x.MerchantId == connection.MerchantId
                && x.PaymentMethodId == catalog.PaymentMethodId, ct), cancellationToken);
        if (accountMethod is null)
            return null;
        var optionCatalog = await LoadProviderMethodOptionAsync(catalog, optionCode, cancellationToken);
        if (optionCatalog is null)
            return null;
        var row = await PlatformReadGuard.ReadAsync(ct => db.MerchantProviderAccountMethodOptions
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantProviderAccountMethodId == accountMethod.Id
                && x.PaymentMethodOptionId == optionCatalog.PaymentMethodOptionId, ct), cancellationToken);
        return AccountOptionView(connection, provider, catalog, optionCatalog, row);
    }

    public Task<PaymentCapabilityMutationResult<AccountPaymentCapabilityView>> SetAccountMethodAsync(
        SetAccountPaymentCapabilityIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var method = NormalizeMethod(intent.Method);
            var snapshot = await FindConnectionForAccessAsync(
                intent.PspConnectionId, intent.Access, tracking: false, ct)
                ?? throw new NotFoundException("PSP connection was not found.");
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(snapshot.MerchantId, ct);
            var intentHash = Hash(new
            {
                intent.PspConnectionId, method, intent.Enabled, intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(snapshot.MerchantId, intent.Access.ActorId,
                "payment.account-method.set", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PaymentCapabilityMutationResult<AccountPaymentCapabilityView>(
                    Replay<AccountPaymentCapabilityView>(prior), true);

            var connection = await LoadConnectionAsync(intent.PspConnectionId, snapshot.MerchantId, ct);
            var provider = await LoadProviderAsync(connection.Psp, ct);
            EnsureProviderBinding(connection, provider);
            var catalog = await LoadProviderMethodAsync(provider, method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            var row = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
                .IgnoreQueryFilters().SingleOrDefaultAsync(x =>
                    x.PspConnectionId == connection.Id && x.MerchantId == connection.MerchantId
                    && x.PaymentMethodId == catalog.PaymentMethodId, token), ct);
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            EnsureAccountMethodCanEnable(connection, provider, catalog, intent.Enabled);
            if (row is null)
            {
                if (intent.Enabled)
                {
                    row = MerchantProviderAccountMethod.Create(
                        connection.MerchantId, connection.Id, provider.PaymentProviderId,
                        catalog.PaymentProviderMethodId!.Value, catalog.PaymentMethodId,
                        intent.Access.ActorId, clock.UtcNow);
                    db.MerchantProviderAccountMethods.Add(row);
                }
            }
            else
            {
                row.SetEnabled(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }

            await ProjectAccountMethodsAsync(connection, method, intent.Enabled, ct);
            var view = AccountMethodView(connection, provider, catalog, row);
            var operation = BeginOperation(snapshot.MerchantId, intent.Access.ActorId,
                "payment.account-method.set", intent.IdempotencyKey, intentHash);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PaymentCapabilityMutationResult<AccountPaymentCapabilityView>(view, false);
        }, cancellationToken);

    public Task<PaymentCapabilityMutationResult<AccountPaymentCapabilityView>> SetAccountMethodOptionAsync(
        SetAccountPaymentCapabilityIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var method = NormalizeMethod(intent.Method);
            var option = NormalizeOption(intent.Option ?? string.Empty);
            var snapshot = await FindConnectionForAccessAsync(
                intent.PspConnectionId, intent.Access, tracking: false, ct)
                ?? throw new NotFoundException("PSP connection was not found.");
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(snapshot.MerchantId, ct);
            var intentHash = Hash(new
            {
                intent.PspConnectionId, method, option, intent.Enabled, intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(snapshot.MerchantId, intent.Access.ActorId,
                "payment.account-method-option.set", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PaymentCapabilityMutationResult<AccountPaymentCapabilityView>(
                    Replay<AccountPaymentCapabilityView>(prior), true);

            var connection = await LoadConnectionAsync(intent.PspConnectionId, snapshot.MerchantId, ct);
            var provider = await LoadProviderAsync(connection.Psp, ct);
            EnsureProviderBinding(connection, provider);
            var catalog = await LoadProviderMethodAsync(provider, method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            var accountMethod = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
                .IgnoreQueryFilters().SingleOrDefaultAsync(x =>
                    x.PspConnectionId == connection.Id && x.MerchantId == connection.MerchantId
                    && x.PaymentMethodId == catalog.PaymentMethodId, token), ct)
                ?? throw new PaymentCapabilityUnavailableException("Account method is not configured.");
            var optionCatalog = await LoadProviderMethodOptionAsync(catalog, option, ct)
                ?? throw new NotFoundException("Payment method option was not found.");
            var row = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethodOptions
                .IgnoreQueryFilters().SingleOrDefaultAsync(x =>
                    x.MerchantProviderAccountMethodId == accountMethod.Id
                    && x.PaymentMethodOptionId == optionCatalog.PaymentMethodOptionId, token), ct);
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            if (intent.Enabled && (!connection.IsEnabled || !provider.IsEnabled || !catalog.MethodIsActive
                || !catalog.ProviderMethodIsActive || !accountMethod.IsEnabled
                || !optionCatalog.ProviderMethodOptionIsActive
                || !adapterFactory.For(connection.Psp).SupportedMethods.Contains(method)))
                throw new PaymentCapabilityUnavailableException("Account option has an inactive parent capability.");
            if (row is null)
            {
                if (intent.Enabled)
                {
                    row = MerchantProviderAccountMethodOption.Create(
                        connection.MerchantId, accountMethod.Id, connection.Id, provider.PaymentProviderId,
                        catalog.PaymentProviderMethodId!.Value, catalog.PaymentMethodId,
                        optionCatalog.PaymentProviderMethodOptionId!.Value,
                        optionCatalog.PaymentMethodOptionId, intent.Access.ActorId, clock.UtcNow);
                    db.MerchantProviderAccountMethodOptions.Add(row);
                }
            }
            else
            {
                row.SetEnabled(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }

            var view = AccountOptionView(connection, provider, catalog, optionCatalog, row);
            var operation = BeginOperation(snapshot.MerchantId, intent.Access.ActorId,
                "payment.account-method-option.set", intent.IdempotencyKey, intentHash);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PaymentCapabilityMutationResult<AccountPaymentCapabilityView>(view, false);
        }, cancellationToken);

    public async Task<IReadOnlyList<EffectivePaymentMethod>?> ListMerchantMethodsAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        if (!await MerchantExistsForAccessAsync(merchantId, access, cancellationToken))
            return null;
        return await EffectiveResolver.ListMethodsAsync(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.PlatformAdmin, null), cancellationToken);
    }

    public async Task<MerchantPaymentMethodView?> GetMerchantMethodAsync(
        Guid merchantId, string method, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        if (!await MerchantExistsForAccessAsync(merchantId, access, cancellationToken)
            || await LoadPaymentMethodStateAsync(code, cancellationToken) is null)
            return null;
        var row = await PlatformReadGuard.ReadAsync(ct => db.MerchantPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantId == merchantId && x.PaymentMethodId == MethodId(code), ct), cancellationToken);
        var effective = (await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.PlatformAdmin, null), code, null),
            cancellationToken)).Allowed;
        return MerchantPolicyView(merchantId, code, row, effective);
    }

    public Task<PaymentCapabilityMutationResult<MerchantPaymentMethodView>> SetMerchantMethodAsync(
        SetMerchantPaymentCapabilityIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var method = NormalizeMethod(intent.Method);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            if (!await MerchantExistsForAccessAsync(intent.MerchantId, intent.Access, ct))
                throw new NotFoundException("Merchant was not found.");
            var methodState = await LoadPaymentMethodStateAsync(method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            var intentHash = Hash(new
            {
                intent.MerchantId, method, intent.Enabled, intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "payment.merchant-method.set", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PaymentCapabilityMutationResult<MerchantPaymentMethodView>(
                    Replay<MerchantPaymentMethodView>(prior), true);

            var row = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
                .IgnoreQueryFilters().SingleOrDefaultAsync(x => x.MerchantId == intent.MerchantId
                    && x.PaymentMethodId == methodState.PaymentMethodId, token), ct);
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            if (intent.Enabled && (!methodState.IsActive
                || !await HasQualifyingAccountAsync(intent.MerchantId, method, methodState.PaymentMethodId, ct)))
                throw new PaymentCapabilityUnavailableException(
                    "Merchant method requires an active qualifying provider account method.");

            if (row is null)
            {
                if (intent.Enabled)
                {
                    row = MerchantPaymentMethod.Create(intent.MerchantId, methodState.PaymentMethodId,
                        true, intent.Access.ActorId, clock.UtcNow);
                    db.MerchantPaymentMethods.Add(row);
                }
            }
            else
            {
                row.SetEnabled(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }

            await ProjectMerchantMethodsAsync(
                intent.MerchantId, method, intent.Enabled, ct);
            await unitOfWork.SaveChangesAsync(ct);
            var effective = (await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(intent.MerchantId, PaymentAudience.PlatformAdmin, null), method, null),
                ct)).Allowed;
            var view = MerchantPolicyView(intent.MerchantId, method, row, effective);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "payment.merchant-method.set", intent.IdempotencyKey, intentHash);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), $"{intent.MerchantId:D}:{method}");
            await unitOfWork.SaveChangesAsync(ct);
            return new PaymentCapabilityMutationResult<MerchantPaymentMethodView>(view, false);
        }, cancellationToken);

    public async Task<IReadOnlyList<MerchantUserPaymentMethodView>?> ListMerchantUserMethodsAsync(
        Guid merchantId, Guid merchantUserId, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        if (!await MerchantUserExistsForAccessAsync(merchantId, merchantUserId, access, cancellationToken))
            return null;
        var rows = await PlatformReadGuard.ReadAsync(ct => db.MerchantUserPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId
                && x.MerchantUserId == merchantUserId).OrderBy(x => x.PaymentMethodId).ToListAsync(ct),
            cancellationToken);
        var result = new List<MerchantUserPaymentMethodView>(rows.Count);
        foreach (var row in rows)
        {
            var method = MethodCode(row.PaymentMethodId)
                ?? throw new InvalidOperationException("User policy references an unknown payment method.");
            var effective = (await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(merchantId, PaymentAudience.User, merchantUserId),
                method, null), cancellationToken)).Allowed;
            result.Add(UserPolicyView(merchantUserId, merchantId, method, row, effective));
        }
        return result.OrderBy(x => x.Method, StringComparer.Ordinal).ToList();
    }

    public async Task<MerchantUserPaymentMethodView?> GetMerchantUserMethodAsync(
        Guid merchantId, Guid merchantUserId, string method, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        if (!await MerchantUserExistsForAccessAsync(merchantId, merchantUserId, access, cancellationToken)
            || await LoadPaymentMethodStateAsync(code, cancellationToken) is null)
            return null;
        var row = await PlatformReadGuard.ReadAsync(ct => db.MerchantUserPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.MerchantId == merchantId
                && x.MerchantUserId == merchantUserId && x.PaymentMethodId == MethodId(code), ct),
            cancellationToken);
        var effective = (await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.User, merchantUserId), code, null),
            cancellationToken)).Allowed;
        return UserPolicyView(merchantUserId, merchantId, code, row, effective);
    }

    public Task<PaymentCapabilityMutationResult<MerchantUserPaymentMethodView>> SetMerchantUserMethodAsync(
        SetMerchantUserPaymentCapabilityIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var method = NormalizeMethod(intent.Method);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            if (!await MerchantUserExistsForAccessAsync(
                    intent.MerchantId, intent.MerchantUserId, intent.Access, ct))
                throw new NotFoundException("Merchant user was not found.");
            var methodState = await LoadPaymentMethodStateAsync(method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            var intentHash = Hash(new
            {
                intent.MerchantId, intent.MerchantUserId, method, intent.Enabled, intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "payment.merchant-user-method.set", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PaymentCapabilityMutationResult<MerchantUserPaymentMethodView>(
                    Replay<MerchantUserPaymentMethodView>(prior), true);

            var row = await PlatformReadGuard.ReadAsync(token => db.MerchantUserPaymentMethods
                .IgnoreQueryFilters().SingleOrDefaultAsync(x => x.MerchantId == intent.MerchantId
                    && x.MerchantUserId == intent.MerchantUserId
                    && x.PaymentMethodId == methodState.PaymentMethodId, token), ct);
            EnsureVersion(row?.Version ?? 0, intent.ExpectedVersion);
            if (intent.Enabled && !await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
                    .IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.MerchantId == intent.MerchantId
                        && x.PaymentMethodId == methodState.PaymentMethodId && x.IsEnabled, token), ct))
                throw new PaymentCapabilityUnavailableException(
                    "User method requires an enabled Merchant payment method.");

            if (row is null)
            {
                if (intent.Enabled)
                {
                    row = MerchantUserPaymentMethod.Create(intent.MerchantUserId, intent.MerchantId,
                        methodState.PaymentMethodId, true, intent.Access.ActorId, clock.UtcNow);
                    db.MerchantUserPaymentMethods.Add(row);
                }
            }
            else
            {
                row.SetEnabled(intent.Enabled, intent.Access.ActorId, clock.UtcNow);
            }

            await unitOfWork.SaveChangesAsync(ct);
            var effective = (await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(intent.MerchantId, PaymentAudience.User,
                    intent.MerchantUserId), method, null), ct)).Allowed;
            var view = UserPolicyView(intent.MerchantUserId, intent.MerchantId, method, row, effective);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "payment.merchant-user-method.set", intent.IdempotencyKey, intentHash);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json),
                $"{intent.MerchantUserId:D}:{method}");
            await unitOfWork.SaveChangesAsync(ct);
            return new PaymentCapabilityMutationResult<MerchantUserPaymentMethodView>(view, false);
        }, cancellationToken);

    public async Task<UserPaymentMethodResolutionView?> ResolveMerchantUserMethodAsync(
        Guid merchantId, Guid merchantUserId, string method, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        if (!await MerchantUserExistsForAccessAsync(merchantId, merchantUserId, access, cancellationToken)
            || await LoadPaymentMethodStateAsync(code, cancellationToken) is null)
            return null;
        var decision = await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.User, merchantUserId), code, null),
            cancellationToken);
        return new UserPaymentMethodResolutionView(code, decision.Allowed ? "allowed" : "denied");
    }

    public async Task<IReadOnlyList<EffectivePaymentOption>?> ResolveMerchantUserOptionsAsync(
        Guid merchantId, Guid merchantUserId, string method, string provider,
        AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var code = NormalizeMethod(method);
        if (!await MerchantUserExistsForAccessAsync(merchantId, merchantUserId, access, cancellationToken)
            || await LoadPaymentMethodStateAsync(code, cancellationToken) is null)
            return null;
        return await EffectiveResolver.ResolveOptionsAsync(new ResolvePaymentMethod(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.User, merchantUserId),
            code, provider), cancellationToken);
    }

    public Task<PspConnectionMutationResult> CreateConnectionAsync(
        CreatePspConnectionIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            await EnsureMerchantExistsAsync(intent.MerchantId, ct);
            var psp = ParsePsp(intent.Psp);
            var methods = ValidateMethods(psp, intent.EnabledMethods);
            var provider = await LoadProviderAsync(psp, ct);
            ValidateConfig(intent.Config);
            var envelope = envelopeFactory.Build(new PspSecretInput(psp, intent.Secrets, intent.PspMerchantId));
            var intentHash = Hash(new
            {
                intent.MerchantId,
                psp = psp.ToCode(),
                methods,
                config = intent.Config?.GetRawText(),
                intent.PspMerchantId,
                secretFingerprint = SecretIntentFingerprint(envelope.EnvelopeJson),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.create", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PspConnectionMutationResult(await ReplayConnectionAsync(prior, ct), true);

            var connection = Connection.Create(intent.MerchantId, psp, string.Join(',', methods),
                $"psp-connection-{Guid.CreateVersion7():N}", clock.UtcNow,
                ConnectionMetadata(intent.PspMerchantId, intent.Config, envelope.Hints));
            connection.BindPaymentProvider(provider.PaymentProviderId);
            var secretName = $"psp-connection-{connection.Id:N}";
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json), null, ct);
            await vault.ActivateVersionAsync(intent.MerchantId, candidate, ct);
            connection.SetInitialSecretVersion(candidate);
            db.PspConnections.Add(connection);
            await SyncAccountMethodsAsync(connection, provider, methods, intent.Access.ActorId, ct);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.create", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Succeed(201, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PspConnectionMutationResult(view, false);
        }, cancellationToken);

    public Task<PspConnectionMutationResult> UpdateConnectionAsync(
        UpdatePspConnectionIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var connection = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            var methods = ValidateMethods(connection.Psp, intent.EnabledMethods);
            ValidateConfig(intent.Config);
            var intentHash = Hash(new
            {
                intent.ConnectionId,
                intent.MerchantId,
                methods,
                config = intent.Config?.GetRawText(),
                intent.IsEnabled,
                intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.update", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PspConnectionMutationResult(await ReplayConnectionAsync(prior, ct), true);
            EnsureVersion(connection.Version, intent.ExpectedVersion);

            var provider = await LoadProviderAsync(connection.Psp, ct);
            connection.BindPaymentProvider(provider.PaymentProviderId);
            var metadata = ReadMetadata(connection.Metadata);
            connection.Update(string.Join(',', methods),
                ConnectionMetadata(metadata.PspMerchantId, intent.Config, metadata.Hints), intent.IsEnabled);
            await SyncAccountMethodsAsync(connection, provider, methods, intent.Access.ActorId, ct);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.update", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), connection.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new PspConnectionMutationResult(view, false);
        }, cancellationToken);

    public async Task<PspConnectionMutationResult> TestConnectionAsync(
        TestPspConnectionIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var intentHash = Hash(new { intent.ConnectionId, intent.MerchantId, intent.ExpectedVersion });
        var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
            "psp.test", intent.IdempotencyKey, intentHash, cancellationToken);
        if (prior is not null)
        {
            var replay = await ReplayConnectionAsync(prior, cancellationToken);
            if (prior.HttpStatus == 502)
                throw new PspConnectionTestFailedException(replay);
            return new PspConnectionMutationResult(replay, true);
        }

        var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == intent.ConnectionId && x.MerchantId == intent.MerchantId, ct), cancellationToken)
            ?? throw new NotFoundException("PSP connection was not found.");
        EnsureVersion(snapshot.Version, intent.ExpectedVersion);

        var succeeded = false;
        try
        {
            var secret = snapshot.ActiveSecretVersionId is { } versionId
                ? await vault.ReadVersionForServerAsync(intent.MerchantId, versionId, cancellationToken)
                : await vault.RevealAsync(intent.MerchantId, snapshot.SecretRefName, cancellationToken);
            await adapterFactory.For(snapshot.Psp).TestConnectionAsync(secret, cancellationToken);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
        }

        var current = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, cancellationToken);
        EnsureVersion(current.Version, intent.ExpectedVersion);
        current.RecordTest(succeeded, succeeded ? "authenticated" : "probe_failed", clock.UtcNow);
        var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
            "psp.test", intent.IdempotencyKey, intentHash);
        var view = await ProjectConnectionAsync(current, cancellationToken);
        operation.Succeed(succeeded ? 200 : 502, JsonSerializer.Serialize(view, Json), current.Id.ToString("D"));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (!succeeded)
            throw new PspConnectionTestFailedException(view);
        return new PspConnectionMutationResult(view, false);
    }

    public Task<PspCredentialChangeResult> RequestCredentialChangeAsync(
        RequestPspCredentialChangeIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            var connection = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            var envelope = envelopeFactory.Build(new PspSecretInput(connection.Psp, intent.Secrets, intent.PspMerchantId));
            var intentHash = Hash(new
            {
                intent.ConnectionId,
                intent.MerchantId,
                intent.PspMerchantId,
                intent.ExpectedVersion,
                secretFingerprint = SecretIntentFingerprint(envelope.EnvelopeJson),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-change", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<PspCredentialChangeResult>(prior);
            EnsureVersion(connection.Version, intent.ExpectedVersion);

            var secretName = $"psp-connection-{connection.Id:N}";
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json),
                clock.UtcNow.AddHours(24), ct);
            var approvalId = Guid.CreateVersion7();
            connection.StageSecretVersion(candidate, approvalId);
            var targetVersion = $"v{connection.Version}";
            var result = new PspCredentialChangeResult(approvalId, candidate, "pending", false);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-change", intent.IdempotencyKey, intentHash);
            operation.Succeed(202, JsonSerializer.Serialize(result, Json), approvalId.ToString("D"));
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "psp.credential.change", "settings.manage", intent.Access.ActorId,
                "psp-credential-version", connection.Id.ToString("D"), targetVersion,
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public async Task<PagedResult<RoutingRulesetView>> ListRulesetsAsync(
        RoutingRulesetQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } selected)
            EnsureAccess(query.Access, selected);
        var source = db.RoutingRulesets.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseRulesetStatus(query.Status);
            source = source.Where(x => x.Status == status);
        }
        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.Include(x => x.Rules)
            .OrderBy(x => x.MerchantId).ThenByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        return new PagedResult<RoutingRulesetView>(
            rows.Select(ProjectRuleset).ToList(), query.Page, query.Limit, total);
    }

    public async Task<RoutingRulesetView?> GetRulesetAsync(
        Guid rulesetId, Guid? merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.RoutingRulesets.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && (merchantId == null || x.MerchantId == merchantId), ct),
            cancellationToken);
        return row is null || !access.Allows(row.MerchantId) ? null : ProjectRuleset(row);
    }

    public async Task<RoutingRulesetView> CreateRulesetAsync(
        CreateRoutingRulesetIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        await ValidateRulesAsync(intent.MerchantId, intent.Rules, cancellationToken);
        var entity = RoutingRuleset.Create(intent.MerchantId, intent.Name, Specs(intent.Rules), clock.UtcNow);
        db.RoutingRulesets.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProjectRuleset(entity);
    }

    public async Task<RoutingRulesetView> ReplaceRulesetAsync(
        ReplaceRoutingRulesetIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, cancellationToken);
        EnsureVersion(entity.Version, intent.ExpectedVersion);
        await ValidateRulesAsync(intent.MerchantId, intent.Rules, cancellationToken);
        entity.Replace(intent.Name, Specs(intent.Rules), clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ProjectRuleset(entity);
    }

    public async Task DeleteRulesetAsync(
        Guid rulesetId, Guid merchantId, long expectedVersion, AdminPaymentsAccess access,
        CancellationToken cancellationToken)
    {
        EnsureAccess(access, merchantId);
        var entity = await LoadRulesetAsync(rulesetId, merchantId, cancellationToken);
        EnsureVersion(entity.Version, expectedVersion);
        if (entity.Status != RoutingRulesetStatus.Draft)
            throw new InvalidOperationException("Only draft routing rulesets can be deleted.");
        db.RoutingRulesets.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public Task<RoutingActivationResult> RequestActivationAsync(
        RequestRoutingActivationIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, ct);
            EnsureVersion(entity.Version, intent.ExpectedVersion);
            var input = entity.Rules.Select(x => new RoutingRuleInput(
                x.Priority, x.Method, x.OriginatorId, x.MinAmount, x.MaxAmount,
                x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();
            await ValidateRulesAsync(intent.MerchantId, input, ct);
            var intentHash = Hash(new
            {
                intent.RulesetId,
                intent.MerchantId,
                intent.ExpectedVersion,
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "routing.activation", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<RoutingActivationResult>(prior);

            var approvalId = Guid.CreateVersion7();
            entity.RequestActivation(approvalId, clock.UtcNow);
            var result = new RoutingActivationResult(approvalId, ProjectRuleset(entity), false);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "routing.activation", intent.IdempotencyKey, intentHash);
            operation.Succeed(202, JsonSerializer.Serialize(result, Json), approvalId.ToString("D"));
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "routing.activate", "settings.manage", intent.Access.ActorId,
                "routing-ruleset", entity.Id.ToString("D"), $"v{entity.Version}",
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    private async Task ValidateRulesAsync(Guid merchantId, IReadOnlyList<RoutingRuleInput> rules, CancellationToken ct)
    {
        var specs = Specs(rules);
        RoutingRuleset.Validate(specs);
        var connectionIds = specs.Select(x => x.TargetConnectionId)
            .Concat(specs.Where(x => x.FallbackConnectionId.HasValue).Select(x => x.FallbackConnectionId!.Value))
            .ToHashSet();
        var connections = await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().Where(x => x.MerchantId == merchantId && connectionIds.Contains(x.Id))
            .ToListAsync(token), ct);
        if (connections.Count != connectionIds.Count)
            throw new InvalidRequestException("Routing references an unknown PSP connection.", "routing_invalid");

        foreach (var rule in specs.Where(x => x.Enabled))
        {
            ValidateEligible(connections.Single(x => x.Id == rule.TargetConnectionId), rule.Method);
            if (rule.FallbackConnectionId is { } fallback)
                ValidateEligible(connections.Single(x => x.Id == fallback), rule.Method);
        }

        var originatorIds = specs.Where(x => x.OriginatorId.HasValue).Select(x => x.OriginatorId!.Value).ToHashSet();
        if (originatorIds.Count > 0)
        {
            var count = await PlatformReadGuard.ReadAsync(token => db.Originators.IgnoreQueryFilters()
                .AsNoTracking().CountAsync(
                    x => x.MerchantId == merchantId && originatorIds.Contains(x.Id), token), ct);
            if (count != originatorIds.Count)
                throw new InvalidRequestException("Routing references an unknown originator.", "routing_invalid");
        }
    }

    private void ValidateEligible(Connection connection, string method)
    {
        if (!connection.IsEnabled)
            throw new InvalidRequestException("Routing references a disabled PSP connection.", "routing_invalid");
        var adapter = adapterFactory.For(connection.Psp);
        if (method == "any")
        {
            if (!connection.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(adapter.SupportedMethods.Contains))
                throw new InvalidRequestException("Routing connection has no eligible method.", "routing_invalid");
            return;
        }
        if (!connection.Supports(method) || !adapter.SupportedMethods.Contains(method))
            throw new InvalidRequestException("Routing connection does not support the selected method.", "routing_invalid");
    }

    private async Task<PspConnectionView> ProjectConnectionAsync(Connection x, CancellationToken ct)
    {
        var metadata = ReadMetadata(x.Metadata);
        var masked = new Dictionary<string, string>(metadata.Hints, StringComparer.Ordinal);
        if (x.ActiveSecretVersionId is { } versionId)
        {
            var encoded = await vault.MaskedVersionAsync(x.MerchantId, versionId, ct);
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var hints = JsonSerializer.Deserialize<Dictionary<string, string>>(encoded, Json) ?? [];
                masked = hints.ToDictionary(k => k.Key, v => Mask(v.Value), StringComparer.Ordinal);
            }
            else
            {
                foreach (var key in masked.Keys.ToList())
                    masked[key] = Mask(masked[key]);
            }
        }
        else
        {
            foreach (var key in masked.Keys.ToList())
                masked[key] = Mask(masked[key]);
        }
        var adapter = adapterFactory.For(x.Psp);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["test"] = true,
            ["paymentRedirect"] = adapter.SupportedMethods.Count > 0,
            ["capture"] = false,
            ["void"] = false,
            ["refund"] = false,
            ["receipt"] = false,
        };
        return new PspConnectionView(
            x.Id, x.MerchantId, x.Psp.ToCode(),
            x.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            metadata.Config, masked, x.IsEnabled, HealthCode(x.Health), x.LastTestedAt,
            x.LastTestResult, capabilities, x.PendingApprovalId is not null, x.CreatedAt, x.Version);
    }

    private async Task<PspConnectionView> ReplayConnectionAsync(AdminOperationRecord record, CancellationToken ct)
    {
        var stored = Replay<PspConnectionView>(record);
        return await GetConnectionAsync(stored.PspConnectionId, stored.MerchantId,
            new AdminPaymentsAccess(record.ActorId, true, new HashSet<Guid>()), ct) ?? stored;
    }

    private AdminOperationRecord BeginOperation(Guid merchantId, Guid actorId, string operation, string key, string hash)
    {
        var record = AdminOperationRecord.Create(merchantId, actorId, operation, key, hash, clock.UtcNow);
        db.AdminOperationRecords.Add(record);
        return record;
    }

    private async Task<AdminOperationRecord?> FindOperationAsync(
        Guid merchantId, Guid actorId, string operation, string key, string hash, CancellationToken ct)
    {
        ValidateKey(key);
        var record = await PlatformReadGuard.ReadAsync(token => db.AdminOperationRecords.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantId == merchantId && x.ActorId == actorId && x.Operation == operation
                && x.IdempotencyKey == key, token), ct);
        if (record is null)
            return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.IntentHash), Encoding.ASCII.GetBytes(hash)))
            throw new ConflictException("Idempotency key was reused with a different intent.", "idempotency_key_reused");
        if (record.State != AdminOperationState.Succeeded || record.Result is null)
            throw new ConflictException("The operation is still in progress or has an unknown outcome.", "operation_in_progress");
        return record;
    }

    private void EnqueueApproval(ApprovalRequested message)
    {
        db.OutboxMessages.Add(OutboxMessage.Create(
            message.EventId, message.MerchantId!.Value, ApprovalRequested.EventType,
            ApprovalRequested.SchemaVersion, JsonSerializer.Serialize(message, Json), message.OccurredAt));
    }

    private async Task<Connection> LoadConnectionAsync(Guid connectionId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.Id == connectionId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("PSP connection was not found.");

    private async Task<RoutingRuleset> LoadRulesetAsync(Guid rulesetId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets.IgnoreQueryFilters().Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("Routing ruleset was not found.");

    private async Task EnsureMerchantExistsAsync(Guid merchantId, CancellationToken ct)
    {
        if (!await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
                .AnyAsync(x => x.Id == merchantId, token), ct))
            throw new NotFoundException("Merchant was not found.");
    }

    private Task<bool> MerchantExistsForAccessAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken ct)
    {
        if (merchantId == Guid.Empty || !access.Allows(merchantId))
            return Task.FromResult(false);
        return PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(x => x.Id == merchantId, token), ct);
    }

    private async Task<bool> MerchantUserExistsForAccessAsync(
        Guid merchantId, Guid merchantUserId, AdminPaymentsAccess access, CancellationToken ct)
    {
        if (merchantId == Guid.Empty || merchantUserId == Guid.Empty || !access.Allows(merchantId))
            return false;
        if (!await MerchantExistsForAccessAsync(merchantId, access, ct))
            return false;
        var query = db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM [merch].[Users]
            WHERE [Id] = {merchantUserId} AND [MerchantId] = {merchantId}
              AND [Status] IN ({(int)UserStatus.Active}, {(int)UserStatus.Suspended})
            """);
        var count = await PlatformReadGuard.ReadAsync(token => query.SingleAsync(token), ct);
        return count == 1;
    }

    private async Task<PaymentMethodStateRow?> LoadPaymentMethodStateAsync(
        string method, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return new PaymentMethodStateRow { PaymentMethodId = MethodId(method), IsActive = true };
        var query = db.Database.SqlQuery<PaymentMethodStateRow>($"""
            SELECT [Id] AS [PaymentMethodId], [IsActive]
            FROM [cfg].[PaymentMethods]
            WHERE [Code] = {method}
            """);
        return await PlatformReadGuard.ReadAsync(token => query.SingleOrDefaultAsync(token), ct);
    }

    private async Task<bool> HasQualifyingAccountAsync(
        Guid merchantId, string method, Guid paymentMethodId, CancellationToken ct)
    {
        var accountMethods = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId
                && x.PaymentMethodId == paymentMethodId && x.IsEnabled).ToListAsync(token), ct);
        foreach (var accountMethod in accountMethods)
        {
            var connection = await PlatformReadGuard.ReadAsync(token => db.PspConnections
                .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountMethod.PspConnectionId
                    && x.MerchantId == merchantId && x.PaymentProviderId == accountMethod.PaymentProviderId
                    && x.IsEnabled, token), ct);
            if (connection is null)
                continue;
            var provider = await LoadProviderAsync(connection.Psp, ct);
            var catalog = await LoadProviderMethodAsync(provider, method, ct);
            if (provider.PaymentProviderId == accountMethod.PaymentProviderId && provider.IsEnabled
                && catalog is { MethodIsActive: true, ProviderMethodIsActive: true }
                && catalog.PaymentProviderMethodId == accountMethod.PaymentProviderMethodId
                && adapterFactory.For(connection.Psp).SupportedMethods.Contains(method))
                return true;
        }
        return false;
    }

    private async Task<Connection?> FindConnectionForAccessAsync(
        Guid connectionId, AdminPaymentsAccess access, bool tracking, CancellationToken ct)
    {
        var source = db.PspConnections.IgnoreQueryFilters();
        if (!tracking)
            source = source.AsNoTracking();
        var row = await PlatformReadGuard.ReadAsync(token =>
            source.SingleOrDefaultAsync(x => x.Id == connectionId, token), ct);
        return row is not null && access.Allows(row.MerchantId) ? row : null;
    }

    private async Task SyncAccountMethodsAsync(
        Connection connection, ProviderCatalogRow provider, IReadOnlyList<string> methods,
        Guid actorId, CancellationToken ct)
    {
        EnsureProviderBinding(connection, provider);
        var catalogs = new List<ProviderMethodCatalogRow>(methods.Count);
        foreach (var method in methods)
        {
            var catalog = await LoadProviderMethodAsync(provider, method, ct)
                ?? throw new NotFoundException("Payment method was not found.");
            EnsureAccountMethodCanEnable(connection, provider, catalog, enabled: true);
            catalogs.Add(catalog);
        }

        var existing = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().Where(x => x.MerchantId == connection.MerchantId
                && x.PspConnectionId == connection.Id).ToListAsync(token), ct);
        var requested = catalogs.Select(x => x.PaymentMethodId).ToHashSet();
        foreach (var catalog in catalogs)
        {
            var row = existing.SingleOrDefault(x => x.PaymentMethodId == catalog.PaymentMethodId);
            if (row is null)
            {
                db.MerchantProviderAccountMethods.Add(MerchantProviderAccountMethod.Create(
                    connection.MerchantId, connection.Id, provider.PaymentProviderId,
                    catalog.PaymentProviderMethodId!.Value, catalog.PaymentMethodId,
                    actorId, clock.UtcNow));
            }
            else
            {
                row.SetEnabled(true, actorId, clock.UtcNow);
            }
        }
        foreach (var row in existing.Where(x => !requested.Contains(x.PaymentMethodId)))
            row.SetEnabled(false, actorId, clock.UtcNow);
        connection.ProjectEnabledMethods(methods);
    }

    private async Task ProjectAccountMethodsAsync(
        Connection connection, string changedMethod, bool enabled, CancellationToken ct)
    {
        var methodIds = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == connection.MerchantId
                && x.PspConnectionId == connection.Id && x.IsEnabled)
            .Select(x => x.PaymentMethodId).ToListAsync(token), ct);
        var codes = await LoadMethodCodesAsync(methodIds, ct);
        if (enabled)
            codes.Add(changedMethod);
        else
            codes.Remove(changedMethod);
        connection.ProjectEnabledMethods(codes);
    }

    private async Task ProjectMerchantMethodsAsync(
        Guid merchantId,
        string changedMethod,
        bool enabled,
        CancellationToken ct)
    {
        var methodIds = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId && x.IsEnabled)
            .Select(x => x.PaymentMethodId).ToListAsync(token), ct);
        var codes = await LoadMethodCodesAsync(methodIds, ct);
        if (enabled)
            codes.Add(changedMethod);
        else
            codes.Remove(changedMethod);
        var merchant = await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == merchantId, token), ct);
        merchant.ProjectEnabledChannels(codes);
    }

    private void EnsureAccountMethodCanEnable(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow catalog, bool enabled)
    {
        if (!enabled)
            return;
        if (!connection.IsEnabled || !provider.IsEnabled || !catalog.MethodIsActive
            || catalog.PaymentProviderMethodId is null || !catalog.ProviderMethodIsActive
            || !adapterFactory.For(connection.Psp).SupportedMethods.Contains(catalog.MethodCode))
            throw new PaymentCapabilityUnavailableException(
                "Account method has an inactive parent or exceeds adapter capability.");
    }

    private static void EnsureProviderBinding(Connection connection, ProviderCatalogRow provider)
    {
        if (connection.PaymentProviderId is { } bound && bound != provider.PaymentProviderId)
            throw new PaymentCapabilityUnavailableException("PSP connection provider binding is invalid.");
        connection.BindPaymentProvider(provider.PaymentProviderId);
    }

    private async Task<ProviderCatalogRow> LoadProviderAsync(Code psp, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return psp switch
            {
                Code.TwoCTwoP => new ProviderCatalogRow
                {
                    PaymentProviderId = PaymentCapabilityIds.TwoCTwoP,
                    ProviderCode = "2c2p", AdapterCode = (int)psp, IsEnabled = true,
                },
                Code.Omise => new ProviderCatalogRow
                {
                    PaymentProviderId = PaymentCapabilityIds.Omise,
                    ProviderCode = "omise", AdapterCode = (int)psp, IsEnabled = true,
                },
                _ => throw new PaymentCapabilityUnavailableException("Payment provider is not configured."),
            };

        var query = db.Database.SqlQuery<ProviderCatalogRow>($"""
            SELECT [Id] AS [PaymentProviderId], [Code] AS [ProviderCode], [AdapterCode], [IsEnabled]
            FROM [cfg].[PaymentProviders]
            WHERE [AdapterCode] = {(int)psp}
            """);
        var row = await PlatformReadGuard.ReadAsync(token => query.SingleOrDefaultAsync(token), ct);
        return row ?? throw new PaymentCapabilityUnavailableException("Payment provider is not configured.");
    }

    private async Task<ProviderMethodCatalogRow?> LoadProviderMethodAsync(
        ProviderCatalogRow provider, string method, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            return FallbackProviderMethod(provider, method);
        var query = db.Database.SqlQuery<ProviderMethodCatalogRow>($"""
            SELECT m.[Id] AS [PaymentMethodId], m.[Code] AS [MethodCode], m.[IsActive] AS [MethodIsActive],
                   pm.[Id] AS [PaymentProviderMethodId],
                   COALESCE(pm.[IsActive], CAST(0 AS bit)) AS [ProviderMethodIsActive]
            FROM [cfg].[PaymentMethods] m
            LEFT JOIN [cfg].[PaymentProviderMethods] pm
              ON pm.[PaymentProviderId] = {provider.PaymentProviderId} AND pm.[PaymentMethodId] = m.[Id]
            WHERE m.[Code] = {method}
            """);
        return await PlatformReadGuard.ReadAsync(token => query.SingleOrDefaultAsync(token), ct);
    }

    private async Task<ProviderMethodOptionCatalogRow?> LoadProviderMethodOptionAsync(
        ProviderMethodCatalogRow method, string option, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
        {
            var optionId = option switch
            {
                "KBANK" => PaymentCapabilityIds.Kbank,
                "SCB" => PaymentCapabilityIds.Scb,
                "KTC" => PaymentCapabilityIds.Ktc,
                "BAY" => PaymentCapabilityIds.Bay,
                _ => (Guid?)null,
            };
            return optionId is null ? null : new ProviderMethodOptionCatalogRow
            {
                PaymentMethodOptionId = optionId.Value,
                OptionCode = option,
                PaymentProviderMethodOptionId = null,
                ProviderMethodOptionIsActive = false,
            };
        }
        var query = db.Database.SqlQuery<ProviderMethodOptionCatalogRow>($"""
            SELECT o.[Id] AS [PaymentMethodOptionId], o.[Code] AS [OptionCode],
                   pmo.[Id] AS [PaymentProviderMethodOptionId],
                   COALESCE(pmo.[IsActive], CAST(0 AS bit)) AS [ProviderMethodOptionIsActive]
            FROM [cfg].[PaymentMethodOptions] o
            LEFT JOIN [cfg].[PaymentProviderMethodOptions] pmo
              ON pmo.[PaymentProviderMethodId] = {method.PaymentProviderMethodId}
             AND pmo.[PaymentMethodOptionId] = o.[Id]
            WHERE o.[PaymentMethodId] = {method.PaymentMethodId} AND o.[Code] = {option}
            """);
        return await PlatformReadGuard.ReadAsync(token => query.SingleOrDefaultAsync(token), ct);
    }

    private static Task<HashSet<string>> LoadMethodCodesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ids.Select(MethodCode).Where(x => x is not null).Select(x => x!)
            .ToHashSet(StringComparer.Ordinal));
    }

    private static ProviderMethodCatalogRow? FallbackProviderMethod(ProviderCatalogRow provider, string method)
    {
        var methodId = method switch
        {
            PaymentMethods.Card => PaymentCapabilityIds.Card,
            PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
            PaymentMethods.Installment => PaymentCapabilityIds.Installment,
            _ => (Guid?)null,
        };
        if (methodId is null)
            return null;
        Guid? providerMethodId = (provider.PaymentProviderId, method) switch
        {
            var (p, m) when p == PaymentCapabilityIds.TwoCTwoP && m == PaymentMethods.Card =>
                PaymentCapabilityIds.TwoCTwoPCard,
            var (p, m) when p == PaymentCapabilityIds.TwoCTwoP && m == PaymentMethods.PromptPay =>
                PaymentCapabilityIds.TwoCTwoPPromptPay,
            var (p, m) when p == PaymentCapabilityIds.TwoCTwoP && m == PaymentMethods.Installment =>
                PaymentCapabilityIds.TwoCTwoPInstallment,
            var (p, m) when p == PaymentCapabilityIds.Omise && m == PaymentMethods.Card =>
                PaymentCapabilityIds.OmiseCard,
            _ => null,
        };
        return new ProviderMethodCatalogRow
        {
            PaymentMethodId = methodId.Value,
            MethodCode = method,
            MethodIsActive = true,
            PaymentProviderMethodId = providerMethodId,
            ProviderMethodIsActive = providerMethodId is not null,
        };
    }

    private static string? MethodCode(Guid id) => id switch
    {
        var value when value == PaymentCapabilityIds.Card => PaymentMethods.Card,
        var value when value == PaymentCapabilityIds.PromptPay => PaymentMethods.PromptPay,
        var value when value == PaymentCapabilityIds.Installment => PaymentMethods.Installment,
        _ => null,
    };

    private static Guid MethodId(string method) => method switch
    {
        PaymentMethods.Card => PaymentCapabilityIds.Card,
        PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
        PaymentMethods.Installment => PaymentCapabilityIds.Installment,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static MerchantPaymentMethodView MerchantPolicyView(
        Guid merchantId, string method, MerchantPaymentMethod? row, bool effective) => new(
        merchantId, method, row?.IsEnabled == true, effective,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private static MerchantUserPaymentMethodView UserPolicyView(
        Guid merchantUserId, Guid merchantId, string method,
        MerchantUserPaymentMethod? row, bool effective) => new(
        merchantUserId, merchantId, method, row?.IsEnabled == true, effective,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private static AccountPaymentCapabilityView AccountMethodView(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow method,
        MerchantProviderAccountMethod? row) => new(
        "account-method", connection.Id, connection.MerchantId, provider.ProviderCode,
        method.MethodCode, null, row?.IsEnabled == true,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private static AccountPaymentCapabilityView AccountOptionView(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow method,
        ProviderMethodOptionCatalogRow option, MerchantProviderAccountMethodOption? row) => new(
        "account-method-option", connection.Id, connection.MerchantId, provider.ProviderCode,
        method.MethodCode, option.OptionCode, row?.IsEnabled == true,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private sealed class ProviderCatalogRow
    {
        public Guid PaymentProviderId { get; set; }
        public string ProviderCode { get; set; } = default!;
        public int AdapterCode { get; set; }
        public bool IsEnabled { get; set; }
    }

    private sealed class PaymentMethodStateRow
    {
        public Guid PaymentMethodId { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class ProviderMethodCatalogRow
    {
        public Guid PaymentMethodId { get; set; }
        public string MethodCode { get; set; } = default!;
        public bool MethodIsActive { get; set; }
        public Guid? PaymentProviderMethodId { get; set; }
        public bool ProviderMethodIsActive { get; set; }
    }

    private sealed class ProviderMethodOptionCatalogRow
    {
        public Guid PaymentMethodOptionId { get; set; }
        public string OptionCode { get; set; } = default!;
        public Guid? PaymentProviderMethodOptionId { get; set; }
        public bool ProviderMethodOptionIsActive { get; set; }
    }

    private static IReadOnlyList<RoutingRuleSpec> Specs(IReadOnlyList<RoutingRuleInput> rules) =>
        rules.Select(x => new RoutingRuleSpec(x.Priority, x.Method, x.OriginatorId, x.MinAmount,
            x.MaxAmount, x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();

    private static RoutingRulesetView ProjectRuleset(RoutingRuleset x) => new(
        x.Id, x.MerchantId, x.Name, RulesetStatusCode(x.Status), x.ApprovalId,
        x.Rules.OrderBy(r => r.Priority).Select(r => new RoutingRuleView(
            r.Id, r.Priority, r.Method, r.OriginatorId,
            FormatAmount(r.MinAmount), FormatAmount(r.MaxAmount), r.TargetConnectionId,
            r.FallbackConnectionId, r.Enabled)).ToList(),
        x.CreatedAt, x.UpdatedAt, x.Version);

    private static T Replay<T>(AdminOperationRecord record) =>
        JsonSerializer.Deserialize<T>(record.Result!, Json)
        ?? throw new InvalidOperationException("Stored operation result is invalid.");

    private static (string? PspMerchantId, JsonElement? Config, Dictionary<string, string> Hints) ReadMetadata(string? value)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
            return (null, null, hints);
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        var merchantId = root.TryGetProperty("merchantId", out var mid) && mid.ValueKind == JsonValueKind.String
            ? mid.GetString() : null;
        JsonElement? config = root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object
            ? cfg.Clone() : null;
        if (root.TryGetProperty("secretHints", out var secretHints) && secretHints.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in secretHints.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    hints[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return (merchantId, config, hints);
    }

    private static string ConnectionMetadata(
        string? merchantId, JsonElement? config, IReadOnlyDictionary<string, string> hints) =>
        JsonSerializer.Serialize(new { merchantId, config, secretHints = hints }, Json);

    internal static void ValidateConfig(JsonElement? config)
    {
        if (config is null)
            return;
        if (config.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException("PSP config must be an object.", "invalid_psp_config");
        if (config.Value.GetRawText().Length > 16_384)
            throw new InvalidRequestException("PSP config is too large.", "invalid_psp_config");

        foreach (var property in config.Value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "accountId":
                    ValidateConfigString(property.Value, property.Name, 200);
                    break;
                case "card":
                case "installment":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw InvalidConfigField(property.Name);
                    break;
                case "enabledSources":
                    ValidateConfigStrings(property.Value, property.Name, 20, 50, requireHttps: false);
                    break;
                case "returnUrls":
                    ValidateConfigStrings(property.Value, property.Name, 10, 2_048, requireHttps: true);
                    break;
                default:
                    throw new InvalidRequestException("PSP config contains a non-allowlisted field.", "invalid_psp_config");
            }
        }
    }

    private static void ValidateConfigStrings(
        JsonElement value, string name, int maxItems, int maxLength, bool requireHttps)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw InvalidConfigField(name);
        var items = value.EnumerateArray().ToList();
        if (items.Count > maxItems)
            throw InvalidConfigField(name);
        foreach (var item in items)
        {
            ValidateConfigString(item, name, maxLength);
            if (requireHttps && (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment)))
                throw InvalidConfigField(name);
        }
    }

    private static void ValidateConfigString(JsonElement value, string name, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maxLength
            || value.GetString()!.Any(char.IsControl))
            throw InvalidConfigField(name);
    }

    private static InvalidRequestException InvalidConfigField(string name) =>
        new($"PSP config field '{name}' is invalid.", "invalid_psp_config");

    private IReadOnlyList<string> ValidateMethods(Code psp, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            throw new InvalidRequestException("At least one payment method is required.", "invalid_psp_config");
        IReadOnlyList<string> methods;
        try
        {
            methods = values.Select(PaymentMethods.Normalize).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidRequestException(ex.Message, "validation_failed");
        }
        var supported = adapterFactory.For(psp).SupportedMethods;
        if (methods.Any(x => !supported.Contains(x)))
            throw new InvalidRequestException("PSP method is not supported by the adapter.", "invalid_psp_config");
        return methods;
    }

    private static string NormalizeMethod(string value)
    {
        try { return PaymentMethods.Normalize(value); }
        catch (ArgumentException ex) { throw new InvalidRequestException(ex.Message, "validation_failed"); }
    }

    private static string NormalizeOption(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidRequestException("Payment option code is required.", "validation_failed");
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length > 32 || normalized.Any(char.IsControl))
            throw new InvalidRequestException("Payment option code is invalid.", "validation_failed");
        return normalized;
    }

    private static Code ParsePsp(string value)
    {
        try { return Codes.FromCode(value.Trim().ToLowerInvariant()); }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        { throw new InvalidRequestException("PSP code is invalid.", "invalid_psp_config"); }
    }

    private static PspConnectionHealth ParseHealth(string value) => value.Trim().ToLowerInvariant() switch
    {
        "unknown" => PspConnectionHealth.Unknown,
        "healthy" => PspConnectionHealth.Healthy,
        "failed" => PspConnectionHealth.Failed,
        _ => throw new InvalidRequestException("PSP health filter is invalid.", "invalid_filter"),
    };

    private static string HealthCode(PspConnectionHealth value) => value switch
    {
        PspConnectionHealth.Unknown => "unknown",
        PspConnectionHealth.Healthy => "healthy",
        PspConnectionHealth.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static RoutingRulesetStatus ParseRulesetStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "draft" => RoutingRulesetStatus.Draft,
        "pending" => RoutingRulesetStatus.PendingApproval,
        "active" => RoutingRulesetStatus.Active,
        "superseded" => RoutingRulesetStatus.Superseded,
        _ => throw new InvalidRequestException("Routing status filter is invalid.", "invalid_filter"),
    };

    private static string RulesetStatusCode(RoutingRulesetStatus value) => value switch
    {
        RoutingRulesetStatus.Draft => "draft",
        RoutingRulesetStatus.PendingApproval => "pending",
        RoutingRulesetStatus.Active => "active",
        RoutingRulesetStatus.Superseded => "superseded",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string? FormatAmount(decimal? value) => value?.ToString("0.00##", CultureInfo.InvariantCulture);
    private static string Mask(string value) => value.StartsWith("****", StringComparison.Ordinal) ? value : $"****{value}";

    private static void EnsureAccess(AdminPaymentsAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId))
            throw new AdminPaymentsAccessDeniedException("Merchant is outside the current admin scope.");
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
            throw new ConcurrencyConflictException("The resource version is stale.");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");
    }

    private static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)))).ToLowerInvariant();

    internal static string SecretIntentFingerprint(string envelopeJson) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))).ToLowerInvariant();
}
