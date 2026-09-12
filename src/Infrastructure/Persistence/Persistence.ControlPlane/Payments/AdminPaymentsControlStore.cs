using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Vault;
using Contracts;
using Governance.Domain;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.AdminControlPlane;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.ControlPlane.Payments.Capabilities;
using Persistence.MerchantRuntime;
using SharedKernel;
using Merchant = Merchants.Domain.Merchant;
using ControlPlaneDbContext = Persistence.ControlPlane.ControlPlaneDbContext;
using ControlPlanePaymentAuthorizationSqlLockManager = Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager;
using BuildingBlocks.Infrastructure.Persistence;

namespace Persistence.ControlPlane.Payments;

internal sealed class AdminPaymentsControlStore(
    ControlPlaneDbContext db,
    CommerceDbContext commerceDb,
    IClock clock,
    [FromKeyedServices("admin")] IUnitOfWork unitOfWork,
    IVaultSecretStore vault,
    IPspSecretEnvelopeFactory envelopeFactory,
    IPspAdapterFactory adapterFactory,
    IMerchantRuntimeAuthorizationLease authorizationLease,
    ControlPlanePaymentAuthorizationSqlLockManager? authorizationLocks = null,
    IEffectivePaymentCapabilityResolver? effectiveResolver = null)
    : IAdminPaymentsControlStore, IAccountPaymentCapabilityControlStore, ISimpleRoutingControlStore
{
    private static readonly JsonSerializerOptions Json = new(OutboxSerializer.Options);
    private ControlPlanePaymentAuthorizationSqlLockManager AuthorizationLocks { get; } =
        authorizationLocks ?? new ControlPlanePaymentAuthorizationSqlLockManager(db);
    private IEffectivePaymentCapabilityResolver EffectiveResolver => effectiveResolver
        ?? new EffectivePaymentCapabilityResolver(db, unitOfWork, AuthorizationLocks, adapterFactory);

    public async Task<MerchantPaymentSettingsView?> GetMerchantPaymentSettingsAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        if (!await MerchantExistsForAccessAsync(merchantId, access, cancellationToken))
            return null;
        var merchant = await PlatformReadGuard.ReadAsync(ct => db.Merchants.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == merchantId, ct), cancellationToken);
        return new MerchantPaymentSettingsView(
            merchant.Id, merchant.PaymentEnvironment.ToCode(), merchant.PendingPaymentEnvironment?.ToCode(),
            merchant.PendingPaymentEnvironmentApprovalId, merchant.PaymentEnvironmentUpdatedAt, merchant.Version);
    }

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

    public async Task<IReadOnlyList<PspCredentialVersionView>?> ListCredentialVersionsAsync(
        Guid connectionId, Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        EnsureAccess(access, merchantId);
        var connection = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId && x.MerchantId == merchantId, ct),
            cancellationToken);
        if (connection is null)
            return null;

        return await PlatformReadGuard.ReadAsync(ct => db.VaultSecretVersions.IgnoreQueryFilters()
            .AsNoTracking().Where(x => x.MerchantId == merchantId && x.SecretName == connection.SecretRefName)
            .OrderByDescending(x => x.Version)
            .Select(x => new PspCredentialVersionView(
                x.Id,
                x.Version,
                x.State == VaultSecretVersionState.Active
                    ? "active"
                    : x.State == VaultSecretVersionState.Staged
                        ? "staged"
                        : x.State == VaultSecretVersionState.Retired
                            ? "retired"
                            : "discarded",
                $"****{x.Hint}",
                x.CreatedAt,
                x.ActivatedAt,
                x.RetiredAt,
                x.ExpiresAt))
            .ToListAsync(ct), cancellationToken);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);

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
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);

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
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
        var decision = await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
            new PaymentCapabilitySubject(merchantId, PaymentAudience.PlatformAdmin, null), code, null),
            cancellationToken);
        return MerchantPolicyView(merchantId, code, row, decision);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);

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
            var decision = await EffectiveResolver.ResolveMethodAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(intent.MerchantId, PaymentAudience.PlatformAdmin, null), method, null),
                ct);
            var view = MerchantPolicyView(intent.MerchantId, method, row, decision);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "payment.merchant-method.set", intent.IdempotencyKey, intentHash);
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);

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
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);
            var merchant = await LoadMerchantAsync(intent.MerchantId, ct);
            var environment = merchant.PaymentEnvironment;
            var psp = ParsePsp(intent.Psp);
            var methods = ValidateMethods(psp, intent.EnabledMethods);
            var provider = await LoadProviderAsync(psp, ct);
            ValidateConfig(intent.Config);
            // Allowlist/size/prefix checks run BEFORE the envelope is built and before any vault write (REQ-4.7-4.11).
            ValidateSecretFields(psp, intent.Secrets, intent.PspMerchantId, environment);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);
            // One record per (MerchantId, Psp) regardless of enabled state: re-enable the existing one
            // instead of creating a twin (REQ-3.1/3.7/3.9). Checked here for a named 409; the unique index
            // stays as the floor.
            if (await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
                    .AnyAsync(x => x.MerchantId == intent.MerchantId && x.Psp == psp, token), ct))
                throw new ConflictException("A PSP connection for this provider already exists.", "psp_connection_exists");

            var connection = Connection.Create(intent.MerchantId, psp, string.Join(',', methods),
                $"psp-connection-{Guid.CreateVersion7():N}", clock.UtcNow,
                ConnectionMetadata(intent.PspMerchantId, intent.Config, envelope.Hints), environment);
            connection.BindPaymentProvider(provider.PaymentProviderId);
            var secretName = connection.SecretRefName;
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json), null, ct);
            await vault.ActivateVersionAsync(intent.MerchantId, candidate, ct);
            connection.SetInitialSecretVersion(candidate, environment);
            db.PspConnections.Add(connection);
            await SyncAccountMethodsAsync(connection, provider, methods, intent.Access.ActorId, ct);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.create", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Complete(201, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return new PspConnectionMutationResult(view, false);
        }, cancellationToken);

    public Task<PspConnectionMutationResult> CreateProviderAccountAsync(
        CreatePspProviderAccountIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var merchant = await LoadMerchantAsync(intent.MerchantId, ct);
            if (string.IsNullOrWhiteSpace(intent.DisplayName) || intent.DisplayName.Trim().Length > 200)
                throw new InvalidRequestException("Provider account displayName is invalid.", "validation_failed");
            ValidateConfig(intent.Config);
            var psp = ParseProviderId(intent.ProviderId);
            var provider = await LoadProviderAsync(psp, ct);
            if (!provider.IsEnabled)
                throw new ConflictException("Payment provider is disabled.", "provider_disabled");
            var environment = ParseEnvironment(intent.Environment);
            var intentHash = Hash(new
            {
                intent.MerchantId, intent.ProviderId, displayName = intent.DisplayName.Trim(),
                environment = environment.ToCode(), config = intent.Config?.GetRawText(),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "psp.account.create", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return new PspConnectionMutationResult(await ReplayConnectionAsync(prior, ct), true);
            if (await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
                    .AnyAsync(x => x.MerchantId == intent.MerchantId && x.Psp == psp, token), ct))
                throw new ConflictException("A provider account for this provider already exists.", "psp_connection_exists");

            // Account configuration is deliberately created without a credential. The write-only credential
            // endpoint owns vault staging; this row starts disabled so it cannot route a transaction before a
            // credential and verified method policy exist.
            var connection = Connection.Create(intent.MerchantId, psp, string.Empty,
                $"psp-connection-{Guid.CreateVersion7():N}", clock.UtcNow,
                JsonSerializer.Serialize(new
                {
                    merchantId = (string?)null,
                    displayName = intent.DisplayName.Trim(),
                    config = intent.Config,
                    secretHints = new Dictionary<string, string>(),
                }, Json), environment);
            connection.BindPaymentProvider(provider.PaymentProviderId);
            connection.Update(string.Empty, connection.Metadata, isEnabled: false);
            db.PspConnections.Add(connection);
            await SyncAccountMethodsAsync(connection, provider, [], intent.Access.ActorId, ct);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.account.create", intent.IdempotencyKey, intentHash);
            var view = await ProjectConnectionAsync(connection, ct);
            operation.Complete(201, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);
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
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
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
            if (prior.ResponseStatus == 502)
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
            await adapterFactory.For(snapshot.Psp).TestConnectionAsync(
                secret, snapshot.ActiveSecretEnvironment, cancellationToken);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
        }

        var view = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var current = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            EnsureVersion(current.Version, intent.ExpectedVersion);
            await authorizationLease.VerifyAsync(intent.Access, ct);
            current.RecordTest(succeeded, succeeded ? "authenticated" : "probe_failed", clock.UtcNow);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.test", intent.IdempotencyKey, intentHash);
            var result = await ProjectConnectionAsync(current, ct);
            operation.Complete(succeeded ? 200 : 502, JsonSerializer.Serialize(result, Json), succeeded: true, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
        if (!succeeded)
            throw new PspConnectionTestFailedException(view);
        return new PspConnectionMutationResult(view, false);
    }

    public Task<PspCredentialChangeResult> RequestCredentialChangeAsync(
        RequestPspCredentialChangeIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var connection = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            var merchant = await LoadMerchantAsync(intent.MerchantId, ct);
            // A rotation targets the merchant's CURRENT environment; switching environments is task 7's
            // environment-change request, which stages every connection at once (REQ-2.11/2.16).
            var environment = merchant.PaymentEnvironment;
            ValidateSecretFields(connection.Psp, intent.Secrets, intent.PspMerchantId, environment);
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
            // A single-connection rotation cannot start while another approval already owns this connection's
            // credential (AC-6.1) or while a merchant-wide environment change is pending (AC-7.6 coupling): the
            // environment switch stages every connection, so a lone credential change would race it. Guard state
            // BEFORE the ETag check so a caller holding a fresh ETag still sees approval_pending, not state_conflict.
            if (connection.PendingApprovalId is not null)
                throw new ConflictException("A credential change is already pending for this connection.", "approval_pending");
            if (merchant.PendingPaymentEnvironmentApprovalId is not null)
                throw new ConflictException("A payment environment change is pending for this merchant.", "approval_pending");
            // A merchant with an unresolved legacy Session (snapshot version 0, a historical charge whose
            // pinned secret has not been proven) is blocked from activating a new credential too, not only an
            // environment switch (AC-9.3) — task 9 remediation must clear it first (critical #12).
            if (await PlatformReadGuard.ReadAsync(token => commerceDb.PaymentSessions.IgnoreQueryFilters()
                    .AnyAsync(x => x.MerchantId == intent.MerchantId && x.RoutingSnapshotVersion == 0 && x.PspExternalChargeId != null, token), ct))
                throw new ConflictException(
                    "The merchant has an unresolved legacy payment session that must be remediated first.", "legacy_snapshot_blocked");
            await authorizationLease.VerifyAsync(intent.Access, ct);
            EnsureVersion(connection.Version, intent.ExpectedVersion);

            var secretName = connection.SecretRefName;
            var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json),
                clock.UtcNow.AddHours(24), ct);
            var approvalId = Guid.CreateVersion7();
            connection.StageSecretVersion(candidate, approvalId, environment);
            var targetVersion = $"v{connection.Version}";
            var request = PaymentSettingRequestContract.Pending(
                approvalId,
                intent.MerchantId,
                new PaymentSettingProposal(
                    PaymentSettingRequestKind.Credential,
                    intent.ExpectedVersion,
                    environment,
                    [connection.Id],
                    [candidate],
                    $"provider-account:{connection.Id:D}"),
                intent.Access.ActorId,
                clock.UtcNow);
            var result = new PspCredentialChangeResult(approvalId, candidate, "pending", false, request);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-change", intent.IdempotencyKey, intentHash);
            operation.Complete(202, JsonSerializer.Serialize(result, Json), succeeded: true, clock.UtcNow);
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "psp.credential.change", "settings.manage", intent.Access.ActorId,
                "psp-credential-version", connection.Id.ToString("D"), targetVersion,
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public async Task<PspConnectionMutationResult> TestCandidateCredentialAsync(
        TestPspCandidateCredentialIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var intentHash = Hash(new { intent.ConnectionId, intent.MerchantId, intent.ApprovalId, intent.ExpectedVersion });
        var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
            "psp.credential-test", intent.IdempotencyKey, intentHash, cancellationToken);
        if (prior is not null)
        {
            var replay = await ReplayConnectionAsync(prior, cancellationToken);
            if (prior.ResponseStatus == 502)
                throw new PspConnectionTestFailedException(replay);
            return new PspConnectionMutationResult(replay, true);
        }

        var snapshot = await PlatformReadGuard.ReadAsync(ct => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == intent.ConnectionId && x.MerchantId == intent.MerchantId, ct), cancellationToken)
            ?? throw new NotFoundException("PSP connection was not found.");
        EnsureVersion(snapshot.Version, intent.ExpectedVersion);
        if (snapshot.PendingApprovalId != intent.ApprovalId || snapshot.PendingSecretVersionId is not { } candidateId)
            throw new NotFoundException("The credential change request was not found.");
        var candidateEnvironment = snapshot.PendingSecretEnvironment ?? snapshot.ActiveSecretEnvironment;

        // Probe the STAGED candidate against ITS target environment. Read-only: never activates and never
        // touches the active credential's health (REQ-7.10). A thrown probe is the only failure signal (a
        // failure-shaped result would read as authenticated), matching the active-test path.
        var succeeded = false;
        try
        {
            var secret = await vault.ReadVersionForServerAsync(intent.MerchantId, candidateId, cancellationToken);
            await adapterFactory.For(snapshot.Psp).TestConnectionAsync(secret, candidateEnvironment, cancellationToken);
            succeeded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            succeeded = false;
        }

        var view = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var current = await LoadConnectionAsync(intent.ConnectionId, intent.MerchantId, ct);
            // compare-after-probe (critical #18): any concurrent approve/reject/test bumps Version, so a stale
            // probe result is discarded rather than written. The pending checks are a defensive echo of that.
            EnsureVersion(current.Version, intent.ExpectedVersion);
            await authorizationLease.VerifyAsync(intent.Access, ct);
            if (current.PendingApprovalId != intent.ApprovalId || current.PendingSecretVersionId != candidateId)
                throw new ConcurrencyConflictException("The credential candidate changed during the test.");
            current.RecordPendingSecretTest(succeeded, clock.UtcNow);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "psp.credential-test", intent.IdempotencyKey, intentHash);
            var result = await ProjectConnectionAsync(current, ct);
            operation.Complete(succeeded ? 200 : 502, JsonSerializer.Serialize(result, Json), succeeded: true, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
        if (!succeeded)
            throw new PspConnectionTestFailedException(view);
        return new PspConnectionMutationResult(view, false);
    }

    public Task<EnvironmentChangeResult> RequestEnvironmentChangeAsync(
        RequestEnvironmentChangeIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            // Unknown environment code is malformed input (400), decided before touching state (REQ-2.6).
            var target = ParseEnvironment(intent.TargetEnvironment);
            var merchant = await LoadMerchantForUpdateAsync(intent.MerchantId, ct);
            if (target == merchant.PaymentEnvironment)
                throw new InvalidRequestException(
                    "The merchant already uses the requested payment environment.", "validation_failed");

            var connections = await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
                .Where(x => x.MerchantId == intent.MerchantId).ToListAsync(token), ct);

            // Map the body's credentials to this merchant's connections. A body entry that names an unknown or
            // out-of-scope connection, or names one twice, is malformed input (400) — checked before any 409.
            var byId = new Dictionary<Guid, EnvironmentChangeConnectionCredential>();
            foreach (var entry in intent.Connections)
            {
                if (connections.All(c => c.Id != entry.PspConnectionId))
                    throw new InvalidRequestException(
                        $"Connection '{entry.PspConnectionId:D}' does not belong to this merchant.", "validation_failed");
                if (!byId.TryAdd(entry.PspConnectionId, entry))
                    throw new InvalidRequestException(
                        $"Connection '{entry.PspConnectionId:D}' appears more than once.", "validation_failed");
            }

            // Validate every provided credential at the TARGET environment and build its envelope BEFORE any
            // vault write (REQ-4.7-4.11). Envelopes are kept so a fingerprint feeds the idempotency hash.
            var staged = new List<(Connection Connection, PspSecretEnvelopeResult Envelope)>();
            foreach (var connection in connections.OrderBy(c => c.Id))
            {
                if (!byId.TryGetValue(connection.Id, out var entry))
                    continue;
                ValidateSecretFields(connection.Psp, entry.Secrets, entry.PspMerchantId, target);
                var envelope = envelopeFactory.Build(new PspSecretInput(connection.Psp, entry.Secrets, entry.PspMerchantId));
                staged.Add((connection, envelope));
            }

            var intentHash = Hash(new
            {
                intent.MerchantId,
                target = target.ToCode(),
                intent.ExpectedVersion,
                connections = staged.Select(x => new
                {
                    connectionId = x.Connection.Id,
                    secretFingerprint = SecretIntentFingerprint(x.Envelope.EnvelopeJson),
                }).ToList(),
            });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "payment.environment-change", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<EnvironmentChangeResult>(prior);

            // Every connection must be re-credentialed for the target — a partial switch would leave a mixed
            // sandbox/live state (REQ-2.16). Missing one is a named 409, not a silent partial stage.
            if (staged.Count != connections.Count)
                throw new ConflictException(
                    "Every connection must supply a credential for the target environment.",
                    "environment_credentials_incomplete");
            // Omise live requires the admin to confirm the callback URL is registered at the dashboard (REQ-11.2).
            if (target == PspEnvironment.Live
                && connections.Any(c => c.Psp == Code.Omise) && !intent.OmiseWebhookRegistered)
                throw new ConflictException(
                    "The Omise callback URL must be acknowledged as registered before going live.", "webhook_not_ready");
            // Guard the pending state BEFORE the ETag so a caller holding a fresh ETag still sees approval_pending,
            // not state_conflict (precedent: the single-connection credential change).
            if (merchant.PendingPaymentEnvironmentApprovalId is not null)
                throw new ConflictException("A payment environment change is already pending for this merchant.", "approval_pending");
            if (connections.Any(c => c.PendingApprovalId is not null))
                throw new ConflictException("A credential change is pending for one of this merchant's connections.", "approval_pending");
            // An unresolved legacy Session (snapshot version 0 — a historical charge whose pinned secret has not
            // been proven) blocks the switch until task 9 remediation clears it (critical #12).
            if (await PlatformReadGuard.ReadAsync(token => commerceDb.PaymentSessions.IgnoreQueryFilters()
                    .AnyAsync(x => x.MerchantId == intent.MerchantId && x.RoutingSnapshotVersion == 0 && x.PspExternalChargeId != null, token), ct))
                throw new ConflictException(
                    "The merchant has an unresolved legacy payment session that must be remediated first.", "legacy_snapshot_blocked");

            await authorizationLease.VerifyAsync(intent.Access, ct);
            EnsureVersion(merchant.Version, intent.ExpectedVersion);

            var approvalId = Guid.CreateVersion7();
            var candidateIds = new List<Guid>(staged.Count);
            foreach (var (connection, envelope) in staged)
            {
                var secretName = connection.SecretRefName;
                var candidate = await vault.StageVersionAsync(intent.MerchantId, secretName,
                    envelope.EnvelopeJson, JsonSerializer.Serialize(envelope.Hints, Json),
                    clock.UtcNow.AddHours(24), ct);
                candidateIds.Add(candidate);
                connection.StageSecretVersion(candidate, approvalId, target);
                if (target == PspEnvironment.Live && connection.Psp == Code.Omise)
                    connection.AcknowledgeWebhookRegistration(
                        adapterFactory.For(connection.Psp).CallbackUrlFor(connection.Id), intent.Access.ActorId, clock.UtcNow);
            }
            merchant.StagePaymentEnvironment(target, approvalId);
            var request = PaymentSettingRequestContract.Pending(
                approvalId,
                intent.MerchantId,
                new PaymentSettingProposal(
                    PaymentSettingRequestKind.Environment,
                    intent.ExpectedVersion,
                    target,
                    staged.Select(x => x.Connection.Id).ToList(),
                    candidateIds,
                    $"merchant-environment:{intent.MerchantId:D}"),
                intent.Access.ActorId,
                clock.UtcNow);
            var result = new EnvironmentChangeResult(
                approvalId, intent.MerchantId, target.ToCode(), staged.Count, "pending", false, request);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "payment.environment-change", intent.IdempotencyKey, intentHash);
            operation.Complete(202, JsonSerializer.Serialize(result, Json), succeeded: true, clock.UtcNow);
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "psp.environment.change", "settings.manage", intent.Access.ActorId,
                "merchant-environment", intent.MerchantId.ToString("D"), $"v{merchant.Version}",
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

    public Task<RoutingRulesetView> CreateRulesetAsync(
        CreateRoutingRulesetIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            await authorizationLease.VerifyAsync(intent.Access, ct);
            await ValidateRulesAsync(intent.MerchantId, intent.Rules, ct);
            var entity = RoutingRuleset.Create(intent.MerchantId, intent.Name, Specs(intent.Rules), clock.UtcNow);
            db.RoutingRulesets.Add(entity);
            await unitOfWork.SaveChangesAsync(ct);
            return ProjectRuleset(entity);
        }, cancellationToken);

    public Task<RoutingRulesetView> ReplaceRulesetAsync(
        ReplaceRoutingRulesetIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, ct);
            EnsureVersion(entity.Version, intent.ExpectedVersion);
            await authorizationLease.VerifyAsync(intent.Access, ct);
            await ValidateRulesAsync(intent.MerchantId, intent.Rules, ct);
            entity.Replace(intent.Name, Specs(intent.Rules), clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return ProjectRuleset(entity);
        }, cancellationToken);

    public Task DeleteRulesetAsync(
        Guid rulesetId, Guid merchantId, long expectedVersion, AdminPaymentsAccess access,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(access, merchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(merchantId, ct);
            var entity = await LoadRulesetAsync(rulesetId, merchantId, ct);
            EnsureVersion(entity.Version, expectedVersion);
            await authorizationLease.VerifyAsync(access, ct);
            if (entity.Status != RoutingRulesetStatus.Draft)
                throw new InvalidOperationException("Only draft routing rulesets can be deleted.");
            db.RoutingRulesets.Remove(entity);
            await unitOfWork.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

    public Task<RoutingActivationResult> RequestActivationAsync(
        RequestRoutingActivationIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            var entity = await LoadRulesetAsync(intent.RulesetId, intent.MerchantId, ct);
            EnsureVersion(entity.Version, intent.ExpectedVersion);
            var input = entity.Rules.Select(x => new RoutingRuleInput(
                x.Priority, x.Method, x.OriginatorId, x.MinAmount, x.MaxAmount,
                x.TargetConnectionId, x.FallbackConnectionId, x.Enabled)).ToList();
            await ValidateRulesAsync(intent.MerchantId, input, ct);
            // REQ-6.17 / AC-5.4: every merchant-level enabled method must have a primary before activation.
            // Enforced here because this is the single activation path both the simple and advanced pages use.
            await EnsureRoutingCoverageAsync(intent.MerchantId, entity, ct);
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
            await authorizationLease.VerifyAsync(intent.Access, ct);

            var approvalId = Guid.CreateVersion7();
            entity.RequestActivation(approvalId, clock.UtcNow);
            var merchantEnvironment = await PlatformReadGuard.ReadAsync(token => db.Merchants
                .IgnoreQueryFilters().Where(x => x.Id == intent.MerchantId)
                .Select(x => x.PaymentEnvironment).SingleAsync(token), ct);
            var providerAccountIds = entity.Rules
                .SelectMany(x => new[] { x.TargetConnectionId, x.FallbackConnectionId })
                .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
            var request = PaymentSettingRequestContract.Pending(
                approvalId,
                intent.MerchantId,
                new PaymentSettingProposal(
                    PaymentSettingRequestKind.Routing,
                    intent.ExpectedVersion,
                    merchantEnvironment,
                    providerAccountIds,
                    [],
                    $"routing-ruleset:{entity.Id:D}"),
                intent.Access.ActorId,
                clock.UtcNow);
            var result = new RoutingActivationResult(approvalId, ProjectRuleset(entity), false, request);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "routing.activation", intent.IdempotencyKey, intentHash);
            operation.Complete(202, JsonSerializer.Serialize(result, Json), succeeded: true, clock.UtcNow);
            EnqueueApproval(new ApprovalRequested(
                Guid.CreateVersion7(), approvalId, "merchant", intent.MerchantId,
                "routing.activate", "settings.manage", intent.Access.ActorId,
                "routing-ruleset", entity.Id.ToString("D"), $"v{entity.Version}",
                intent.CorrelationId, clock.UtcNow));
            await unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    private const string SimpleRoutingName = "Simple routing";

    public async Task<SimpleRoutingView?> GetSimpleRoutingAsync(
        Guid merchantId, AdminPaymentsAccess access, CancellationToken cancellationToken)
    {
        if (!await MerchantExistsForAccessAsync(merchantId, access, cancellationToken))
            return null;
        // Every LIVE ruleset (draft, pending-approval or active — not superseded history): the page is
        // read-only if ANY of them carries an advanced predicate, including one already sent for activation.
        var rulesets = await PlatformReadGuard.ReadAsync(ct => db.RoutingRulesets.IgnoreQueryFilters()
            .AsNoTracking().Include(x => x.Rules)
            .Where(x => x.MerchantId == merchantId && x.Status != RoutingRulesetStatus.Superseded)
            .ToListAsync(ct), cancellationToken);
        var advancedReadOnly = rulesets.Any(x => x.Rules.Any(IsAdvancedRule));
        // Resolve "the draft" deterministically; PUT resolves it the same way, so a second draft appearing
        // shifts the resolution and the stale ETag turns into a 409 state_conflict.
        var draft = rulesets.Where(x => x.Status == RoutingRulesetStatus.Draft)
            .OrderBy(x => x.Id).FirstOrDefault();
        var display = draft
            ?? rulesets.FirstOrDefault(x => x.Status == RoutingRulesetStatus.PendingApproval)
            ?? rulesets.FirstOrDefault(x => x.Status == RoutingRulesetStatus.Active);
        var rows = advancedReadOnly || display is null
            ? (IReadOnlyList<SimpleRoutingRuleView>)[]
            : display.Rules.OrderBy(r => r.Priority)
                .Select(r => new SimpleRoutingRuleView(r.Method, r.TargetConnectionId, r.FallbackConnectionId))
                .ToList();
        // Version is the draft's (the write target); 0 when no draft exists yet, even if a pending/active
        // ruleset is shown for context — a PUT still creates a fresh draft against ETag 0.
        return new SimpleRoutingView(
            merchantId, draft?.Id, display is null ? "none" : RulesetStatusCode(display.Status),
            advancedReadOnly, rows, draft?.Version ?? 0);
    }

    public Task<SimpleRoutingView> SetSimpleRoutingAsync(
        SetSimpleRoutingIntent intent, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(intent.Access, intent.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(intent.MerchantId, ct);
            // Scan every LIVE ruleset (draft, pending-approval or active) — a pending-approval advanced
            // ruleset must still make the page read-only, or a simple PUT could create a draft that then
            // supersedes it. Superseded history is excluded.
            var rulesets = await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets.IgnoreQueryFilters()
                .Include(x => x.Rules)
                .Where(x => x.MerchantId == intent.MerchantId && x.Status != RoutingRulesetStatus.Superseded)
                .ToListAsync(token), ct);

            // Advanced guard BEFORE the ETag check: an advanced-rule merchant is always read-only from the
            // simple page whatever ETag it presents, so no advanced ruleset is ever mutated here.
            if (rulesets.Any(x => x.Rules.Any(IsAdvancedRule)))
                throw new ConflictException(
                    "Routing carries advanced rules and is read-only from the simple settings page.",
                    "advanced_routing_read_only");

            var draft = rulesets.Where(x => x.Status == RoutingRulesetStatus.Draft)
                .OrderBy(x => x.Id).FirstOrDefault();

            // Idempotency replay BEFORE the ETag check (same order as the other mutations here): a genuine
            // retry returns the stored result, and a same-key/different-intent request is a reuse conflict
            // rather than a stale-ETag one.
            var intentHash = Hash(new { intent.MerchantId, intent.ExpectedVersion, Rows = intent.Rules });
            var prior = await FindOperationAsync(intent.MerchantId, intent.Access.ActorId,
                "routing.simple-set", intent.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<SimpleRoutingView>(prior);
            EnsureVersion(draft?.Version ?? 0, intent.ExpectedVersion);
            await authorizationLease.VerifyAsync(intent.Access, ct);

            var specs = await BuildSimpleSpecsAsync(intent.MerchantId, intent.Rules, ct);
            if (draft is null)
            {
                draft = RoutingRuleset.Create(intent.MerchantId, SimpleRoutingName, specs, clock.UtcNow);
                db.RoutingRulesets.Add(draft);
            }
            else
            {
                draft.Replace(draft.Name, specs, clock.UtcNow);
            }

            var view = ProjectSimpleRouting(draft, advancedReadOnly: false);
            var operation = BeginOperation(intent.MerchantId, intent.Access.ActorId,
                "routing.simple-set", intent.IdempotencyKey, intentHash);
            operation.Complete(200, JsonSerializer.Serialize(view, Json), succeeded: true, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return view;
        }, cancellationToken);

    private static bool IsAdvancedRule(RoutingRule rule) =>
        rule.Method == "any" || rule.OriginatorId is not null
        || rule.MinAmount is not null || rule.MaxAmount is not null;

    private static SimpleRoutingView ProjectSimpleRouting(RoutingRuleset draft, bool advancedReadOnly) => new(
        draft.MerchantId, draft.Id, RulesetStatusCode(draft.Status), advancedReadOnly,
        draft.Rules.OrderBy(r => r.Priority)
            .Select(r => new SimpleRoutingRuleView(r.Method, r.TargetConnectionId, r.FallbackConnectionId)).ToList(),
        draft.Version);

    /// <summary>Turns simple rows into full specs, emitting <c>validation_failed</c> at the request boundary
    /// BEFORE the domain guards (which throw bare <see cref="ArgumentException"/> without a code). Rejects a
    /// non-canonical method (incl. <c>any</c>), a duplicate method, an empty/equal primary/fallback (AC-5.3
    /// #8), and a connection that is unknown, out of the merchant, disabled, in the wrong environment (AC-5.3
    /// #9) or without the method enabled at both the account and the merchant policy level (AC-5.3).</summary>
    private async Task<IReadOnlyList<RoutingRuleSpec>> BuildSimpleSpecsAsync(
        Guid merchantId, IReadOnlyList<SimpleRoutingRuleRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            throw new InvalidRequestException("At least one routing row is required.", "validation_failed");
        var merchant = await LoadMerchantAsync(merchantId, ct);
        var specs = new List<RoutingRuleSpec>(rows.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var priority = 1;
        foreach (var row in rows)
        {
            var method = NormalizeMethod(row.Method);
            if (!seen.Add(method))
                throw new InvalidRequestException("A method appears more than once.", "validation_failed");
            if (row.PrimaryConnectionId == Guid.Empty)
                throw new InvalidRequestException("A routing row requires a primary connection.", "validation_failed");
            if (row.FallbackConnectionId == row.PrimaryConnectionId)
                throw new InvalidRequestException("Primary and fallback connections must differ.", "validation_failed");
            await ValidateSimpleEligibleAsync(merchantId, row.PrimaryConnectionId, method, merchant.PaymentEnvironment, ct);
            if (row.FallbackConnectionId is { } fallback)
                await ValidateSimpleEligibleAsync(merchantId, fallback, method, merchant.PaymentEnvironment, ct);
            specs.Add(new RoutingRuleSpec(
                priority++, method, null, null, null, row.PrimaryConnectionId, row.FallbackConnectionId, true));
        }
        return specs;
    }

    private async Task ValidateSimpleEligibleAsync(
        Guid merchantId, Guid connectionId, string method, PspEnvironment environment, CancellationToken ct)
    {
        var connection = await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId && x.MerchantId == merchantId, token), ct);
        if (connection is null)
            throw new InvalidRequestException("Routing connection is unknown for the merchant.", "validation_failed");
        if (!connection.IsEnabled)
            throw new InvalidRequestException("Routing connection is disabled.", "validation_failed");
        if (connection.ActiveSecretVersionId is null)
            throw new InvalidRequestException("Routing connection has no active credential.", "validation_failed");
        if (connection.ActiveSecretEnvironment != environment)
            throw new InvalidRequestException(
                "Routing connection environment does not match the merchant payment environment.", "validation_failed");
        var provider = await LoadProviderAsync(connection.Psp, ct);
        var methods = await ProjectConnectionMethodsAsync(connection, provider, ct);
        if (!methods.Any(x => x.Method == method && x.Available))
            throw new InvalidRequestException(
                "Routing connection does not have the method enabled at the account level.", "validation_failed");
        if (!await MerchantMethodEnabledAsync(merchantId, method, ct))
            throw new InvalidRequestException(
                "The method is not enabled at the merchant policy level.", "validation_failed");
    }

    private async Task<bool> MerchantMethodEnabledAsync(Guid merchantId, string method, CancellationToken ct)
    {
        var methodId = MethodId(method);
        var row = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x => x.MerchantId == merchantId && x.PaymentMethodId == methodId, token), ct);
        return row?.IsEnabled == true;
    }

    /// <summary>REQ-6.17 / AC-5.4: every merchant-level enabled method must be covered by an enabled rule with
    /// a primary connection (its own method or the catch-all <c>any</c>), else the activation is refused.</summary>
    private async Task EnsureRoutingCoverageAsync(Guid merchantId, RoutingRuleset ruleset, CancellationToken ct)
    {
        var enabledMethodIds = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
            .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId && x.IsEnabled)
            .Select(x => x.PaymentMethodId).ToListAsync(token), ct);
        foreach (var methodId in enabledMethodIds)
        {
            var code = MethodCode(methodId);
            if (code is null)
                continue;
            var covered = ruleset.Rules.Any(r => r.Enabled
                && (r.Method == code || r.Method == "any") && r.TargetConnectionId != Guid.Empty);
            if (!covered)
                throw new ConflictException("An enabled merchant method has no primary route.", "routing_incomplete");
        }
    }

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

        var merchant = await LoadMerchantAsync(merchantId, ct);
        foreach (var rule in specs.Where(x => x.Enabled))
        {
            await ValidateEligibleAsync(connections.Single(x => x.Id == rule.TargetConnectionId), rule.Method, merchant.PaymentEnvironment, ct);
            if (rule.FallbackConnectionId is { } fallback)
                await ValidateEligibleAsync(connections.Single(x => x.Id == fallback), rule.Method, merchant.PaymentEnvironment, ct);
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

    /// <summary>Local routing eligibility (REQ-6.6/6.7/6.14): enabled connection, a credential reference in
    /// the merchant's environment, and — from the NORMALIZED account-method rows, never the CSV projection
    /// (REQ-5.8) — an enabled account method the adapter has sandbox evidence for. Health is deliberately
    /// not consulted (REQ-6.15) and no probe is run (REQ-6.16).</summary>
    private async Task ValidateEligibleAsync(
        Connection connection, string method, PspEnvironment environment, CancellationToken ct)
    {
        if (!connection.IsEnabled)
            throw new InvalidRequestException("Routing references a disabled PSP connection.", "routing_invalid");
        if (connection.ActiveSecretVersionId is null)
            throw new InvalidRequestException("Routing connection has no active credential.", "routing_invalid");
        if (connection.ActiveSecretEnvironment != environment)
            throw new InvalidRequestException(
                "Routing connection credential environment does not match the merchant payment environment.", "routing_invalid");
        var provider = await LoadProviderAsync(connection.Psp, ct);
        var methods = await ProjectConnectionMethodsAsync(connection, provider, ct);
        var eligible = method == "any"
            ? methods.Any(x => x.Available)
            : methods.Any(x => x.Method == method && x.Available);
        if (!eligible)
            throw new InvalidRequestException(
                method == "any"
                    ? "Routing connection has no eligible method."
                    : "Routing connection does not support the selected method.", "routing_invalid");
    }

    private static readonly string[] CanonicalMethods =
        [PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment];

    /// <summary>Per canonical method: the account-level switch, adapter evidence and the backend decision
    /// with its first blocking reason (REQ-5.4/5.5/5.11/5.16). The console renders this; it never infers
    /// availability per provider on its own.</summary>
    private async Task<IReadOnlyList<PspConnectionMethodView>> ProjectConnectionMethodsAsync(
        Connection connection, ProviderCatalogRow provider, CancellationToken ct)
    {
        // Tracked rows win over the snapshot: create/update project the view inside the same transaction,
        // before SaveChanges, so rows added or flipped a moment ago are only in the change tracker.
        var rows = (await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
                .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == connection.MerchantId
                    && x.PspConnectionId == connection.Id).ToListAsync(token), ct))
            .Where(x => db.MerchantProviderAccountMethods.Local.All(local => local.Id != x.Id))
            .Concat(db.MerchantProviderAccountMethods.Local.Where(x =>
                x.MerchantId == connection.MerchantId && x.PspConnectionId == connection.Id))
            .ToList();
        var adapter = adapterFactory.For(connection.Psp);
        var result = new List<PspConnectionMethodView>(CanonicalMethods.Length);
        foreach (var method in CanonicalMethods)
        {
            var catalog = await LoadProviderMethodAsync(provider, method, ct);
            var row = catalog is null ? null : rows.SingleOrDefault(x => x.PaymentMethodId == catalog.PaymentMethodId);
            var accountEnabled = row?.IsEnabled == true;
            var verified = adapter.SupportedMethods.Contains(method);
            var denial = AccountMethodDenial(connection, provider, catalog, verified, accountEnabled);
            result.Add(new PspConnectionMethodView(method, accountEnabled, verified, denial is null, denial));
        }
        return result;
    }

    private static string? AccountMethodDenial(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow? catalog,
        bool adapterVerified, bool accountEnabled)
    {
        if (!connection.IsEnabled) return "connection_disabled";
        if (!provider.IsEnabled) return "provider_disabled";
        if (catalog is null || !catalog.MethodIsActive) return "method_inactive";
        if (catalog.PaymentProviderMethodId is null || !catalog.ProviderMethodIsActive) return "provider_method_unavailable";
        if (!adapterVerified) return "adapter_unverified";
        if (!accountEnabled) return "account_method_disabled";
        return null;
    }

    private static string DenialCode(PaymentCapabilityDenial denial) => denial switch
    {
        PaymentCapabilityDenial.None => throw new ArgumentOutOfRangeException(nameof(denial)),
        PaymentCapabilityDenial.UserNotActive => "user_not_active",
        PaymentCapabilityDenial.UserPolicyDenied => "user_policy_denied",
        PaymentCapabilityDenial.MerchantUnavailable => "merchant_unavailable",
        PaymentCapabilityDenial.MethodUnavailable => "method_unavailable",
        PaymentCapabilityDenial.ProviderUnavailable => "provider_unavailable",
        PaymentCapabilityDenial.AccountUnavailable => "account_unavailable",
        PaymentCapabilityDenial.AdapterUnsupported => "adapter_unverified",
        _ => throw new ArgumentOutOfRangeException(nameof(denial)),
    };

    private async Task<PspConnectionView> ProjectConnectionAsync(Connection x, CancellationToken ct)
    {
        var environment = await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == x.MerchantId).Select(m => m.PaymentEnvironment).SingleAsync(token), ct);
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
        var methods = await ProjectConnectionMethodsAsync(x, await LoadProviderAsync(x.Psp, ct), ct);
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
            x.LastTestResult, capabilities, x.PendingApprovalId is not null, x.CreatedAt, x.Version,
            environment.ToCode(), x.ActiveSecretEnvironment.ToCode(), adapter.CallbackUrlFor(x.Id),
            x.PendingSecretTestResult is { } testResult && x.PendingSecretTestedAt is { } testedAt
                ? new PspCredentialTestView(testResult, testedAt)
                : null,
            new WebhookRegistrationView(x.WebhookRegistrationHash is not null, x.WebhookRegisteredAt),
            methods);
    }

    private async Task<PspConnectionView> ReplayConnectionAsync(OperationRecord record, CancellationToken ct)
    {
        var stored = Replay<PspConnectionView>(record);
        return await GetConnectionAsync(stored.PspConnectionId, stored.MerchantId,
            new AdminPaymentsAccess(record.ActorId, 0, true, new HashSet<Guid>()), ct) ?? stored;
    }

    private OperationRecord BeginOperation(Guid merchantId, Guid actorId, string operation, string key, string hash)
    {
        var now = clock.UtcNow;
        var record = OperationRecord.Create(
            actorId, operation, key, hash, GovernanceScopeKind.Merchant, merchantId, now, now.AddHours(24));
        db.OperationRecords.Add(record);
        return record;
    }

    private async Task<OperationRecord?> FindOperationAsync(
        Guid merchantId, Guid actorId, string operation, string key, string hash, CancellationToken ct)
    {
        ValidateKey(key);
        var record = await PlatformReadGuard.ReadAsync(token => db.OperationRecords
            .AsNoTracking().SingleOrDefaultAsync(x =>
                x.MerchantId == merchantId && x.ActorId == actorId && x.Operation == operation
                && x.IdempotencyKey == key, token), ct);
        if (record is null)
            return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.RequestHash), Encoding.ASCII.GetBytes(hash)))
            throw new ConflictException("Idempotency key was reused with a different intent.", "idempotency_key_reused");
        if (record.Status != OperationStatus.Succeeded || record.ResponseBody is null)
            throw new ConflictException("The operation is still in progress or has an unknown outcome.", "operation_in_progress");
        return record;
    }

    private void EnqueueApproval(ApprovalRequested message)
    {
        db.GovernanceOutboxMessages.Add(GovernanceOutboxMessage.Create(
            message.EventId, GovernanceScopeKind.Merchant, message.MerchantId,
            ApprovalRequested.EventType, ApprovalRequested.SchemaVersion,
            JsonSerializer.Serialize(message, Json), message.OccurredAt));
    }

    private async Task<Connection> LoadConnectionAsync(Guid connectionId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.PspConnections.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.Id == connectionId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("PSP connection was not found.");

    private async Task<RoutingRuleset> LoadRulesetAsync(Guid rulesetId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.RoutingRulesets.IgnoreQueryFilters().Include(x => x.Rules)
            .SingleOrDefaultAsync(x => x.Id == rulesetId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("Routing ruleset was not found.");

    private async Task<Merchant> LoadMerchantAsync(Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == merchantId, token), ct)
        ?? throw new NotFoundException("Merchant was not found.");

    private async Task<Merchant> LoadMerchantForUpdateAsync(Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == merchantId, token), ct)
        ?? throw new NotFoundException("Merchant was not found.");

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
        Guid merchantId, string method, MerchantPaymentMethod? row, PaymentMethodDecision decision) => new(
        merchantId, method, row?.IsEnabled == true, decision.Allowed,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0,
        decision.Allowed ? null : DenialCode(decision.Denial));

    private static MerchantUserPaymentMethodView UserPolicyView(
        Guid merchantUserId, Guid merchantId, string method,
        MerchantUserPaymentMethod? row, bool effective) => new(
        merchantUserId, merchantId, method, row?.IsEnabled == true, effective,
        row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0);

    private AccountPaymentCapabilityView AccountMethodView(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow method,
        MerchantProviderAccountMethod? row)
    {
        var verified = adapterFactory.For(connection.Psp).SupportedMethods.Contains(method.MethodCode);
        return new(
            "account-method", connection.Id, connection.MerchantId, provider.ProviderCode,
            method.MethodCode, null, row?.IsEnabled == true,
            row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0,
            verified, AccountMethodDenial(connection, provider, method, verified, row?.IsEnabled == true));
    }

    private AccountPaymentCapabilityView AccountOptionView(
        Connection connection, ProviderCatalogRow provider, ProviderMethodCatalogRow method,
        ProviderMethodOptionCatalogRow option, MerchantProviderAccountMethodOption? row)
    {
        var verified = adapterFactory.For(connection.Psp).SupportedMethods.Contains(method.MethodCode);
        return new(
            "account-method-option", connection.Id, connection.MerchantId, provider.ProviderCode,
            method.MethodCode, option.OptionCode, row?.IsEnabled == true,
            row?.UpdatedBy ?? row?.CreatedBy, row?.UpdatedAt ?? row?.CreatedAt, row?.Version ?? 0,
            verified, verified ? null : "adapter_unverified");
    }

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

    private static T Replay<T>(OperationRecord record) =>
        JsonSerializer.Deserialize<T>(record.ResponseBody!, Json)
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

    private static readonly IReadOnlyDictionary<Code, string[]> SecretFieldAllowlist =
        new Dictionary<Code, string[]>
        {
            [Code.TwoCTwoP] = ["secretKey"],
            [Code.Omise] = ["secretKey", "publicKey", "webhookSecret"],
        };

    private const int SecretFieldMaxLength = 4_096;

    /// <summary>Request-boundary credential checks, all BEFORE any vault write (REQ-4.1/4.2/4.7/4.8/4.10/4.11):
    /// only the provider's allowlisted field names, every value at most 4,096 chars, the provider's required
    /// fields present, and an Omise key whose prefix matches the merchant's environment.</summary>
    internal static void ValidateSecretFields(
        Code psp, IReadOnlyDictionary<string, string> secrets, string? pspMerchantId, PspEnvironment environment)
    {
        var allowed = SecretFieldAllowlist[psp];
        foreach (var (name, value) in secrets)
        {
            if (!allowed.Contains(name, StringComparer.Ordinal))
                throw new InvalidRequestException($"Credential field '{name}' is not allowed for {psp.ToCode()}.", "validation_failed");
            if (value is null || value.Length > SecretFieldMaxLength)
                throw new InvalidRequestException($"Credential field '{name}' exceeds {SecretFieldMaxLength} characters.", "validation_failed");
        }
        if (pspMerchantId is { Length: > SecretFieldMaxLength })
            throw new InvalidRequestException($"pspMerchantId exceeds {SecretFieldMaxLength} characters.", "validation_failed");
        if (!secrets.TryGetValue("secretKey", out var secretKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidRequestException("secretKey is required.", "validation_failed");
        if (psp == Code.TwoCTwoP && string.IsNullOrWhiteSpace(pspMerchantId))
            throw new InvalidRequestException("pspMerchantId is required for 2c2p.", "validation_failed");
        if (psp == Code.Omise && !OmiseSecretKeys.MatchesEnvironment(secretKey.Trim(), environment))
            throw new InvalidRequestException(
                $"Omise secret key prefix does not match the {environment.ToCode()} payment environment.", "validation_failed");
    }

    private IReadOnlyList<string> ValidateMethods(Code psp, IReadOnlyList<string> values)
    {
        // Empty is allowed: a zero-method connection holds credentials so the provider can be tested before
        // any capability is granted (REQ-3.3); normalized account-method rows stay the authorization source.
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

    private static Code ParseProviderId(Guid providerId) => providerId switch
    {
        var value when value == PaymentCapabilityIds.TwoCTwoP => Code.TwoCTwoP,
        var value when value == PaymentCapabilityIds.Omise => Code.Omise,
        _ => throw new InvalidRequestException("Payment provider is invalid.", "invalid_psp_config"),
    };

    private static PspEnvironment ParseEnvironment(string value)
    {
        try { return PspEnvironments.FromCode(value); }
        catch (ArgumentException ex) { throw new InvalidRequestException(ex.Message, "validation_failed"); }
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
