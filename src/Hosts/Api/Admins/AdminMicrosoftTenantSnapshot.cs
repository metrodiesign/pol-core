namespace Api.Admins;

/// <summary>Immutable, validated tenant pin for the enabled Admin Microsoft provider.</summary>
internal sealed record AdminMicrosoftTenantSnapshot(Guid? TenantId)
{
    public bool IsEnabled => TenantId.HasValue;

    public static AdminMicrosoftTenantSnapshot Resolve(AdminAuthOptions auth)
    {
        var providers = auth.Providers
            .Where(pair => MicrosoftOidc.Is(pair.Key))
            .Select(pair => pair.Value)
            .ToArray();
        if (providers.Length > 1)
            throw InvalidAuthority();
        return providers.Length == 0
            ? new AdminMicrosoftTenantSnapshot((Guid?)null)
            : Parse(providers[0].ClientId, providers[0].Authority);
    }

    internal static AdminMicrosoftTenantSnapshot Parse(string? clientId, string? authority)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new AdminMicrosoftTenantSnapshot((Guid?)null);

        if (string.IsNullOrEmpty(authority)
            || authority != authority.Trim()
            || authority.Contains('%')
            || authority.Contains('\\')
            || authority.Contains('?')
            || authority.Contains('#')
            || authority.Contains("/./", StringComparison.Ordinal)
            || authority.Contains("/../", StringComparison.Ordinal)
            || !Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw InvalidAuthority();
        }

        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var segments = path.Split('/', StringSplitOptions.None);
        var hasOneTrailingSlash = segments.Length == 3 && segments[2].Length == 0;
        if ((segments.Length != 2 && !hasOneTrailingSlash)
            || !Guid.TryParseExact(segments[0], "D", out var tenantId)
            || tenantId == Guid.Empty
            || !string.Equals(segments[1], "v2.0", StringComparison.Ordinal))
        {
            throw InvalidAuthority();
        }

        return new AdminMicrosoftTenantSnapshot(tenantId);
    }

    private static InvalidOperationException InvalidAuthority() => new(
        "AdminAuth:Providers:Microsoft:Authority must be an HTTPS public-cloud workforce Authority "
        + "with path /{tenant-guid}/v2.0.");
}
