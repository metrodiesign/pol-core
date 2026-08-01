using Api.Admins;
using Api.Merchants;
using Microsoft.AspNetCore.Authorization;

namespace Api.Iam;

/// <summary>
/// Boot-guard marker recording which cookie-session scheme's CSRF filter an endpoint carries. A plain
/// <c>AddEndpointFilter&lt;T&gt;</c> leaves no queryable trace in <see cref="Endpoint.Metadata"/> (verified in
/// AdminCorsGuardTests), so the <see cref="CsrfProtection"/> extensions attach this alongside the filter and
/// <see cref="CsrfParity"/> reads it at boot.
/// </summary>
internal sealed record CsrfProtected(string SchemeId);

/// <summary>
/// The only sanctioned way to attach a CSRF filter: filter + <see cref="CsrfProtected"/> marker in one call, so
/// the boot parity guard can see it. Generic over the builder so the same call works on a single route and on a
/// route group (group metadata propagates to every child endpoint). Do not use a bare
/// <c>AddEndpointFilter&lt;CsrfFilter&gt;()</c> — the guard cannot see it and will fail the boot.
/// </summary>
internal static class CsrfProtection
{
    public static TBuilder RequireCsrf<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new CsrfProtected("AdminSession")).AddEndpointFilter<TBuilder, CsrfFilter>();

    public static TBuilder RequireUserCsrf<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new CsrfProtected("MerchantUserSession")).AddEndpointFilter<TBuilder, UserCsrfFilter>();
}

/// <summary>
/// Boot-time parity guard for CSRF coverage, the same shape as <see cref="PermissionParity"/>: every endpoint
/// that accepts an unsafe method under a cookie-session policy (<see cref="AuthPolicyScheme"/>) must carry the
/// CSRF filter of its own scheme — a missing or wrong-side filter is a boot failure, not a runtime gap.
/// Endpoints with no recognized policy (PSP webhooks, anonymous pre-session routes) have no cookie session to
/// forge and are skipped. Pure in-memory (no DB); call right before <c>app.Run()</c>, after all endpoints are
/// mapped.
/// </summary>
internal static class CsrfParity
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    // Takes the route builder, NOT IServiceProvider: before app.Run() the DI EndpointDataSource composite is
    // still empty (minimal-API sources attach at host start), so a service-provider walk sees 0 endpoints and
    // silently guards nothing. app.DataSources is populated at map time.
    public static void Assert(IEndpointRouteBuilder app)
    {
        var endpoints = app.DataSources.SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => (
                Pattern: e.RoutePattern.RawText ?? "?",
                Methods: (IReadOnlyList<string>?)e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods,
                Policy: e.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => !string.IsNullOrEmpty(p)),
                Schemes: (IReadOnlyList<string>)[.. e.Metadata.GetOrderedMetadata<CsrfProtected>().Select(m => m.SchemeId)]));
        var problems = FindProblems(endpoints);
        if (problems.Count > 0)
            throw new InvalidOperationException("CSRF parity violation(s): " + string.Join(" ", problems));
    }

    /// <summary>Pure — unit-testable. Rules: an endpoint whose policy maps through
    /// <see cref="AuthPolicyScheme"/> and accepts an unsafe method must carry a <see cref="CsrfProtected"/>
    /// marker for that policy's scheme; a marker for any other scheme is always a problem (wrong-side filter
    /// reads the wrong cookie and silently 403s everything). Missing method metadata counts as unsafe
    /// (fail-closed). A marker on a safe-only endpoint is fine (GETs are attached deliberately, REQ-7.1).</summary>
    internal static IReadOnlyList<string> FindProblems(
        IEnumerable<(string Pattern, IReadOnlyList<string>? Methods, string? Policy, IReadOnlyList<string> Schemes)> endpoints)
    {
        var problems = new List<string>();
        foreach (var (pattern, methods, policy, schemes) in endpoints)
        {
            var mapped = AuthPolicyScheme.For(policy);
            if (mapped is null)
                continue;
            var required = mapped.Value.SchemeId;
            foreach (var wrong in schemes.Where(s => s != required))
                problems.Add($"'{pattern}' (policy '{policy}') carries a {wrong} CSRF filter — wrong side.");
            var hasUnsafe = methods is null || methods.Any(m => !SafeMethods.Contains(m));
            if (hasUnsafe && !schemes.Contains(required))
                problems.Add($"'{string.Join(",", methods ?? ["*"])} {pattern}' (policy '{policy}') has no {required} CSRF filter.");
        }
        return problems;
    }
}
