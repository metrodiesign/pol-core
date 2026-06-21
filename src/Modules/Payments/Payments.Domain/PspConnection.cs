using SharedKernel;

namespace Payments.Domain;

/// <summary>
/// A tenant's configured credentials+capability binding for one PSP. The actual secret never lives
/// here — only <see cref="SecretRefName"/>, the lookup name under which the plaintext is custodied in
/// <c>IVaultSecretStore</c> (PLAN #14). Enabled methods are kept as a verbatim comma-separated code
/// list ("card,promptpay"); <see cref="Metadata"/> is free-form JSON restricted to low-risk display
/// data only (PLAN #12 — never secrets).
/// </summary>
public sealed class PspConnection : Entity<Guid>
{
    public Guid TenantId { get; private set; }

    public PspCode Psp { get; private set; }

    /// <summary>Comma-separated verbatim method codes this connection enables (e.g. "card,promptpay").</summary>
    public string EnabledMethods { get; private set; } = default!;

    /// <summary>Vault lookup name for this connection's secret. Never the secret itself.</summary>
    public string SecretRefName { get; private set; } = default!;

    /// <summary>Low-risk display-only JSON (PLAN #12). Never holds a secret or PII.</summary>
    public string? Metadata { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private PspConnection() { }

    private PspConnection(
        Guid id,
        Guid tenantId,
        PspCode psp,
        string enabledMethods,
        string secretRefName,
        string? metadata,
        DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        Psp = psp;
        EnabledMethods = enabledMethods;
        SecretRefName = secretRefName;
        Metadata = metadata;
        IsEnabled = true;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Creates an enabled PSP connection for a tenant.</summary>
    public static PspConnection Create(
        Guid tenantId,
        PspCode psp,
        string enabledMethods,
        string secretRefName,
        DateTime createdAtUtc,
        string? metadata = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledMethods);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRefName);

        return new PspConnection(
            Guid.NewGuid(), tenantId, psp, enabledMethods.Trim(), secretRefName.Trim(), metadata, createdAtUtc);
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
