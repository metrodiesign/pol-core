namespace Api;

/// <summary>Same-origin return-path allowlist (open-redirect prevention, REQ-1.3). A requested target is honored
/// ONLY when it is a relative same-origin path (single leading slash) that is in the configured allowlist; any
/// other value falls back to the default landing path.</summary>
internal static class ReturnUrlPolicy
{
    public static string Resolve(string? requested, IReadOnlyCollection<string> allowlist, string defaultPath) =>
        !string.IsNullOrEmpty(requested)
        && requested.StartsWith('/')
        && !requested.StartsWith("//", StringComparison.Ordinal)   // protocol-relative -> off-origin
        && allowlist.Contains(requested, StringComparer.Ordinal)
            ? requested
            : defaultPath;
}
