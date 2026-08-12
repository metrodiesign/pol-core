using SharedKernel;

namespace Payments.Domain.Psp;

/// <summary>
/// A merchant's configured credentials+capability binding for one PSP. The actual secret never lives
/// here — only <see cref="SecretRefName"/>, the lookup name under which the plaintext is custodied in
/// <c>IVaultSecretStore</c> (PLAN #14). Enabled methods are kept as a verbatim comma-separated code
/// list ("card,promptpay"); <see cref="Metadata"/> is free-form JSON restricted to low-risk display
/// data only (PLAN #12 — never secrets).
/// </summary>
public sealed class Connection : Entity<Guid>
{
    public Guid MerchantId { get; private set; }

    public Code Psp { get; private set; }

    /// <summary>Comma-separated verbatim method codes this connection enables (e.g. "card,promptpay").</summary>
    public string EnabledMethods { get; private set; } = default!;

    /// <summary>Vault lookup name for this connection's secret. Never the secret itself.</summary>
    public string SecretRefName { get; private set; } = default!;

    /// <summary>Low-risk display-only JSON (PLAN #12). Never holds a secret or PII.</summary>
    public string? Metadata { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public Guid? ActiveSecretVersionId { get; private set; }
    public Guid? PendingSecretVersionId { get; private set; }
    public Guid? PendingApprovalId { get; private set; }
    public PspConnectionHealth Health { get; private set; }
    public DateTime? LastTestedAt { get; private set; }
    public string? LastTestResult { get; private set; }
    public long Version { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Connection() { }

    private Connection(
        Guid id,
        Guid merchantId,
        Code psp,
        string enabledMethods,
        string secretRefName,
        string? metadata,
        DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        Psp = psp;
        EnabledMethods = enabledMethods;
        SecretRefName = secretRefName;
        Metadata = metadata;
        IsEnabled = true;
        CreatedAt = createdAt;
        Health = PspConnectionHealth.Unknown;
        Version = 1;
    }

    public void Update(string enabledMethods, string? metadata, bool isEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledMethods);
        EnabledMethods = enabledMethods.Trim();
        Metadata = metadata;
        IsEnabled = isEnabled;
        Version++;
    }

    public void SetInitialSecretVersion(Guid versionId)
    {
        if (versionId == Guid.Empty)
            throw new ArgumentException("Secret version is required.", nameof(versionId));
        if (ActiveSecretVersionId is not null)
            throw new InvalidOperationException("An active secret version already exists.");
        ActiveSecretVersionId = versionId;
        Version++;
    }

    public void StageSecretVersion(Guid versionId, Guid approvalId)
    {
        if (versionId == Guid.Empty)
            throw new ArgumentException("Secret version is required.", nameof(versionId));
        if (approvalId == Guid.Empty)
            throw new ArgumentException("ApprovalId is required.", nameof(approvalId));
        if (PendingSecretVersionId is not null)
            throw new InvalidOperationException("A credential change is already pending.");
        PendingSecretVersionId = versionId;
        PendingApprovalId = approvalId;
        Version++;
    }

    public Guid ActivatePendingSecretVersion()
    {
        var candidate = PendingSecretVersionId
            ?? throw new InvalidOperationException("No credential change is pending.");
        ActiveSecretVersionId = candidate;
        PendingSecretVersionId = null;
        PendingApprovalId = null;
        Version++;
        return candidate;
    }

    public Guid RejectPendingSecretVersion()
    {
        var candidate = PendingSecretVersionId
            ?? throw new InvalidOperationException("No credential change is pending.");
        PendingSecretVersionId = null;
        PendingApprovalId = null;
        Version++;
        return candidate;
    }

    public void RecordTest(bool succeeded, string result, DateTime testedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        LastTestResult = result.Trim().Length <= 500 ? result.Trim() : result.Trim()[..500];
        LastTestedAt = testedAt;
        Health = succeeded ? PspConnectionHealth.Healthy : PspConnectionHealth.Failed;
        Version++;
    }

    /// <summary>Creates an enabled PSP connection for a merchant.</summary>
    public static Connection Create(
        Guid merchantId,
        Code psp,
        string enabledMethods,
        string secretRefName,
        DateTime createdAt,
        string? metadata = null)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledMethods);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRefName);

        return new Connection(
            Guid.NewGuid(), merchantId, psp, enabledMethods.Trim(), secretRefName.Trim(), metadata, createdAt);
    }

    /// <summary>
    /// Throws unless this connection may charge <paramref name="method"/> right now: it must be enabled
    /// and the method must be in its enabled list. The single eligibility gate — both the create-session
    /// and the start-redirect paths call it, so a connection disabled (or re-scoped) between the two
    /// cannot still reach the PSP. <see cref="InvalidOperationException"/> (409) rather than
    /// <see cref="ArgumentException"/> (400): a disabled connection, or a method the company never
    /// enabled, is SERVER state — not malformed client input. The message names only the connection id,
    /// never <see cref="SecretRefName"/> or any secret.
    /// </summary>
    public void EnsureEligible(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        if (!IsEnabled)
            throw new InvalidOperationException($"PSP connection {Id} is disabled.");
        if (!Supports(method))
            throw new InvalidOperationException($"PSP connection {Id} does not enable method '{method}'.");
    }

    /// <summary>True when <paramref name="method"/> appears in this connection's enabled method list.</summary>
    public bool Supports(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        foreach (var code in EnabledMethods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(code, method, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

public enum PspConnectionHealth
{
    Unknown = 1,
    Healthy = 2,
    Failed = 3,
}
