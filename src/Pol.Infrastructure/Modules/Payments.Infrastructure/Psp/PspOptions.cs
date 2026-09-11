namespace Payments.Infrastructure.Psp;

/// <summary>
/// Non-secret PSP endpoint + environment config, bound from the "Psp" configuration section
/// (appsettings + env override), NEVER the vault. Secrets live only in the revealed JSON envelope
/// (see <see cref="PspSecretEnvelope"/>). The endpoint family is no longer a global flag — it comes from the
/// merchant's <c>PaymentEnvironment</c> pinned per call (REQ-2.3/2.4).
/// </summary>
public sealed class PspOptions
{
    public const string SectionName = "Psp";

    // NOTE: the old global "UseSandbox" flag is gone (merchant-psp-settings task 9, design step 13). It was
    // already unread at runtime after task 2, and its one remaining use — bootstrapping existing merchants'
    // PaymentEnvironment at cutover — is now an explicit argument to ILegacyPaymentRemediation, supplied by
    // the operator. A "Psp:UseSandbox" key left in appsettings binds harmlessly (the options binder ignores
    // unknown keys), so an old config still boots.

    /// <summary>Stable PSP code selected for customer payment links.</summary>
    public string DefaultCode { get; set; } = "2c2p";

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

/// <summary>2C2P has two distinct hosts; the active one is chosen per call by the pinned <c>PspEnvironment</c>.</summary>
public sealed class TwoCTwoPOptions
{
    public string SandboxBaseUrl { get; set; } = "https://sandbox-pgw.2c2p.com";

    public string ProductionBaseUrl { get; set; } = "https://pgw.2c2p.com";

    /// <summary>Where 2C2P sends the customer's browser back after the hosted page (UX only). Stays
    /// platform-wide: the customer SPA is one app shared by all three companies. The backend
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
