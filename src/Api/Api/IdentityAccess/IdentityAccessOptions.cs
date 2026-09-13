namespace Api.IdentityAccess;

public sealed class IdentityAccessOptions
{
    public const string SectionName = "IdentityAccess";

    public string WorkforceIssuer { get; set; } = string.Empty;
    public string WorkforceTenantId { get; set; } = string.Empty;
    public string WorkforceAudience { get; set; } = string.Empty;
    public string AgentIssuer { get; set; } = string.Empty;
    public string AgentTenantId { get; set; } = string.Empty;
    public string AgentAudience { get; set; } = string.Empty;
    public Guid? AgentMerchantId { get; set; }
    public int RegistrationSessionMinutes { get; set; } = 30;
    public int BffSessionMinutes { get; set; } = 480;

    /// <summary>Origin of the workforce SPA. The OIDC callback lands on the API origin, so a relative returnTo is
    /// made absolute against this (blank = redirect stays on the API origin). Mirrors AdminSession:WebAppBaseUrl.</summary>
    public string WorkforceWebAppBaseUrl { get; set; } = string.Empty;

    /// <summary>Origin of the agent (external user) SPA; the post-callback /register redirect is made absolute
    /// against this (blank = stays on the API origin).</summary>
    public string AgentWebAppBaseUrl { get; set; } = string.Empty;

    public void Validate()
    {
        ValidateOrigin(WorkforceWebAppBaseUrl, nameof(WorkforceWebAppBaseUrl));
        ValidateOrigin(AgentWebAppBaseUrl, nameof(AgentWebAppBaseUrl));
        if (RegistrationSessionMinutes <= 0)
            throw new InvalidOperationException("IdentityAccess:RegistrationSessionMinutes must be greater than zero.");
        if (BffSessionMinutes <= 0)
            throw new InvalidOperationException("IdentityAccess:BffSessionMinutes must be greater than zero.");
    }

    private static void ValidateOrigin(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"IdentityAccess:{name} must be an absolute HTTP(S) origin.");
    }
}

public sealed class IdentityOidcProviderOptions
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
}

internal sealed class IdentityAccessProviders : Dictionary<string, string>;

internal sealed record IdentityAccessAmbiguousAuthentication;
