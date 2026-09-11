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

    /// <summary>Normalized provider identity. Nullable only during expand/backfill compatibility.</summary>
    public Guid? PaymentProviderId { get; private set; }

    /// <summary>Comma-separated verbatim method codes this connection enables (e.g. "card,promptpay").</summary>
    public string EnabledMethods { get; private set; } = default!;

    /// <summary>Vault lookup name for this connection's secret. Never the secret itself.</summary>
    public string SecretRefName { get; private set; } = default!;

    /// <summary>Low-risk display-only JSON (PLAN #12). Never holds a secret or PII.</summary>
    public string? Metadata { get; private set; }

    public bool IsEnabled { get; private set; }

    /// <summary>Emergency stop is represented by the existing account enabled switch.</summary>
    public bool IsEmergencyDisabled => !IsEnabled;

    public DateTime CreatedAt { get; private set; }

    public Guid? ActiveSecretVersionId { get; private set; }

    /// <summary>The endpoint family the active credential was issued for. Inherited from
    /// <c>Merchant.PaymentEnvironment</c> at creation and kept equal to it by the control plane; the
    /// runtime pins it per call so two merchants in different environments share one process (REQ-2.2/2.3).</summary>
    public PspEnvironment ActiveSecretEnvironment { get; private set; }

    public Guid? PendingSecretVersionId { get; private set; }

    /// <summary>The environment the staged candidate targets; null while nothing is pending.</summary>
    public PspEnvironment? PendingSecretEnvironment { get; private set; }

    public Guid? PendingApprovalId { get; private set; }

    /// <summary>"authenticated" / "probe_failed" from an optional read-only candidate test (REQ-7.8-7.11);
    /// never the active credential's health.</summary>
    public string? PendingSecretTestResult { get; private set; }

    public DateTime? PendingSecretTestedAt { get; private set; }

    /// <summary>SHA-256 (hex) of the callback URL an admin confirmed as registered at the PSP dashboard
    /// (REQ-11.2). Null until acknowledged.</summary>
    public string? WebhookRegistrationHash { get; private set; }

    public DateTime? WebhookRegisteredAt { get; private set; }

    public Guid? WebhookRegisteredBy { get; private set; }
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
        PspEnvironment environment,
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
        ActiveSecretEnvironment = environment;
        Version = 1;
    }

    /// <summary><paramref name="enabledMethods"/> may be empty: a connection can exist with credentials only
    /// so the provider can be tested before any capability is granted (REQ-3.3, design "zero-method").</summary>
    public void Update(string enabledMethods, string? metadata, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(enabledMethods);
        EnabledMethods = enabledMethods.Trim();
        Metadata = metadata;
        IsEnabled = isEnabled;
        Version++;
    }

    public void EmergencyDisable() => SetEnabled(false);

    public void ClearEmergencyDisable() => SetEnabled(true);

    private void SetEnabled(bool enabled)
    {
        if (IsEnabled == enabled)
            return;
        IsEnabled = enabled;
        Version++;
    }

    /// <summary>Writes the deterministic compatibility projection; normalized account rows stay canonical.</summary>
    public void ProjectEnabledMethods(IEnumerable<string> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        var projected = string.Join(',', methods.Select(PaymentMethods.Normalize)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (string.Equals(EnabledMethods, projected, StringComparison.Ordinal))
            return;
        EnabledMethods = projected;
        Version++;
    }

    public void BindPaymentProvider(Guid paymentProviderId)
    {
        if (paymentProviderId == Guid.Empty)
            throw new ArgumentException("PaymentProviderId is required.", nameof(paymentProviderId));
        if (PaymentProviderId is { } current && current != paymentProviderId)
            throw new InvalidOperationException("The PSP connection is already bound to another payment provider.");
        if (PaymentProviderId == paymentProviderId)
            return;
        PaymentProviderId = paymentProviderId;
        Version++;
    }

    public void SetInitialSecretVersion(Guid versionId, PspEnvironment environment)
    {
        if (versionId == Guid.Empty)
            throw new ArgumentException("Secret version is required.", nameof(versionId));
        if (ActiveSecretVersionId is not null)
            throw new InvalidOperationException("An active secret version already exists.");
        ActiveSecretVersionId = versionId;
        ActiveSecretEnvironment = environment;
        Version++;
    }

    public void StageSecretVersion(Guid versionId, Guid approvalId, PspEnvironment environment)
    {
        if (versionId == Guid.Empty)
            throw new ArgumentException("Secret version is required.", nameof(versionId));
        if (approvalId == Guid.Empty)
            throw new ArgumentException("ApprovalId is required.", nameof(approvalId));
        if (PendingSecretVersionId is not null)
            throw new InvalidOperationException("A credential change is already pending.");
        PendingSecretVersionId = versionId;
        PendingSecretEnvironment = environment;
        PendingApprovalId = approvalId;
        PendingSecretTestResult = null;
        PendingSecretTestedAt = null;
        Version++;
    }

    /// <summary>Records an optional read-only probe of the staged candidate. Never touches the active
    /// credential's health (REQ-7.10).</summary>
    public void RecordPendingSecretTest(bool succeeded, DateTime testedAt)
    {
        if (PendingSecretVersionId is null)
            throw new InvalidOperationException("No credential change is pending.");
        PendingSecretTestResult = succeeded ? "authenticated" : "probe_failed";
        PendingSecretTestedAt = testedAt;
        Version++;
    }

    /// <summary>Promotes the candidate: it becomes the active version in ITS environment, and the active
    /// health resets to <see cref="PspConnectionHealth.Unknown"/> — the candidate's test history is approval
    /// evidence, not the new credential's health (design "Entity changes").</summary>
    public Guid ActivatePendingSecretVersion()
    {
        var candidate = PendingSecretVersionId
            ?? throw new InvalidOperationException("No credential change is pending.");
        ActiveSecretVersionId = candidate;
        ActiveSecretEnvironment = PendingSecretEnvironment ?? ActiveSecretEnvironment;
        ClearPending();
        Health = PspConnectionHealth.Unknown;
        LastTestedAt = null;
        LastTestResult = null;
        Version++;
        return candidate;
    }

    public Guid RejectPendingSecretVersion()
    {
        var candidate = PendingSecretVersionId
            ?? throw new InvalidOperationException("No credential change is pending.");
        ClearPending();
        Version++;
        return candidate;
    }

    private void ClearPending()
    {
        PendingSecretVersionId = null;
        PendingSecretEnvironment = null;
        PendingApprovalId = null;
        PendingSecretTestResult = null;
        PendingSecretTestedAt = null;
    }

    /// <summary>Admin acknowledgement that <paramref name="callbackUrl"/> is registered at the PSP
    /// dashboard (Omise live gate, REQ-11.2). Stores only a hash of the URL.</summary>
    public void AcknowledgeWebhookRegistration(string callbackUrl, Guid actorId, DateTime at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackUrl);
        WebhookRegistrationHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(callbackUrl.Trim())))
            .ToLowerInvariant();
        WebhookRegisteredAt = at;
        WebhookRegisteredBy = actorId;
        Version++;
    }

    public void RecordTest(bool succeeded, string result, DateTime testedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        LastTestResult = result.Trim().Length <= 500 ? result.Trim() : result.Trim()[..500];
        LastTestedAt = testedAt;
        Health = succeeded ? PspConnectionHealth.Healthy : PspConnectionHealth.Failed;
        Version++;
    }

    /// <summary>Creates an enabled PSP connection for a merchant, inheriting <paramref name="environment"/>
    /// from the merchant (REQ-2.2; defaults to sandbox so an unconfigured caller can never target live).
    /// <paramref name="enabledMethods"/> may be empty (zero-method connection, REQ-3.3).</summary>
    public static Connection Create(
        Guid merchantId,
        Code psp,
        string enabledMethods,
        string secretRefName,
        DateTime createdAt,
        string? metadata = null,
        PspEnvironment environment = PspEnvironment.Sandbox)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentNullException.ThrowIfNull(enabledMethods);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRefName);

        return new Connection(
            Guid.NewGuid(), merchantId, psp, enabledMethods.Trim(), secretRefName.Trim(), metadata, environment, createdAt);
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
