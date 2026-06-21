namespace Payments.Infrastructure.Psp;

/// <summary>
/// Non-secret PSP endpoint + environment config, bound from the "Psp" configuration section
/// (appsettings + env override), NEVER the vault. Secrets live only in the revealed JSON envelope
/// (see <see cref="PspSecretEnvelope"/>). <see cref="UseSandbox"/> defaults to <c>true</c> so an
/// unconfigured environment hits a PSP sandbox, never production — production is an explicit opt-in.
/// </summary>
public sealed class PspOptions
{
    public const string SectionName = "Psp";

    /// <summary>When true, adapters target each PSP's sandbox/test surface. Default true (safe).</summary>
    public bool UseSandbox { get; set; } = true;

    public TwoCTwoPOptions TwoCTwoP { get; set; } = new();

    public OmiseOptions Omise { get; set; } = new();
}

/// <summary>2C2P has two distinct hosts; the active one is chosen by <see cref="PspOptions.UseSandbox"/>.</summary>
public sealed class TwoCTwoPOptions
{
    public string SandboxBaseUrl { get; set; } = "https://sandbox-pgw.2c2p.com";

    public string ProductionBaseUrl { get; set; } = "https://pgw.2c2p.com";

    /// <summary>Where 2C2P sends the customer's browser back after the hosted page (UX only).</summary>
    public string FrontendReturnUrl { get; set; } = "";

    /// <summary>Where 2C2P POSTs the backend notification (our /webhooks endpoint).</summary>
    public string BackendReturnUrl { get; set; } = "";
}

/// <summary>Omise uses one host regardless of environment; the secret key's prefix decides test vs live.</summary>
public sealed class OmiseOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.omise.co";

    /// <summary>Where Omise sends the cardholder's browser back after hosted 3DS.</summary>
    public string ReturnUri { get; set; } = "";
}
