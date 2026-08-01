using Admins.Application;
using Iam.Domain.Permissions;
using Merchants.Application;
using Microsoft.AspNetCore.Authorization;

namespace Api.Iam;

/// <summary>
/// The one place a named authorization policy maps to the auth scheme it pins (REQ-10.3's literal scheme ids)
/// and, in turn, the permission-catalog <see cref="Scope"/> that scheme implies (REQ-5.4). Both the OpenAPI
/// document transformer (Program.cs — which scheme an operation requires) and the boot parity guard below
/// (which side a gated key must belong to) read this single table, so the two can never silently drift apart.
/// </summary>
internal static class AuthPolicyScheme
{
    public static (string SchemeId, Scope Side)? For(string? policy) => policy switch
    {
        "admin" => ("AdminSession", Scope.Platform),
        "merchant-user" => ("MerchantUserSession", Scope.Merchant),
        _ => null,
    };
}

/// <summary>Marks an endpoint as requiring a specific permission key (REQ-4.1), replacing the two duplicated
/// admin/merchant-user metadata types. The boot parity guard reads this metadata; the filter below enforces
/// it.</summary>
internal sealed record RequiredPermission(string Permission);

/// <summary>
/// Single permission gate for both consoles (REQ-4.1/4.2/4.3): reads whichever scope is bound to this request —
/// <see cref="IAdminScope"/> first, then <see cref="IUserScope"/> — and checks its effective permission set
/// (resolved fresh per request by the auth handler, never a claim) for the required key. Fail-closed: neither
/// scope bound, or the bound one missing the key, is 403 — never a 500. The two scope types never both bind on
/// one request (mutually exclusive auth schemes/policies), so checking admin first is safe and deterministic.
/// </summary>
internal static class PermissionAuthorization
{
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.WithMetadata(new RequiredPermission(permission));
        return builder.AddEndpointFilter(async (context, next) =>
            IsAllowed(
                context.HttpContext.RequestServices.GetRequiredService<IAdminScope>(),
                context.HttpContext.RequestServices.GetRequiredService<IUserScope>(),
                permission)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "You do not have permission for this action."));
    }

    /// <summary>Fail-closed permission decision (REQ-4.3): whichever scope is bound must hold the key.</summary>
    internal static bool IsAllowed(IAdminScope adminScope, IUserScope userScope, string permission) =>
        adminScope.IsBound
            ? adminScope.Current.Permissions.Contains(permission)
            : userScope.IsBound && userScope.Current.Permissions.Contains(permission);
}

/// <summary>
/// Boot-time parity guard (REQ-5), side-aware: every <see cref="RequiredPermission"/> key must (a) exist in the
/// catalog vocabulary and (b) belong to the <see cref="Scope"/> its endpoint's own auth policy implies (via
/// <see cref="AuthPolicyScheme"/>) — an endpoint under the "admin" policy (AdminSession scheme) gated with a
/// Merchant-side key, or vice versa, is a boot failure, not a runtime surprise (REQ-5.4). An endpoint gated by a
/// policy <see cref="AuthPolicyScheme"/> does not recognize is likewise a boot failure — a new policy must be
/// taught to the table before it can gate a permission. Pure in-memory (no DB); call right before
/// <c>app.Run()</c>, after all endpoints are mapped.
/// </summary>
internal static class PermissionParity
{
    // Takes the route builder, NOT IServiceProvider: before app.Run() the DI EndpointDataSource composite is
    // still empty (minimal-API sources attach at host start), so the previous service-provider walk saw 0
    // endpoints and silently guarded nothing. app.DataSources is populated at map time.
    public static void Assert(IEndpointRouteBuilder app)
    {
        var gated = app.DataSources.SelectMany(ds => ds.Endpoints)
            .SelectMany(e =>
            {
                var policy = e.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => !string.IsNullOrEmpty(p));
                return e.Metadata.GetOrderedMetadata<RequiredPermission>().Select(m => (m.Permission, Policy: policy));
            });
        var problems = FindProblems(gated);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "RequirePermission parity violation(s): " + string.Join(" ", problems));
    }

    /// <summary>Pure — unit-testable (REQ-5.1/5.4). For each gated (key, policy) pair: the key must be in
    /// <see cref="Keys.AllKeys"/>, the policy must be one <see cref="AuthPolicyScheme"/> recognizes, and the
    /// key's own side (<see cref="Keys.KeySide"/>) must match the side that policy implies.</summary>
    internal static IReadOnlyList<string> FindProblems(IEnumerable<(string Key, string? Policy)> gated)
    {
        var problems = new List<string>();
        foreach (var (key, policy) in gated.Distinct())
        {
            if (!Keys.AllKeys.Contains(key))
            {
                problems.Add($"'{key}' is absent from the catalog.");
                continue;
            }
            var mapped = AuthPolicyScheme.For(policy);
            if (mapped is null)
            {
                problems.Add($"'{key}' is gated under unrecognized policy '{policy}'.");
                continue;
            }
            if (Keys.KeySide[key] != mapped.Value.Side)
                problems.Add($"'{key}' is {Keys.KeySide[key]}-side but gated under policy '{policy}' ({mapped.Value.Side}-side).");
        }
        return problems;
    }
}
