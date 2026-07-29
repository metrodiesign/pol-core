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

    /// <summary>This API's public origin (e.g. <c>https://api.example.com</c>), the base every
    /// per-connection backend-notification URL is derived from:
    /// <c>{PublicBaseUrl}/api/v1/webhooks/{pspConnectionId}</c>. It replaces the old per-deployment
    /// callback URL, which could only ever be correct for ONE connection — every other company's webhook
    /// missed the route and its orders stayed AwaitingPayment after the customer had paid. Required
    /// outside Development (boot guard); blank here so the committed defaults still boot locally.</summary>
    public string PublicBaseUrl { get; set; } = "";

    public TwoCTwoPOptions TwoCTwoP { get; set; } = new();

    public OmiseOptions Omise { get; set; } = new();
}

/// <summary>2C2P has two distinct hosts; the active one is chosen by <see cref="PspOptions.UseSandbox"/>.</summary>
public sealed class TwoCTwoPOptions
{
    public string SandboxBaseUrl { get; set; } = "https://sandbox-pgw.2c2p.com";

    public string ProductionBaseUrl { get; set; } = "https://pgw.2c2p.com";

    /// <summary>Where 2C2P sends the customer's browser back after the hosted page (UX only). Stays
    /// platform-wide: the Tenant Console is one app shared by all three companies. The backend
    /// notification URL is NOT here — it is derived per connection from
    /// <see cref="PspOptions.PublicBaseUrl"/>.</summary>
    public string FrontendReturnUrl { get; set; } = "";
}

/// <summary>Omise uses one host regardless of environment; the secret key's prefix decides test vs live.</summary>
public sealed class OmiseOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.omise.co";

    /// <summary>Where Omise sends the cardholder's browser back after hosted 3DS.</summary>
    public string ReturnUri { get; set; } = "";
}
