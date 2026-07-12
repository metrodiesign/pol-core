using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Application.Users.Permissions;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Merchants.Domain.Users.Permissions;

namespace Api.Merchants;

/// <summary>Marks an endpoint as requiring a specific merchant-user permission (REQ-17.2). The boot parity guard
/// reads this metadata; the filter below enforces it.</summary>
internal sealed record RequiredUserPermission(string Permission);

/// <summary>Permission gate for role-driven merchant-user actions (REQ-17.2): 403 unless the per-request effective
/// permission set (resolved into <see cref="IMerchantUserScope"/> by the auth handler) contains the required key.
/// Reads the scope rather than a claim to keep the principal lean and the decision fresh. <b>Fail-closed when no
/// merchant user is bound</b> (F10).</summary>
// ponytail: DUPLICATE-shaped of Api.AdminPermissionAuthorization (IAdminScope -> IMerchantUserScope) — deliberate debt.
internal static class UserPermissionAuthorization
{
    public static RouteHandlerBuilder RequireMerchantUserPermission(this RouteHandlerBuilder builder, string permission)
    {
        builder.WithMetadata(new RequiredUserPermission(permission));
        return builder.AddEndpointFilter(async (context, next) =>
            IsAllowed(context.HttpContext.RequestServices.GetRequiredService<IUserScope>(), permission)
                ? await next(context)
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "You do not have permission for this action."));
    }

    /// <summary>Fail-closed permission decision (REQ-17.2/F10): a bound merchant user whose effective set holds the key.</summary>
    internal static bool IsAllowed(IUserScope scope, string permission) =>
        scope.IsBound && scope.Current.Permissions.Contains(permission);
}

/// <summary>
/// Fail-closed gate for the WHOLE authenticated merchant-user BFF surface (REQ-17.2/F10): every route in the
/// <c>/merchants/users</c> group is for a BOUND merchant user. The pre-session routes (login/callback/register) are
/// mapped OUTSIDE this group, so they are untouched by it.
/// </summary>
internal sealed class BoundFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.RequestServices.GetRequiredService<IUserScope>().IsBound
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your merchant-user account is not active.");
}

/// <summary>Boot-time parity guard (REQ-15.5): every permission key a <c>RequireMerchantUserPermission</c> gate
/// references MUST exist in the code-canonical catalog vocabulary (<see cref="MerchantUserPermissions.AllKeys"/>,
/// which the migration seeds the DB from). Pure in-memory — no DB. Call right before <c>app.Run()</c>, after all
/// endpoints are mapped.</summary>
// ponytail: DUPLICATE-shaped of Api.AdminPermissionParity (Keys -> MerchantUserPermissions) — deliberate.
internal static class UserPermissionParity
{
    public static void Assert(IServiceProvider services)
    {
        var gated = services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<RequiredUserPermission>())
            .Select(m => m.Permission);
        var unknown = FindUnknown(gated);
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"RequireMerchantUserPermission references permission key(s) absent from the catalog: {string.Join(", ", unknown)}.");
    }

    /// <summary>The gated keys that are NOT in the code-canonical catalog (REQ-15.5). Pure — unit-testable.</summary>
    internal static IReadOnlyList<string> FindUnknown(IEnumerable<string> gatedKeys) =>
        [.. gatedKeys.Distinct(StringComparer.Ordinal).Where(p => !Keys.AllKeys.Contains(p))];
}
