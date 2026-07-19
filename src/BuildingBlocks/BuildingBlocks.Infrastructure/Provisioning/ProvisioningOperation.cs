namespace BuildingBlocks.Infrastructure.Provisioning;

/// <summary>
/// rls-to-query-filter task 7 (design.md "Provisioning UoW" step 4, R1-v7 #2/R2 #3/R4-v7 #1/#2/#4): the
/// idempotency ledger for the ONE cross-context write. One row per attempted <c>operationKey</c> — the
/// coordinator INSERTs it via raw parameterized SQL (matched on the named unique index
/// <c>UX_ProvisioningOperations_Key</c>, never a tracked <c>DbSet.Add</c>, so a concurrent duplicate surfaces
/// as a specific, classifiable constraint violation instead of a generic conflict) BEFORE doing any
/// provisioning work, then fills in <see cref="Result"/> once the merchant/connections/vault-secrets/audit
/// are written. <see cref="MerchantId"/> is pre-minted here and reused for the real
/// <c>merch.Merchants.Id</c> — a deliberate NON-FK ledger value (the row exists before that table's row
/// does).
/// </summary>
public sealed class ProvisioningOperation
{
    public Guid Id { get; private set; }

    /// <summary>Caller-supplied idempotency key. Unique (named index) — the sole conflict-detection surface.</summary>
    public string OperationKey { get; private set; } = default!;

    public Guid CallerAdminId { get; private set; }

    /// <summary>The caller's authorization snapshot pinned at the request boundary — replay compares against
    /// this stored value, never a freshly re-read one (a re-read would make the pin meaningless).</summary>
    public long ExpectedAuthorizationVersion { get; private set; }

    /// <summary>Canonical hash of the request payload — a same-key replay with a DIFFERENT payload is a
    /// caller bug/attack, rejected before the stored result is ever deserialized (R5-v7 #1).</summary>
    public string RequestHash { get; private set; } = default!;

    public Guid MerchantId { get; private set; }

    /// <summary>NULL until the operation completes; a replay/verify match returns this exact serialized body.</summary>
    public string? Result { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private ProvisioningOperation() { }

    public static ProvisioningOperation Create(
        string operationKey, Guid callerAdminId, long expectedAuthorizationVersion, string requestHash,
        Guid merchantId, DateTime createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (callerAdminId == Guid.Empty)
            throw new ArgumentException("CallerAdminId is required.", nameof(callerAdminId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));

        return new ProvisioningOperation
        {
            Id = Guid.NewGuid(),
            OperationKey = operationKey.Trim(),
            CallerAdminId = callerAdminId,
            ExpectedAuthorizationVersion = expectedAuthorizationVersion,
            RequestHash = requestHash,
            MerchantId = merchantId,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Fills in the serialized result once the provisioning write completes (step 5) — the only
    /// mutation this row ever takes after insert.</summary>
    public void SetResult(string result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        Result = result;
    }
}
