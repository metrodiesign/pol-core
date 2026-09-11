namespace SharedKernel;

/// <summary>
/// The PSP endpoint family a credential set belongs to. Lives in SharedKernel because BOTH the Merchant
/// aggregate (the source of truth: <c>Merchant.PaymentEnvironment</c>) and the Payments module (the
/// connection's active/pending credential environment, the adapter's endpoint choice) need it, and neither
/// domain project may reference the other. Persisted as int (Sandbox=1, Live=2); crosses the wire ONLY as
/// the lowercase codes in <see cref="PspEnvironments"/> — never as the enum member name.
/// </summary>
public enum PspEnvironment
{
    Sandbox = 1,
    Live = 2,
}

/// <summary>Wire/storage codes for <see cref="PspEnvironment"/> ("sandbox"/"live").</summary>
public static class PspEnvironments
{
    public static string ToCode(this PspEnvironment environment) => environment switch
    {
        PspEnvironment.Sandbox => "sandbox",
        PspEnvironment.Live => "live",
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown PSP environment."),
    };

    /// <summary>Parses a wire code; throws <see cref="ArgumentException"/> (400) on anything else.</summary>
    public static PspEnvironment FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "sandbox" => PspEnvironment.Sandbox,
        "live" => PspEnvironment.Live,
        _ => throw new ArgumentException("Payment environment must be 'sandbox' or 'live'.", nameof(code)),
    };
}
