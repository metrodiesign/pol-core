namespace Api.IdentityAccess;

/// <summary>The Microsoft workforce Authority behind employee login (<c>IdentityAccess:Workforce:Authority</c>) must
/// pin exactly one tenant: an HTTPS public-cloud endpoint with path <c>/{tenant-guid}/v2.0</c>. Multi-tenant
/// authorities (<c>common</c>, <c>organizations</c>) would accept any Entra tenant, so they are rejected.</summary>
internal static class WorkforceAuthority
{
    /// <summary>The redirect URI registered on the workforce Entra app, relative to the API's public origin.</summary>
    public const string CallbackPath = "/api/v1/admins/auth/microsoft/callback";

    /// <summary>Returns the pinned tenant id or throws <see cref="InvalidOperationException"/>.</summary>
    internal static Guid Parse(string? authority)
    {
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

        return tenantId;
    }

    private static InvalidOperationException InvalidAuthority() => new(
        "IdentityAccess:Workforce:Authority must be an HTTPS public-cloud workforce Authority "
        + "with path /{tenant-guid}/v2.0.");
}
