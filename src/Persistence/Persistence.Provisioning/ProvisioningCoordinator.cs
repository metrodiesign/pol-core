using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Provisioning;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Persistence.ControlPlane;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;

namespace Persistence.Provisioning;

/// <summary>Test-only failpoint hook — see <see cref="ProvisioningCoordinator"/>'s ctor doc.</summary>
internal enum ProvisioningCheckpoint { AfterControlPlaneSave, AfterMerchantRuntimeSave }

/// <summary>
/// rls-to-query-filter task 7: the internal coordinator behind <see cref="IProvisioningWriter"/> — the ONE
/// place two runtime contexts share a transaction (design.md "Provisioning UoW", steps 1-7). Not public; the
/// Api host composition root is the only caller allowed to construct it (design.md "NOT a public
/// coordinator"). Scaffolding only — <c>admin.ProvisioningOperations</c> does not exist on the real database
/// yet (task 8's migration), so this is proven standalone via SQLite, not yet wired as the live
/// <c>ProvisionMerchantHandler</c> implementation.
/// </summary>
internal sealed class ProvisioningCoordinator : IProvisioningWriter
{
    private readonly Func<CancellationToken, Task<DbConnection>> _openConnectionAsync;
    private readonly Func<DbConnection, ControlPlaneDbContext> _controlPlaneFactory;
    private readonly Func<DbConnection, MerchantRuntimeDbContext> _merchantRuntimeFactory;
    private readonly VaultKeyring _keyring;
    private readonly IClock _clock;
    private readonly ISecurityTelemetry _telemetry;
    private readonly int _maxAttempts;
    private readonly Func<ProvisioningCheckpoint, CancellationToken, Task>? _checkpoint;

    /// <param name="openConnectionAsync">Opens a FRESH connection for one attempt (step 1) — called again on
    /// every retry so a transient-fault retry never reuses a poisoned change tracker.</param>
    /// <param name="controlPlaneFactory">Builds a <see cref="ControlPlaneDbContext"/> bound to the given
    /// (already-open) connection. The caller decides the provider (SQL Server in production, SQLite in
    /// tests) and supplies <see cref="IWriteAuthorizer"/>.</param>
    /// <param name="merchantRuntimeFactory">Same as <paramref name="controlPlaneFactory"/> for
    /// <see cref="MerchantRuntimeDbContext"/>.</param>
    /// <param name="checkpoint">Test-only: invoked after each context's <c>SaveChanges</c> (before commit) so
    /// a test can inject a failpoint and assert the WHOLE transaction rolled back atomically. Always null in
    /// production.</param>
    internal ProvisioningCoordinator(
        Func<CancellationToken, Task<DbConnection>> openConnectionAsync,
        Func<DbConnection, ControlPlaneDbContext> controlPlaneFactory,
        Func<DbConnection, MerchantRuntimeDbContext> merchantRuntimeFactory,
        VaultKeyring keyring,
        IClock clock,
        ISecurityTelemetry telemetry,
        int maxAttempts = 3,
        Func<ProvisioningCheckpoint, CancellationToken, Task>? checkpoint = null)
    {
        _openConnectionAsync = openConnectionAsync;
        _controlPlaneFactory = controlPlaneFactory;
        _merchantRuntimeFactory = merchantRuntimeFactory;
        _keyring = keyring;
        _clock = clock;
        _telemetry = telemetry;
        _maxAttempts = maxAttempts;
        _checkpoint = checkpoint;
    }

    public async Task<ProvisioningWriteResult> ProvisionAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        if (callerAdminId == Guid.Empty)
            throw new ArgumentException("CallerAdminId is required.", nameof(callerAdminId));

        var requestHash = ComputeRequestHash(spec);

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                return await AttemptAsync(spec, callerAdminId, expectedAuthorizationVersion, operationKey, requestHash, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _maxAttempts && IsTransient(ex))
            {
                // Commit-unknown (R5-v7 #2): verify BEFORE blindly retrying, so a committed-but-lost-ack
                // operation is recognised rather than re-attempted and wrongly denied on retry.
                var verified = await TryVerifySucceededAsync(
                    operationKey, callerAdminId, requestHash, spec, cancellationToken)
                    .ConfigureAwait(false);
                if (verified is not null)
                    return verified;
            }
        }

        throw new InvalidOperationException($"Provisioning operation '{operationKey}' exhausted {_maxAttempts} attempt(s).");
    }

    private async Task<ProvisioningWriteResult> AttemptAsync(
        ProvisionSpec spec, Guid callerAdminId, long expectedAuthorizationVersion, string operationKey, string requestHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var controlPlane = _controlPlaneFactory(connection);
        await using var merchantRuntime = _merchantRuntimeFactory(connection);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await controlPlane.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await merchantRuntime.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        // Step 3: lock + recheck the FULL authorization in-tx (never trust a pre-transaction check).
        await VerifyCallerIsActiveSuperAsync(controlPlane, callerAdminId, expectedAuthorizationVersion, _telemetry, cancellationToken)
            .ConfigureAwait(false);

        // Step 4: idempotency ledger — immediate parameterized raw INSERT, not a deferred DbSet.Add.
        var mintedMerchantId = Guid.NewGuid();
        await new PaymentAuthorizationSqlLockManager(merchantRuntime)
            .AcquireMerchantExclusiveAsync(mintedMerchantId, cancellationToken).ConfigureAwait(false);
        var ledgerRow = ProvisioningOperation.Create(
            operationKey, callerAdminId, expectedAuthorizationVersion, requestHash, mintedMerchantId, _clock.UtcNow);
        var inserted = await TryInsertLedgerRowAsync(controlPlane, ledgerRow, cancellationToken).ConfigureAwait(false);

        if (!inserted.IsNew)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var existing = inserted.Existing!;
            if (existing.CallerAdminId != callerAdminId || existing.RequestHash != requestHash)
                throw new ConflictException(
                    $"Provisioning operation key '{operationKey}' was already used by a different caller or payload.");
            if (existing.Result is null)
                throw new InvalidOperationException(
                    $"Provisioning operation key '{operationKey}' exists but has no recorded result.");
            return ProvisioningLedgerResultCodec.Deserialize(existing.Result, spec);
        }

        // Step 5: new operation — add the EXACT entity set (Merchant + PspConnection(s) + VaultSecret(s) +
        // ProvisioningAudit), all keyed off the pre-minted merchant id.
        var merchant = Merchant.CreateWithId(mintedMerchantId, spec.MerchantCode, spec.Name, spec.Note,
            spec.Country, spec.Currency, spec.EnabledChannels, spec.MerchantMetadataJson, _clock.UtcNow);
        merchantRuntime.Merchants.Add(merchant);

        var connectionResults = new List<ProvisionedConnectionWrite>(spec.Connections.Count);
        var accountMethodIds = new HashSet<Guid>();
        var (activeKeyId, masterKey) = _keyring.Active;
        foreach (var c in spec.Connections)
        {
            var psp = Codes.FromCode(c.Psp);
            var providerId = ProviderId(psp);
            var pspConnection = Connection.Create(
                mintedMerchantId, psp, c.EnabledMethods, c.SecretName, _clock.UtcNow, c.ConnectionMetadataJson);
            pspConnection.BindPaymentProvider(providerId);
            merchantRuntime.PspConnections.Add(pspConnection);

            foreach (var method in c.EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var methodId = MethodId(method);
                merchantRuntime.MerchantProviderAccountMethods.Add(MerchantProviderAccountMethod.Create(
                    mintedMerchantId, pspConnection.Id, providerId, ProviderMethodId(providerId, methodId),
                    methodId, callerAdminId, _clock.UtcNow));
                accountMethodIds.Add(methodId);
            }

            var kek = VaultEnvelope.DeriveKek(masterKey, mintedMerchantId);
            var dek = RandomNumberGenerator.GetBytes(32);
            try
            {
                var encryptedSecret = VaultEnvelope.Encrypt(dek, Encoding.UTF8.GetBytes(c.SecretEnvelopeJson));
                var wrappedDek = VaultEnvelope.Encrypt(kek, dek);
                merchantRuntime.VaultSecrets.Add(new VaultSecretBlob(
                    mintedMerchantId, c.SecretName, activeKeyId, wrappedDek, encryptedSecret, LastFour(c.SecretEnvelopeJson), _clock.UtcNow));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(kek);
            }

            connectionResults.Add(new ProvisionedConnectionWrite(pspConnection.Id, c.Psp, c.MaskedSecretHints));
        }

        foreach (var method in (spec.EnabledChannels ?? []).Select(PaymentMethods.Normalize))
        {
            var methodId = MethodId(method);
            if (!accountMethodIds.Contains(methodId))
                throw new InvalidOperationException($"Merchant method '{method}' has no qualifying PSP connection.");
            merchantRuntime.MerchantPaymentMethods.Add(MerchantPaymentMethod.Create(
                mintedMerchantId, methodId, true, callerAdminId, _clock.UtcNow));
        }

        merchantRuntime.ProvisioningAudits.Add(
            ProvisioningAudit.Create(mintedMerchantId, spec.MerchantCode, spec.AdminSubject, spec.CorrelationId, _clock.UtcNow));

        var result = new ProvisioningWriteResult(mintedMerchantId, connectionResults);
        ledgerRow.SetResult(ProvisioningLedgerResultCodec.Serialize(result));
        // ledgerRow was inserted via raw SQL — untracked. Attach + mark ONLY Result modified, so SaveChanges
        // emits a targeted UPDATE without a redundant re-SELECT.
        controlPlane.ProvisioningOperations.Attach(ledgerRow);
        controlPlane.Entry(ledgerRow).Property(x => x.Result).IsModified = true;

        // Step 6: SaveChanges(false) on each -> Commit -> AcceptAllChanges (R5 #2: never accept before the
        // joint commit). Checkpoints let a test inject a failpoint between these steps.
        await controlPlane.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken).ConfigureAwait(false);
        if (_checkpoint is not null)
            await _checkpoint(ProvisioningCheckpoint.AfterControlPlaneSave, cancellationToken).ConfigureAwait(false);

        await merchantRuntime.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken).ConfigureAwait(false);
        if (_checkpoint is not null)
            await _checkpoint(ProvisioningCheckpoint.AfterMerchantRuntimeSave, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        controlPlane.ChangeTracker.AcceptAllChanges();
        merchantRuntime.ChangeTracker.AcceptAllChanges();

        return result;
    }

    private static async Task VerifyCallerIsActiveSuperAsync(
        ControlPlaneDbContext db, Guid callerAdminId, long expectedAuthorizationVersion, ISecurityTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        var table = TableName(db, typeof(Admins.Domain.Users.User));
        // WITH (UPDLOCK, HOLDLOCK) is SQL-Server-only syntax — the table hint is real ONLY there; the
        // Tier/Status/AuthorizationVersion condition (the thing this test suite actually verifies) is
        // identical on both providers.
        var hint = db.Database.IsSqlServer() ? " WITH (UPDLOCK, HOLDLOCK)" : "";
        var sql = $"SELECT 1 AS Value FROM {table}{hint} WHERE Id={{0}} AND Tier={{1}} AND Status={{2}} AND AuthorizationVersion={{3}}";

        var rows = await db.Database.SqlQueryRaw<int>(
            sql, callerAdminId, (int)Admins.Domain.Users.Tier.Super, (int)Admins.Domain.Users.UserStatus.Active, expectedAuthorizationVersion)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            telemetry.Emit(new DenialEvent(
                DenialCategory.AdminRevalidationDenial, "admin", callerAdminId, TargetMerchant: null,
                nameof(Admins.Domain.Users.User), "ProvisioningCoordinator.VerifyCallerIsActiveSuper",
                "Provisioning caller is not an active Super admin at the expected authorization version.",
                CorrelationId.Current, DateTime.UtcNow));
            throw new WriteGuardException(
                $"Provisioning denied: caller '{callerAdminId}' is not an active Super admin at authorization version {expectedAuthorizationVersion}.");
        }
    }

    private static async Task<(bool IsNew, ProvisioningOperation? Existing)> TryInsertLedgerRowAsync(
        ControlPlaneDbContext db, ProvisioningOperation row, CancellationToken cancellationToken)
    {
        var table = TableName(db, typeof(ProvisioningOperation));
        var sql = $"INSERT INTO {table} (Id, OperationKey, CallerAdminId, ExpectedAuthorizationVersion, RequestHash, MerchantId, Result, CreatedAt) "
            + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, NULL, {6})";

        try
        {
            await db.Database.ExecuteSqlRawAsync(sql,
                [row.Id, row.OperationKey, row.CallerAdminId, row.ExpectedAuthorizationVersion, row.RequestHash, row.MerchantId, row.CreatedAt],
                cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        catch (DbException ex)
        {
            if (!IsDuplicateKeyViolation(ex))
                throw;

            var existing = await db.ProvisioningOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationKey == row.OperationKey, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw; // not actually a duplicate of THIS key -> some other constraint issue, propagate the real error
            return (false, existing);
        }
    }

    // Matched on the SPECIFIC named index (2601/2627 alone is ambiguous — R4-v7 #2): the constraint name is
    // parsed, not inferred from the error number alone. Any other provider (SQLite, test-only — never
    // production) falls back to re-querying the key immediately after (see the caller), which is safe because
    // a false positive here just costs one extra read before rethrowing the real error.
    private static bool IsDuplicateKeyViolation(DbException ex) => ex switch
    {
        SqlException sqlEx => sqlEx.Number is 2601 or 2627
            && sqlEx.Message.Contains("UX_ProvisioningOperations_Key", StringComparison.Ordinal),
        _ => true,
    };

    private async Task<ProvisioningWriteResult?> TryVerifySucceededAsync(
        string operationKey, Guid callerAdminId, string requestHash, ProvisionSpec spec,
        CancellationToken cancellationToken)
    {
        await using var connection = await _openConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var controlPlane = _controlPlaneFactory(connection);

        var existing = await controlPlane.ProvisioningOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Result is null)
            return null; // genuinely never committed -> caller retries

        if (existing.CallerAdminId != callerAdminId || existing.RequestHash != requestHash)
            throw new ConflictException(
                $"Provisioning operation key '{operationKey}' was already used by a different caller or payload.");
        return ProvisioningLedgerResultCodec.Deserialize(existing.Result, spec);
    }

    private static string ComputeRequestHash(ProvisionSpec spec) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spec))));

    // Reasonable-effort SQL Server transient classification (common throttling/timeout/connection-loss error
    // numbers) — refined against EF Core's own retry policy at task 8's production wiring; this scaffolding
    // only needs to prove the RETRY + commit-unknown-verify SHAPE is correct, not chase every real-world code.
    private static bool IsTransient(Exception ex) => ex switch
    {
        SqlException sqlEx => sqlEx.Number is 4060 or 40197 or 40501 or 40613 or 49918 or 49919 or 49920 or 11001 or -2,
        TimeoutException => true,
        _ => false,
    };

    private static string LastFour(string secret) => secret.Length <= 4 ? new string('*', secret.Length) : secret[^4..];

    private static Guid ProviderId(Code psp) => psp switch
    {
        Code.TwoCTwoP => PaymentCapabilityIds.TwoCTwoP,
        Code.Omise => PaymentCapabilityIds.Omise,
        _ => throw new ArgumentOutOfRangeException(nameof(psp)),
    };

    private static Guid MethodId(string method) => PaymentMethods.Normalize(method) switch
    {
        PaymentMethods.Card => PaymentCapabilityIds.Card,
        PaymentMethods.PromptPay => PaymentCapabilityIds.PromptPay,
        PaymentMethods.Installment => PaymentCapabilityIds.Installment,
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static Guid ProviderMethodId(Guid providerId, Guid methodId) => (providerId, methodId) switch
    {
        var (provider, method) when provider == PaymentCapabilityIds.TwoCTwoP
            && method == PaymentCapabilityIds.Card => PaymentCapabilityIds.TwoCTwoPCard,
        var (provider, method) when provider == PaymentCapabilityIds.TwoCTwoP
            && method == PaymentCapabilityIds.PromptPay => PaymentCapabilityIds.TwoCTwoPPromptPay,
        var (provider, method) when provider == PaymentCapabilityIds.TwoCTwoP
            && method == PaymentCapabilityIds.Installment => PaymentCapabilityIds.TwoCTwoPInstallment,
        var (provider, method) when provider == PaymentCapabilityIds.Omise
            && method == PaymentCapabilityIds.Card => PaymentCapabilityIds.OmiseCard,
        _ => throw new InvalidOperationException("PSP method is outside the canonical provider catalog."),
    };

    // GetSchemaQualifiedTableName() is provider-agnostic MODEL metadata — it returns "admin.Users" regardless
    // of provider, but SQLite's EnsureCreated() physically ignores the schema and creates a bare "Users"
    // table (task 2 precedent). Only qualify with the schema on SQL Server, where it physically exists.
    private static string TableName(ControlPlaneDbContext db, Type clrType)
    {
        var entityType = db.Model.FindEntityType(clrType)!;
        return db.Database.IsSqlServer() ? entityType.GetSchemaQualifiedTableName()! : entityType.GetTableName()!;
    }
}
