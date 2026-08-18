using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using Merchants.Application.AdminControlPlane;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime.Payments;

namespace Persistence.MerchantRuntime.Merchants;

internal sealed class AdminMerchantControlStore(
    MerchantRuntimeDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    IPspAdapterFactory adapterFactory,
    PaymentAuthorizationSqlLockManager? authorizationLocks = null) : IAdminMerchantControlStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private PaymentAuthorizationSqlLockManager AuthorizationLocks { get; } =
        authorizationLocks ?? new PaymentAuthorizationSqlLockManager(db);

    public async Task<PagedResult<AdminMerchantListItem>> ListMerchantsAsync(
        AdminMerchantListQuery query, CancellationToken cancellationToken)
    {
        var source = db.Merchants.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.Id));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseMerchantStatus(query.Status);
            source = source.Where(x => x.Status == status);
        }

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        return new PagedResult<AdminMerchantListItem>(rows.Select(Project).ToList(), query.Page, query.Limit, total);
    }

    public Task<AdminMutationResult<AdminMerchantListItem>> UpdateMerchantAsync(
        AdminMerchantMutation mutation, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(mutation.Access, mutation.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(mutation.MerchantId, ct);
            var intentHash = Hash(new
            {
                mutation.MerchantId,
                mutation.Name,
                mutation.Note,
                mutation.EnabledChannels,
                metadata = mutation.Metadata?.GetRawText(),
                mutation.ExpectedVersion,
            });
            var prior = await FindOperationAsync(mutation.MerchantId, mutation.Access.ActorId,
                "merchant.update", mutation.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<AdminMerchantListItem>(prior);

            var merchant = await LoadMerchantAsync(mutation.MerchantId, ct);
            EnsureVersion(merchant.Version, mutation.ExpectedVersion);
            var operation = BeginOperation(mutation.MerchantId, mutation.Access.ActorId,
                "merchant.update", mutation.IdempotencyKey, intentHash);
            var channels = mutation.EnabledChannels.Select(PaymentMethods.Normalize)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            await SyncMerchantPoliciesAsync(merchant.Id, channels, mutation.Access.ActorId, ct);
            merchant.Update(mutation.Name, mutation.Note, channels,
                mutation.Metadata?.GetRawText());
            var view = Project(merchant);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), merchant.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new AdminMutationResult<AdminMerchantListItem>(view, false);
        }, cancellationToken);

    public Task<AdminMutationResult<AdminMerchantListItem>> ChangeMerchantStatusAsync(
        AdminMerchantStatusMutation mutation, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            EnsureAccess(mutation.Access, mutation.MerchantId);
            await AuthorizationLocks.AcquireMerchantExclusiveAsync(mutation.MerchantId, ct);
            var operationName = mutation.Activate ? "merchant.reactivate" : "merchant.suspend";
            var intentHash = Hash(new { mutation.MerchantId, mutation.Activate, mutation.ExpectedVersion });
            var prior = await FindOperationAsync(mutation.MerchantId, mutation.Access.ActorId,
                operationName, mutation.IdempotencyKey, intentHash, ct);
            if (prior is not null)
                return Replay<AdminMerchantListItem>(prior);

            var merchant = await LoadMerchantAsync(mutation.MerchantId, ct);
            EnsureVersion(merchant.Version, mutation.ExpectedVersion);
            var operation = BeginOperation(mutation.MerchantId, mutation.Access.ActorId,
                operationName, mutation.IdempotencyKey, intentHash);
            if (mutation.Activate) merchant.Reactivate(); else merchant.Suspend();
            var view = Project(merchant);
            operation.Succeed(200, JsonSerializer.Serialize(view, Json), merchant.Id.ToString("D"));
            await unitOfWork.SaveChangesAsync(ct);
            return new AdminMutationResult<AdminMerchantListItem>(view, false);
        }, cancellationToken);

    public async Task<PagedResult<OriginatorView>> ListOriginatorsAsync(
        OriginatorListQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } merchantId)
            EnsureAccess(query.Access, merchantId);
        var source = db.Originators.IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } selected)
            source = source.Where(x => x.MerchantId == selected);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var type = ParseType(query.Type);
            source = source.Where(x => x.Type == type);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = ParseOriginatorStatus(query.Status);
            source = source.Where(x => x.Status == status);
        }
        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.OrderBy(x => x.MerchantId)
            .ThenBy(x => x.Code).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.Limit).Take(query.Limit).ToListAsync(ct), cancellationToken);
        return new PagedResult<OriginatorView>(rows.Select(Project).ToList(), query.Page, query.Limit, total);
    }

    public async Task<OriginatorView?> GetOriginatorAsync(
        Guid originatorId, Guid? expectedMerchantId, AdminMerchantAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.Originators.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == originatorId && (expectedMerchantId == null || x.MerchantId == expectedMerchantId))
            .SingleOrDefaultAsync(ct), cancellationToken);
        return row is null || !access.Allows(row.MerchantId) ? null : Project(row);
    }

    public async Task<OriginatorView> CreateOriginatorAsync(
        CreateOriginatorIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        await EnsureMerchantExistsAsync(intent.MerchantId, cancellationToken);
        var entity = Originator.Create(intent.MerchantId, intent.Code, intent.Name, ParseType(intent.Type),
            intent.SaleCode, intent.LinkedApiClientId, clock.UtcNow);
        db.Originators.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Project(entity);
    }

    public async Task<OriginatorView> UpdateOriginatorAsync(
        UpdateOriginatorIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var entity = await LoadOriginatorAsync(intent.OriginatorId, intent.MerchantId, cancellationToken);
        EnsureVersion(entity.Version, intent.ExpectedVersion);
        entity.Update(intent.Name, ParseType(intent.Type), intent.SaleCode, intent.LinkedApiClientId, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Project(entity);
    }

    public async Task<OriginatorView> SetOriginatorStateAsync(
        OriginatorStateIntent intent, CancellationToken cancellationToken)
    {
        EnsureAccess(intent.Access, intent.MerchantId);
        var entity = await LoadOriginatorAsync(intent.OriginatorId, intent.MerchantId, cancellationToken);
        EnsureVersion(entity.Version, intent.ExpectedVersion);
        if (intent.Enable) entity.Enable(clock.UtcNow); else entity.Disable(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Project(entity);
    }

    public async Task DeleteOriginatorAsync(
        Guid originatorId, Guid merchantId, long expectedVersion, AdminMerchantAccess access,
        CancellationToken cancellationToken)
    {
        EnsureAccess(access, merchantId);
        var entity = await LoadOriginatorAsync(originatorId, merchantId, cancellationToken);
        EnsureVersion(entity.Version, expectedVersion);
        var referenced = await PlatformReadGuard.ReadAsync(ct => db.RoutingRules.IgnoreQueryFilters()
            .AnyAsync(x => x.MerchantId == merchantId && x.OriginatorId == originatorId, ct), cancellationToken);
        if (referenced)
            entity.Disable(clock.UtcNow);
        else
            db.Originators.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private AdminOperationRecord BeginOperation(
        Guid merchantId, Guid actorId, string operation, string key, string hash)
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

    private static AdminMutationResult<T> Replay<T>(AdminOperationRecord record) => new(
        JsonSerializer.Deserialize<T>(record.Result!, Json)
            ?? throw new InvalidOperationException("Stored operation result is invalid."), true);

    private async Task<Merchant> LoadMerchantAsync(Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == merchantId, token), ct)
        ?? throw new NotFoundException("Merchant was not found.");

    private async Task SyncMerchantPoliciesAsync(
        Guid merchantId,
        IReadOnlyCollection<string> channels,
        Guid actorId,
        CancellationToken ct)
    {
        // SQLite-only unit tests do not carry cfg catalog tables. SQL Server rollout always runs additive
        // backfill before compatibility binary accepts this legacy facade.
        if (!db.Database.IsSqlServer())
            return;

        var requested = channels.Select(MethodId).ToHashSet();
        foreach (var channel in channels)
        {
            var methodId = MethodId(channel);
            var accounts = await PlatformReadGuard.ReadAsync(token => db.MerchantProviderAccountMethods
                .IgnoreQueryFilters().AsNoTracking().Where(x => x.MerchantId == merchantId
                    && x.PaymentMethodId == methodId && x.IsEnabled).ToListAsync(token), ct);
            var qualifying = false;
            foreach (var account in accounts)
            {
                var connection = await PlatformReadGuard.ReadAsync(token => db.PspConnections
                    .IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.Id == account.PspConnectionId
                        && x.MerchantId == merchantId && x.IsEnabled
                        && x.PaymentProviderId == account.PaymentProviderId, token), ct);
                if (connection is null || !adapterFactory.For(connection.Psp).SupportedMethods.Contains(channel))
                    continue;
                var query = db.Database.SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value]
                    FROM [cfg].[PaymentMethods] m
                    JOIN [cfg].[PaymentProviders] p ON p.[Id] = {account.PaymentProviderId}
                    JOIN [cfg].[PaymentProviderMethods] pm
                      ON pm.[Id] = {account.PaymentProviderMethodId}
                     AND pm.[PaymentProviderId] = p.[Id]
                     AND pm.[PaymentMethodId] = m.[Id]
                    WHERE m.[Id] = {methodId} AND m.[IsActive] = CAST(1 AS bit)
                      AND p.[IsEnabled] = CAST(1 AS bit) AND pm.[IsActive] = CAST(1 AS bit)
                    """);
                var active = await PlatformReadGuard.ReadAsync(token => query.SingleAsync(token), ct);
                if (active == 1)
                {
                    qualifying = true;
                    break;
                }
            }
            if (!qualifying)
                throw new PaymentCapabilityUnavailableException(
                    $"Merchant method '{channel}' has no active qualifying provider account.");
        }

        var existing = await PlatformReadGuard.ReadAsync(token => db.MerchantPaymentMethods
            .IgnoreQueryFilters().Where(x => x.MerchantId == merchantId).ToListAsync(token), ct);
        foreach (var row in existing)
            row.SetEnabled(requested.Remove(row.PaymentMethodId), actorId, clock.UtcNow);
        foreach (var methodId in requested)
            db.MerchantPaymentMethods.Add(MerchantPaymentMethod.Create(
                merchantId, methodId, true, actorId, clock.UtcNow));
    }

    private static Guid MethodId(string method) => method switch
    {
        PaymentMethods.Card => PaymentCapabilityIds.Card,
        PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
        PaymentMethods.Installment => PaymentCapabilityIds.Installment,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private async Task EnsureMerchantExistsAsync(Guid merchantId, CancellationToken ct)
    {
        if (!await PlatformReadGuard.ReadAsync(token => db.Merchants.IgnoreQueryFilters()
                .AnyAsync(x => x.Id == merchantId, token), ct))
            throw new NotFoundException("Merchant was not found.");
    }

    private async Task<Originator> LoadOriginatorAsync(Guid originatorId, Guid merchantId, CancellationToken ct) =>
        await PlatformReadGuard.ReadAsync(token => db.Originators.IgnoreQueryFilters().SingleOrDefaultAsync(
            x => x.Id == originatorId && x.MerchantId == merchantId, token), ct)
        ?? throw new NotFoundException("Originator was not found.");

    private static AdminMerchantListItem Project(Merchant x) => new(
        x.Id, x.Code, x.Name, x.Status == MerchantStatus.Active ? "active" : "suspended",
        x.Country, x.Currency, x.EnabledChannels, x.CreatedAt, x.Version);

    private static OriginatorView Project(Originator x) => new(
        x.Id, x.MerchantId, x.Code, x.Name, TypeCode(x.Type), x.SaleCode, x.ApiClientId,
        x.Status == OriginatorStatus.Active ? "active" : "inactive", x.CreatedAt, x.UpdatedAt, x.Version);

    private static OriginatorType ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "branch" => OriginatorType.Branch,
        "agent" => OriginatorType.Agent,
        "broker" => OriginatorType.Broker,
        "staff" => OriginatorType.Staff,
        "app" => OriginatorType.App,
        _ => throw new InvalidRequestException("Originator type is invalid.", "invalid_type"),
    };

    private static string TypeCode(OriginatorType type) => type switch
    {
        OriginatorType.Branch => "branch",
        OriginatorType.Agent => "agent",
        OriginatorType.Broker => "broker",
        OriginatorType.Staff => "staff",
        OriginatorType.App => "app",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static MerchantStatus ParseMerchantStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "active" => MerchantStatus.Active,
        "suspended" or "inactive" => MerchantStatus.Inactive,
        _ => throw new InvalidRequestException("Merchant status is invalid.", "invalid_filter"),
    };

    private static OriginatorStatus ParseOriginatorStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "active" => OriginatorStatus.Active,
        "inactive" => OriginatorStatus.Inactive,
        _ => throw new InvalidRequestException("Originator status is invalid.", "invalid_filter"),
    };

    private static void EnsureAccess(AdminMerchantAccess access, Guid merchantId)
    {
        if (!access.Allows(merchantId))
            throw new AdminMerchantAccessDeniedException("Merchant is outside the current admin scope.");
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
}
