using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments.Capabilities;

internal sealed class PaymentCapabilityMigrationService(
    MerchantRuntimeDbContext db,
    IUnitOfWork unitOfWork,
    PaymentAuthorizationSqlLockManager locks,
    IPspAdapterFactory adapters) : IPaymentCapabilityMigration
{
    private static readonly string[] ConflictKinds =
        ["provider-binding", "account-method", "merchant-method", "order-creator", "adapter-drift"];

    public Task<PaymentCapabilityMigrationReport> BackfillAsync(
        Guid actorId,
        CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireGlobalExclusiveAsync(ct);
            var state = await StateAsync(ct)
                ?? throw new InvalidOperationException("Payment authorization state row is missing.");
            if (state.Mode != (int)PaymentAuthorizationMode.LegacyRead)
                throw new InvalidOperationException(
                    "Compatibility backfill is available only before normalized authorization cutover.");
            var now = await DatabaseUtcNowAsync(ct);
            await ReconcileAsync(actorId, now, projectLegacy: false, ct);
            return await ReportAsync(ct);
        }, cancellationToken);
    }

    public async Task<PaymentCapabilityMigrationReport> CutoverAsync(
        Guid actorId,
        bool oldInstancesDrained,
        CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        if (!oldInstancesDrained)
            throw new PaymentAuthorizationCutoverBlockedException(
                "Authorization cutover requires confirmed old-instance drain.");

        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await locks.AcquireGlobalExclusiveAsync(ct);
                var state = await StateAsync(ct)
                    ?? throw new PaymentAuthorizationCutoverBlockedException(
                        "Payment authorization state row is missing.");
                if (state.Mode == (int)PaymentAuthorizationMode.NormalizedRead)
                    return await ReportAsync(ct);

                var cutoff = await DatabaseUtcNowAsync(ct);
                await ReconcileAsync(actorId, cutoff, projectLegacy: true, ct);
                await VerifyAsync(ct);
                await TightenAsync(ct);

                var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [cfg].[PaymentAuthorizationStates]
                    SET [Mode] = {(int)PaymentAuthorizationMode.NormalizedRead},
                        [CutoffAt] = {cutoff}, [Version] = [Version] + 1
                    WHERE [Id] = {PaymentCapabilityIds.AuthorizationState}
                      AND [Mode] IN ({(int)PaymentAuthorizationMode.LegacyRead},
                                     {(int)PaymentAuthorizationMode.FailClosed});
                    """, ct);
                if (changed != 1)
                    throw new PaymentAuthorizationCutoverBlockedException(
                        "Payment authorization mode changed during cutover.");
                return await ReportAsync(ct);
            }, cancellationToken);
        }
        catch (PaymentAuthorizationCutoverBlockedException)
        {
            // Final transaction must roll back entirely. Re-scan once after rollback so any newly detected
            // delta conflict remains durable for remediation while mode stays unchanged.
            await BackfillAsync(actorId, cancellationToken);
            throw;
        }
    }

    public Task<PaymentAuthorizationMode> PrepareRollbackAsync(
        Guid actorId,
        bool normalizedAwareBinaryAvailable,
        CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireGlobalExclusiveAsync(ct);
            var state = await StateAsync(ct)
                ?? throw new InvalidOperationException("Payment authorization state row is missing.");
            var mode = (PaymentAuthorizationMode)state.Mode;
            if (mode == PaymentAuthorizationMode.LegacyRead)
                return mode;

            var target = normalizedAwareBinaryAvailable
                ? PaymentAuthorizationMode.NormalizedRead
                : PaymentAuthorizationMode.FailClosed;
            if (mode != target)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [cfg].[PaymentAuthorizationStates]
                    SET [Mode] = {(int)target}, [Version] = [Version] + 1
                    WHERE [Id] = {PaymentCapabilityIds.AuthorizationState};
                    """, ct);
            return target;
        }, cancellationToken);
    }

    private async Task ReconcileAsync(
        Guid actorId,
        DateTime now,
        bool projectLegacy,
        CancellationToken ct)
    {
        await ResolvePriorConflictsAsync(actorId, now, ct);
        var currentConflicts = new HashSet<(string Kind, Guid EntityId)>();
        await ReconcileAccountsAsync(actorId, now, projectLegacy, currentConflicts, ct);
        await ReconcileMerchantsAsync(actorId, now, projectLegacy, currentConflicts, ct);
        await ReconcileUsersAsync(actorId, now, ct);
        await ReconcileOrdersAsync(now, currentConflicts, ct);
    }

    private async Task ReconcileAccountsAsync(
        Guid actorId,
        DateTime now,
        bool projectLegacy,
        HashSet<(string Kind, Guid EntityId)> conflicts,
        CancellationToken ct)
    {
        var catalog = (await db.Database.SqlQuery<ProviderMethodRow>($"""
            SELECT pm.[Id] AS [ProviderMethodId], pm.[PaymentProviderId], pm.[PaymentMethodId],
                   pm.[IsActive] AS [ProviderMethodActive], p.[AdapterCode],
                   p.[IsEnabled] AS [ProviderActive], m.[Code] AS [MethodCode],
                   m.[IsActive] AS [MethodActive]
            FROM [cfg].[PaymentProviderMethods] pm
            JOIN [cfg].[PaymentProviders] p ON p.[Id] = pm.[PaymentProviderId]
            JOIN [cfg].[PaymentMethods] m ON m.[Id] = pm.[PaymentMethodId]
            """).ToListAsync(ct)).ToDictionary(x => (x.PaymentProviderId, x.PaymentMethodId));
        var connections = await db.Database.SqlQuery<ConnectionRow>($"""
            SELECT [Id], [MerchantId], [Psp], [PaymentProviderId], [EnabledMethods], [IsEnabled]
            FROM [txn].[PspConnections]
            ORDER BY [MerchantId], [Id]
            """).ToListAsync(ct);

        foreach (var connection in connections)
        {
            if (!TryProvider((Code)connection.Psp, out var providerId))
            {
                await AddConflictAsync("provider-binding", connection.MerchantId, connection.Id,
                    "PSP connection has an unknown provider discriminator.", now, conflicts, ct);
                continue;
            }
            if (connection.PaymentProviderId is { } bound && bound != providerId)
            {
                await AddConflictAsync("provider-binding", connection.MerchantId, connection.Id,
                    "PSP connection provider binding contradicts its adapter discriminator.", now, conflicts, ct);
                continue;
            }
            if (connection.PaymentProviderId is null)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [txn].[PspConnections]
                    SET [PaymentProviderId] = {providerId}, [Version] = [Version] + 1
                    WHERE [Id] = {connection.Id} AND [PaymentProviderId] IS NULL;
                    """, ct);

            var parsed = ParseLegacy(connection.EnabledMethods);
            if (parsed.HasUnknown)
                await AddConflictAsync("account-method", connection.MerchantId, connection.Id,
                    "PSP connection contains an unknown legacy payment method.", now, conflicts, ct);

            var adapter = adapters.For((Code)connection.Psp);
            var desired = new Dictionary<Guid, ProviderMethodRow>();
            foreach (var code in parsed.Known)
            {
                var methodId = MethodId(code);
                if (!adapter.SupportedMethods.Contains(code)
                    || !catalog.TryGetValue((providerId, methodId), out var providerMethod)
                    || !providerMethod.ProviderActive || !providerMethod.ProviderMethodActive
                    || !providerMethod.MethodActive || providerMethod.AdapterCode != connection.Psp)
                {
                    await AddConflictAsync("adapter-drift", connection.MerchantId, connection.Id,
                        "Legacy account method exceeds registered adapter or provider capability.",
                        now, conflicts, ct);
                    continue;
                }
                desired[methodId] = providerMethod;
            }

            var existing = await db.Database.SqlQuery<AccountMethodRow>($"""
                SELECT [Id], [PaymentProviderId], [PaymentProviderMethodId], [PaymentMethodId], [IsEnabled]
                FROM [txn].[MerchantProviderAccountMethods]
                WHERE [PspConnectionId] = {connection.Id}
                """).ToListAsync(ct);
            foreach (var row in existing)
            {
                var enable = desired.TryGetValue(row.PaymentMethodId, out var expected)
                    && row.PaymentProviderId == providerId
                    && row.PaymentProviderMethodId == expected.ProviderMethodId;
                if (desired.ContainsKey(row.PaymentMethodId) && !enable)
                    await AddConflictAsync("account-method", connection.MerchantId, connection.Id,
                        "Normalized account method has a conflicting provider chain.", now, conflicts, ct);
                await SetEnabledAsync("MerchantProviderAccountMethods", row.Id, enable, actorId, now, ct);
                if (enable)
                    desired.Remove(row.PaymentMethodId);
            }
            foreach (var (methodId, providerMethod) in desired)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT [txn].[MerchantProviderAccountMethods]
                        ([Id], [MerchantId], [PspConnectionId], [PaymentProviderId],
                         [PaymentProviderMethodId], [PaymentMethodId], [IsEnabled],
                         [CreatedBy], [CreatedAt], [Version])
                    VALUES ({Guid.CreateVersion7()}, {connection.MerchantId}, {connection.Id}, {providerId},
                            {providerMethod.ProviderMethodId}, {methodId}, CAST(1 AS bit),
                            {actorId}, {now}, 1);
                    """, ct);

            if (projectLegacy && !conflicts.Any(x => x.EntityId == connection.Id))
            {
                var projection = string.Join(',', parsed.Known
                    .Where(x => adapter.SupportedMethods.Contains(x))
                    .Order(StringComparer.Ordinal));
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [txn].[PspConnections]
                    SET [EnabledMethods] = {projection}, [Version] = [Version] + 1
                    WHERE [Id] = {connection.Id} AND [EnabledMethods] <> {projection};
                    """, ct);
            }
        }
    }

    private async Task ReconcileMerchantsAsync(
        Guid actorId,
        DateTime now,
        bool projectLegacy,
        HashSet<(string Kind, Guid EntityId)> conflicts,
        CancellationToken ct)
    {
        var qualifyingRows = await db.Database.SqlQuery<QualifyingMethodRow>($"""
            SELECT DISTINCT am.[MerchantId], am.[PaymentMethodId], c.[Psp]
            FROM [txn].[MerchantProviderAccountMethods] am
            JOIN [txn].[PspConnections] c
              ON c.[Id] = am.[PspConnectionId]
             AND c.[MerchantId] = am.[MerchantId]
             AND c.[PaymentProviderId] = am.[PaymentProviderId]
            JOIN [cfg].[PaymentProviders] p
              ON p.[Id] = am.[PaymentProviderId] AND p.[AdapterCode] = c.[Psp]
            JOIN [cfg].[PaymentProviderMethods] pm
              ON pm.[Id] = am.[PaymentProviderMethodId]
             AND pm.[PaymentProviderId] = am.[PaymentProviderId]
             AND pm.[PaymentMethodId] = am.[PaymentMethodId]
            JOIN [cfg].[PaymentMethods] m ON m.[Id] = am.[PaymentMethodId]
            WHERE am.[IsEnabled] = CAST(1 AS bit) AND c.[IsEnabled] = CAST(1 AS bit)
              AND p.[IsEnabled] = CAST(1 AS bit) AND pm.[IsActive] = CAST(1 AS bit)
              AND m.[IsActive] = CAST(1 AS bit)
            """).ToListAsync(ct);
        var qualifying = qualifyingRows
            .Where(x => adapters.For((Code)x.Psp).SupportedMethods.Contains(MethodCode(x.PaymentMethodId)))
            .GroupBy(x => x.MerchantId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.PaymentMethodId).ToHashSet());
        var merchants = await db.Database.SqlQuery<MerchantRow>($"""
            SELECT [Id], [EnabledChannels] FROM [merch].[Merchants] ORDER BY [Id]
            """).ToListAsync(ct);

        foreach (var merchant in merchants)
        {
            var parsed = ParseLegacy(merchant.EnabledChannels);
            if (parsed.HasUnknown)
                await AddConflictAsync("merchant-method", merchant.Id, merchant.Id,
                    "Merchant contains an unknown legacy payment method.", now, conflicts, ct);
            qualifying.TryGetValue(merchant.Id, out var available);
            available ??= [];
            var desired = parsed.Known.Select(MethodId).Where(available.Contains).ToHashSet();
            var existing = await db.Database.SqlQuery<PolicyRow>($"""
                SELECT [Id], [PaymentMethodId], [IsEnabled]
                FROM [txn].[MerchantPaymentMethods]
                WHERE [MerchantId] = {merchant.Id}
                """).ToListAsync(ct);
            foreach (var row in existing)
            {
                var enable = desired.Remove(row.PaymentMethodId);
                await SetEnabledAsync("MerchantPaymentMethods", row.Id, enable, actorId, now, ct);
            }
            foreach (var methodId in desired)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT [txn].[MerchantPaymentMethods]
                        ([Id], [MerchantId], [PaymentMethodId], [IsEnabled],
                         [CreatedBy], [CreatedAt], [Version])
                    VALUES ({Guid.CreateVersion7()}, {merchant.Id}, {methodId}, CAST(1 AS bit),
                            {actorId}, {now}, 1);
                    """, ct);

            if (projectLegacy && !conflicts.Contains(("merchant-method", merchant.Id)))
            {
                var projection = string.Join(',', parsed.Known.Select(MethodId)
                    .Where(available.Contains).Select(MethodCode).Order(StringComparer.Ordinal));
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [merch].[Merchants]
                    SET [EnabledChannels] = {projection}, [Version] = [Version] + 1
                    WHERE [Id] = {merchant.Id} AND [EnabledChannels] <> {projection};
                    """, ct);
            }
        }
    }

    private async Task ReconcileUsersAsync(Guid actorId, DateTime now, CancellationToken ct)
    {
        var users = await db.Database.SqlQuery<ActiveUserRow>($"""
            SELECT [Id], [MerchantId]
            FROM [merch].[Users]
            WHERE [Status] = 2 AND [MerchantId] IS NOT NULL
            ORDER BY [MerchantId], [Id]
            """).ToListAsync(ct);
        foreach (var user in users)
        {
            var desired = (await db.Database.SqlQuery<Guid>($"""
                SELECT [PaymentMethodId] AS [Value]
                FROM [txn].[MerchantPaymentMethods]
                WHERE [MerchantId] = {user.MerchantId} AND [IsEnabled] = CAST(1 AS bit)
                """).ToListAsync(ct)).ToHashSet();
            var existing = await db.Database.SqlQuery<PolicyRow>($"""
                SELECT [Id], [PaymentMethodId], [IsEnabled]
                FROM [txn].[MerchantUserPaymentMethods]
                WHERE [MerchantUserId] = {user.Id} AND [MerchantId] = {user.MerchantId}
                """).ToListAsync(ct);
            foreach (var row in existing)
            {
                var enable = desired.Remove(row.PaymentMethodId);
                await SetEnabledAsync("MerchantUserPaymentMethods", row.Id, enable, actorId, now, ct);
            }
            foreach (var methodId in desired)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT [txn].[MerchantUserPaymentMethods]
                        ([Id], [MerchantUserId], [MerchantId], [PaymentMethodId], [IsEnabled],
                         [CreatedBy], [CreatedAt], [Version])
                    VALUES ({Guid.CreateVersion7()}, {user.Id}, {user.MerchantId}, {methodId},
                            CAST(1 AS bit), {actorId}, {now}, 1);
                    """, ct);
        }
    }

    private async Task ReconcileOrdersAsync(
        DateTime now,
        HashSet<(string Kind, Guid EntityId)> conflicts,
        CancellationToken ct)
    {
        var orders = await db.Database.SqlQuery<LegacyOrderRow>($"""
            SELECT [Id], [MerchantId], [OriginatorId], [SaleCode]
            FROM [shop].[Orders]
            WHERE [InitiatingAudience] IS NULL
            ORDER BY [CreatedAt], [Id]
            """).ToListAsync(ct);
        foreach (var order in orders)
        {
            if (order.OriginatorId is { } originatorId)
            {
                var valid = await db.Database.SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value]
                    FROM [merch].[Originators]
                    WHERE [Id] = {originatorId} AND [MerchantId] = {order.MerchantId}
                    """).SingleAsync(ct);
                if (valid == 1)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [shop].[Orders]
                        SET [InitiatingAudience] = 2, [InitiatingMerchantUserId] = NULL,
                            [Version] = [Version] + 1
                        WHERE [Id] = {order.Id} AND [InitiatingAudience] IS NULL;
                        """, ct);
                    continue;
                }
                await AddConflictAsync("order-creator", order.MerchantId, order.Id,
                    "Order has an invalid Admin-origin marker.", now, conflicts, ct);
                continue;
            }

            var candidates = order.SaleCode is null
                ? []
                : await db.Database.SqlQuery<Guid>($"""
                    SELECT [Id] AS [Value]
                    FROM [merch].[Users]
                    WHERE [MerchantId] = {order.MerchantId} AND [SaleCode] = {order.SaleCode}
                      AND [Status] IN (2, 4)
                    ORDER BY [Id]
                    """).ToListAsync(ct);
            if (candidates.Count == 1)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [shop].[Orders]
                    SET [InitiatingAudience] = 1, [InitiatingMerchantUserId] = {candidates[0]},
                        [Version] = [Version] + 1
                    WHERE [Id] = {order.Id} AND [InitiatingAudience] IS NULL;
                    """, ct);
                continue;
            }
            await AddConflictAsync("order-creator", order.MerchantId, order.Id,
                candidates.Count == 0
                    ? "Order has no unique active or suspended Merchant User creator."
                    : "Order has multiple active or suspended Merchant User creator candidates.",
                now, conflicts, ct);
        }
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        var unresolved = await ScalarAsync("""
            SELECT COUNT(*) AS [Value]
            FROM [cfg].[PaymentCapabilityMigrationConflicts]
            WHERE [ResolvedAt] IS NULL;
            """, ct);
        if (unresolved != 0)
            throw new PaymentAuthorizationCutoverBlockedException(
                $"Authorization cutover blocked by {unresolved} unresolved migration conflict(s).");

        var invalid = await ScalarAsync("""
            SELECT
                (SELECT COUNT(*) FROM [txn].[PspConnections] WHERE [PaymentProviderId] IS NULL)
              + (SELECT COUNT(*) FROM [shop].[Orders] WHERE [InitiatingAudience] IS NULL)
              + (SELECT COUNT(*) FROM [merch].[Merchants] WHERE [Id] = '00000000-0000-0000-0000-000000000000')
              + (SELECT COUNT(*) FROM [txn].[MerchantProviderAccountMethods] am
                   LEFT JOIN [txn].[PspConnections] c
                     ON c.[Id] = am.[PspConnectionId] AND c.[MerchantId] = am.[MerchantId]
                    AND c.[PaymentProviderId] = am.[PaymentProviderId]
                  WHERE c.[Id] IS NULL)
              + (SELECT COUNT(*) FROM [txn].[MerchantUserPaymentMethods] up
                   LEFT JOIN [merch].[Users] u
                     ON u.[Id] = up.[MerchantUserId] AND u.[MerchantId] = up.[MerchantId]
                   LEFT JOIN [txn].[MerchantPaymentMethods] mp
                     ON mp.[MerchantId] = up.[MerchantId] AND mp.[PaymentMethodId] = up.[PaymentMethodId]
                  WHERE u.[Id] IS NULL OR mp.[Id] IS NULL)
              + (SELECT COUNT(*) FROM [shop].[Orders] o
                   LEFT JOIN [merch].[Users] u
                     ON u.[Id] = o.[InitiatingMerchantUserId] AND u.[MerchantId] = o.[MerchantId]
                  WHERE o.[InitiatingAudience] = 1 AND u.[Id] IS NULL)
              + (SELECT COUNT(*) FROM (
                    SELECT [PspConnectionId], [PaymentMethodId]
                    FROM [txn].[MerchantProviderAccountMethods]
                    GROUP BY [PspConnectionId], [PaymentMethodId] HAVING COUNT(*) > 1) d)
              + (SELECT COUNT(*) FROM (
                    SELECT [MerchantId], [PaymentMethodId]
                    FROM [txn].[MerchantPaymentMethods]
                    GROUP BY [MerchantId], [PaymentMethodId] HAVING COUNT(*) > 1) d)
              + (SELECT COUNT(*) FROM (
                    SELECT [MerchantUserId], [PaymentMethodId]
                    FROM [txn].[MerchantUserPaymentMethods]
                    GROUP BY [MerchantUserId], [PaymentMethodId] HAVING COUNT(*) > 1) d)
              AS [Value];
            """, ct);
        if (invalid != 0)
            throw new PaymentAuthorizationCutoverBlockedException(
                "Authorization cutover verification found count, uniqueness or tenant relationship drift.");

        var manifest = await db.Database.SqlQuery<ProviderMethodRow>($"""
            SELECT pm.[Id] AS [ProviderMethodId], pm.[PaymentProviderId], pm.[PaymentMethodId],
                   pm.[IsActive] AS [ProviderMethodActive], p.[AdapterCode],
                   p.[IsEnabled] AS [ProviderActive], m.[Code] AS [MethodCode],
                   m.[IsActive] AS [MethodActive]
            FROM [cfg].[PaymentProviderMethods] pm
            JOIN [cfg].[PaymentProviders] p ON p.[Id] = pm.[PaymentProviderId]
            JOIN [cfg].[PaymentMethods] m ON m.[Id] = pm.[PaymentMethodId]
            WHERE p.[IsEnabled] = CAST(1 AS bit) AND pm.[IsActive] = CAST(1 AS bit)
            """).ToListAsync(ct);
        foreach (var row in manifest)
        {
            if (!Enum.IsDefined(typeof(Code), row.AdapterCode)
                || !adapters.For((Code)row.AdapterCode).SupportedMethods.Contains(row.MethodCode))
                throw new PaymentAuthorizationCutoverBlockedException(
                    "Authorization cutover verification found adapter manifest drift.");
        }
    }

    private Task TightenAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("""
        IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                       WHERE name = N'CK_PspConnections_PaymentProviderRequired')
            ALTER TABLE [txn].[PspConnections] WITH CHECK
                ADD CONSTRAINT [CK_PspConnections_PaymentProviderRequired]
                CHECK ([PaymentProviderId] IS NOT NULL);

        IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                       WHERE name = N'CK_Orders_InitiatingAudienceRequired')
            ALTER TABLE [shop].[Orders] WITH CHECK
                ADD CONSTRAINT [CK_Orders_InitiatingAudienceRequired]
                CHECK ([InitiatingAudience] IS NOT NULL);
        """, ct);

    private async Task ResolvePriorConflictsAsync(Guid actorId, DateTime now, CancellationToken ct)
    {
        foreach (var kind in ConflictKinds)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [cfg].[PaymentCapabilityMigrationConflicts]
                SET [ResolvedAt] = {now}, [ResolvedBy] = {actorId}
                WHERE [ResolvedAt] IS NULL AND [Kind] = {kind};
                """, ct);
    }

    private async Task AddConflictAsync(
        string kind,
        Guid? merchantId,
        Guid entityId,
        string detail,
        DateTime now,
        HashSet<(string Kind, Guid EntityId)> conflicts,
        CancellationToken ct)
    {
        if (!conflicts.Add((kind, entityId)))
            return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT [cfg].[PaymentCapabilityMigrationConflicts]
                ([Id], [Kind], [MerchantId], [EntityId], [Detail], [DetectedAt])
            VALUES ({Guid.CreateVersion7()}, {kind}, {merchantId}, {entityId}, {detail}, {now});
            """, ct);
    }

    private Task SetEnabledAsync(
        string table,
        Guid id,
        bool enabled,
        Guid actorId,
        DateTime now,
        CancellationToken ct)
    {
        var sql = table switch
        {
            "MerchantProviderAccountMethods" => """
                UPDATE [txn].[MerchantProviderAccountMethods]
                SET [IsEnabled] = @enabled, [UpdatedBy] = @actor, [UpdatedAt] = @now,
                    [Version] = [Version] + 1
                WHERE [Id] = @id AND [IsEnabled] <> @enabled;
                """,
            "MerchantPaymentMethods" => """
                UPDATE [txn].[MerchantPaymentMethods]
                SET [IsEnabled] = @enabled, [UpdatedBy] = @actor, [UpdatedAt] = @now,
                    [Version] = [Version] + 1
                WHERE [Id] = @id AND [IsEnabled] <> @enabled;
                """,
            "MerchantUserPaymentMethods" => """
                UPDATE [txn].[MerchantUserPaymentMethods]
                SET [IsEnabled] = @enabled, [UpdatedBy] = @actor, [UpdatedAt] = @now,
                    [Version] = [Version] + 1
                WHERE [Id] = @id AND [IsEnabled] <> @enabled;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        return db.Database.ExecuteSqlRawAsync(sql,
            [
                new Microsoft.Data.SqlClient.SqlParameter("@enabled", enabled),
                new Microsoft.Data.SqlClient.SqlParameter("@actor", actorId),
                new Microsoft.Data.SqlClient.SqlParameter("@now", now),
                new Microsoft.Data.SqlClient.SqlParameter("@id", id),
            ], ct);
    }

    private async Task<PaymentCapabilityMigrationReport> ReportAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct)
            ?? throw new InvalidOperationException("Payment authorization state row is missing.");
        return new PaymentCapabilityMigrationReport(
            (PaymentAuthorizationMode)state.Mode,
            state.CutoffAt,
            await ScalarAsync("SELECT COUNT(*) AS [Value] FROM [txn].[MerchantProviderAccountMethods];", ct),
            await ScalarAsync("SELECT COUNT(*) AS [Value] FROM [txn].[MerchantPaymentMethods];", ct),
            await ScalarAsync("SELECT COUNT(*) AS [Value] FROM [txn].[MerchantUserPaymentMethods];", ct),
            await ScalarAsync("SELECT COUNT(*) AS [Value] FROM [shop].[Orders] WHERE [InitiatingAudience] IS NOT NULL;", ct),
            await ScalarAsync("SELECT COUNT(*) AS [Value] FROM [cfg].[PaymentCapabilityMigrationConflicts] WHERE [ResolvedAt] IS NULL;", ct));
    }

    private Task<StateRow?> StateAsync(CancellationToken ct) => db.Database.SqlQuery<StateRow>($"""
        SELECT [Mode], [CutoffAt]
        FROM [cfg].[PaymentAuthorizationStates]
        WHERE [Id] = {PaymentCapabilityIds.AuthorizationState}
        """).SingleOrDefaultAsync(ct);

    private Task<DateTime> DatabaseUtcNowAsync(CancellationToken ct) =>
        db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);

    private Task<int> ScalarAsync(string sql, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>(sql.Trim().TrimEnd(';')).SingleAsync(ct);

    private static LegacyMethods ParseLegacy(string? csv)
    {
        var known = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = false;
        foreach (var value in (csv ?? string.Empty).Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = value.ToLowerInvariant();
            if (PaymentMethods.IsKnown(code)) known.Add(code); else unknown = true;
        }
        return new LegacyMethods(known, unknown);
    }

    private static bool TryProvider(Code psp, out Guid providerId)
    {
        providerId = psp switch
        {
            Code.TwoCTwoP => PaymentCapabilityIds.TwoCTwoP,
            Code.Omise => PaymentCapabilityIds.Omise,
            _ => Guid.Empty,
        };
        return providerId != Guid.Empty;
    }

    private static Guid MethodId(string method) => method switch
    {
        PaymentMethods.Card => PaymentCapabilityIds.Card,
        PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
        PaymentMethods.Installment => PaymentCapabilityIds.Installment,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static string MethodCode(Guid methodId)
    {
        if (methodId == PaymentCapabilityIds.Card) return PaymentMethods.Card;
        if (methodId == PaymentCapabilityIds.PromptPay) return PaymentMethods.PromptPay;
        if (methodId == PaymentCapabilityIds.Installment) return PaymentMethods.Installment;
        throw new InvalidOperationException("Unknown canonical Payment Method id.");
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new ArgumentException("ActorId is required.", nameof(actorId));
    }

    private sealed record LegacyMethods(SortedSet<string> Known, bool HasUnknown);

    private sealed class StateRow
    {
        public int Mode { get; set; }
        public DateTime? CutoffAt { get; set; }
    }

    private sealed class ConnectionRow
    {
        public Guid Id { get; set; }
        public Guid MerchantId { get; set; }
        public int Psp { get; set; }
        public Guid? PaymentProviderId { get; set; }
        public string EnabledMethods { get; set; } = default!;
        public bool IsEnabled { get; set; }
    }

    private sealed class ProviderMethodRow
    {
        public Guid ProviderMethodId { get; set; }
        public Guid PaymentProviderId { get; set; }
        public Guid PaymentMethodId { get; set; }
        public bool ProviderMethodActive { get; set; }
        public int AdapterCode { get; set; }
        public bool ProviderActive { get; set; }
        public string MethodCode { get; set; } = default!;
        public bool MethodActive { get; set; }
    }

    private sealed class AccountMethodRow
    {
        public Guid Id { get; set; }
        public Guid PaymentProviderId { get; set; }
        public Guid PaymentProviderMethodId { get; set; }
        public Guid PaymentMethodId { get; set; }
        public bool IsEnabled { get; set; }
    }

    private sealed class QualifyingMethodRow
    {
        public Guid MerchantId { get; set; }
        public Guid PaymentMethodId { get; set; }
        public int Psp { get; set; }
    }

    private sealed class MerchantRow
    {
        public Guid Id { get; set; }
        public string EnabledChannels { get; set; } = default!;
    }

    private sealed class PolicyRow
    {
        public Guid Id { get; set; }
        public Guid PaymentMethodId { get; set; }
        public bool IsEnabled { get; set; }
    }

    private sealed class ActiveUserRow
    {
        public Guid Id { get; set; }
        public Guid MerchantId { get; set; }
    }

    private sealed class LegacyOrderRow
    {
        public Guid Id { get; set; }
        public Guid MerchantId { get; set; }
        public Guid? OriginatorId { get; set; }
        public string? SaleCode { get; set; }
    }
}
