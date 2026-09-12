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

    public void Validate()
    {
        if (RegistrationSessionMinutes <= 0)
            throw new InvalidOperationException("IdentityAccess:RegistrationSessionMinutes must be greater than zero.");
        if (BffSessionMinutes <= 0)
            throw new InvalidOperationException("IdentityAccess:BffSessionMinutes must be greater than zero.");
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
