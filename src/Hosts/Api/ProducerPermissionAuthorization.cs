using Producer.Application;
using Producer.Domain;

namespace Api;

/// <summary>Marks an endpoint as requiring a specific producer permission (REQ-17.2). The boot parity guard reads this
/// metadata; the filter below enforces it.</summary>
internal sealed record RequiredProducerPermission(string Permission);

/// <summary>Permission gate for role-driven producer actions (REQ-17.2): 403 unless the per-request effective
/// permission set (resolved into <see cref="IProducerScope"/> by the auth handler) contains the required key. Reads
/// the scope rather than a claim to keep the principal lean and the decision fresh. <b>Fail-closed when no producer is
/// bound</b> — so a request authenticated via the tenant Bearer scheme (REQ-17.3) that binds no producer scope is
/// admitted by the <c>producer</c> policy yet denied 403 here (authentication passes, authorization fails — F10).</summary>
// ponytail: DUPLICATE-shaped of Api.AdminPermissionAuthorization (IAdminScope -> IProducerScope) — deliberate debt.
internal static class ProducerPermissionAuthorization
{
    public static RouteHandlerBuilder RequireProducerPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.WithMetadata(new RequiredProducerPermission(permission));
        return builder.AddEndpointFilter(async (context, next) =>
            IsAllowed(context.HttpContext.RequestServices.GetRequiredService<IProducerScope>(), permission)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "You do not have permission for this action."));
    }

    /// <summary>Fail-closed permission decision (REQ-17.2/F10): a bound producer whose effective set holds the key.</summary>
    internal static bool IsAllowed(IProducerScope scope, string permission) =>
        scope.IsBound && scope.Current.Permissions.Contains(permission);
}

/// <summary>
/// Fail-closed gate for the WHOLE authenticated producer BFF surface (REQ-17.2/F10): every route in the
/// <c>/producer</c> group is for a BOUND producer. A tenant-Bearer caller admitted by the dual-scheme <c>producer</c>
/// policy binds no producer scope, so it is denied 403 here — the same posture <c>/producer/me</c> enforces inline,
/// applied groupwide so a tenant-Bearer token cannot read the producer role/permission catalog or hit logout-all
/// (which dereferences <c>scope.Current</c>). The shared product/payment writes that legitimately accept tenant-Bearer
/// are mapped OUTSIDE this group, so they are untouched.
/// </summary>
internal sealed class ProducerBoundProducerFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.RequestServices.GetRequiredService<IProducerScope>().IsBound
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your producer account is not active.");
}

/// <summary>Boot-time parity guard (REQ-15.5): every permission key a <c>RequireProducerPermission</c> gate references
/// MUST exist in the code-canonical catalog vocabulary (<see cref="ProducerPermissions.AllKeys"/>, which the migration
/// seeds the DB from). Pure in-memory — no DB. Call right before <c>app.Run()</c>, after all endpoints are mapped.</summary>
// ponytail: DUPLICATE-shaped of Api.AdminPermissionParity (AdminPermissions -> ProducerPermissions) — deliberate debt.
internal static class ProducerPermissionParity
{
    public static void Assert(IServiceProvider services)
    {
        var gated = services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<RequiredProducerPermission>())
            .Select(m => m.Permission);
        var unknown = FindUnknown(gated);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"RequireProducerPermission references permission key(s) absent from the catalog: {string.Join(", ", unknown)}.");
    }

    /// <summary>The gated keys that are NOT in the code-canonical catalog (REQ-15.5). Pure — unit-testable.</summary>
    internal static IReadOnlyList<string> FindUnknown(IEnumerable<string> gatedKeys) =>
        [.. gatedKeys.Distinct(StringComparer.Ordinal).Where(p => !ProducerPermissions.AllKeys.Contains(p))];
}
