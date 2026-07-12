using SharedKernel;

namespace Payments.Domain;

/// <summary>
/// A merchant's configured credentials+capability binding for one PSP. The actual secret never lives
/// here — only <see cref="SecretRefName"/>, the lookup name under which the plaintext is custodied in
/// <c>IVaultSecretStore</c> (PLAN #14). Enabled methods are kept as a verbatim comma-separated code
/// list ("card,promptpay"); <see cref="Metadata"/> is free-form JSON restricted to low-risk display
/// data only (PLAN #12 — never secrets).
/// </summary>
public sealed class PspConnection : Entity<Guid>
{
    public Guid MerchantId { get; private set; }

    public PspCode Psp { get; private set; }

    /// <summary>Comma-separated verbatim method codes this connection enables (e.g. "card,promptpay").</summary>
    public string EnabledMethods { get; private set; } = default!;

    /// <summary>Vault lookup name for this connection's secret. Never the secret itself.</summary>
    public string SecretRefName { get; private set; } = default!;

    /// <summary>Low-risk display-only JSON (PLAN #12). Never holds a secret or PII.</summary>
    public string? Metadata { get; private set; }

    public bool IsEnabled { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private PspConnection() { }

    private PspConnection(
        Guid id,
        Guid merchantId,
        PspCode psp,
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
    }

    /// <summary>Creates an enabled PSP connection for a merchant.</summary>
    public static PspConnection Create(
        Guid merchantId,
        PspCode psp,
        string enabledMethods,
        string secretRefName,
        DateTime createdAt,
        string? metadata = null)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledMethods);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRefName);

        return new PspConnection(
            Guid.NewGuid(), merchantId, psp, enabledMethods.Trim(), secretRefName.Trim(), metadata, createdAt);
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
