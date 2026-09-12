using System.Security.Claims;
using Admins.Application;
using Accounts.Application;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Merchants.Application;
using Orders.Application;

namespace Api.Iam;

internal static class IdentityPermissionAuthorization
{
    internal sealed record IdentityOrderPermissionMarker;

    public static bool IsIdentityRequest(HttpContext http) =>
        http.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || http.Request.Cookies.ContainsKey(Api.IdentityAccess.BffSessionManager.SessionCookieName)
        || http.Request.Cookies.ContainsKey(Api.IdentityAccess.BffSessionManager.SessionCookieNameDevHttp);

    public static bool IsIdentityOrderRoute(HttpContext http)
        => http.GetEndpoint()?.Metadata.GetMetadata<IdentityOrderPermissionMarker>() is not null;

    public static RouteHandlerBuilder RequireOrderIdentityPermission(
        this RouteHandlerBuilder builder, string humanPermission, string systemScope) =>
        builder.WithMetadata(new RequiredPermission(humanPermission), new IdentityOrderPermissionMarker())
            .AddEndpointFilter(async (context, next) =>
        {
            if (!IsIdentityRequest(context.HttpContext))
            {
                var allowed = PermissionAuthorization.IsAllowed(
                    context.HttpContext.RequestServices.GetRequiredService<IAdminScope>(),
                    context.HttpContext.RequestServices.GetRequiredService<IUserScope>(),
                    humanPermission);
                return allowed
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "You do not have permission for this action.");
            }
            return await CheckAsync(context, next, humanPermission, systemScope);
        });

    public static RouteHandlerBuilder RequireIdentityPermission(
        this RouteHandlerBuilder builder, string permission) =>
        builder.WithMetadata(new RequiredPermission(permission), new IdentityOrderPermissionMarker())
            .AddEndpointFilter(async (context, next) =>
        {
            return await CheckAsync(context, next, permission, "order.write");
        });

    public static CommerceAuthorizationProof? GetCommerceAuthorizationProof(HttpContext http) =>
        http.Items.TryGetValue(typeof(CommerceAuthorizationProof), out var value)
            ? value as CommerceAuthorizationProof
            : null;

    private static async ValueTask<object?> CheckAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        string humanPermission,
        string systemScope)
    {
            var http = context.HttpContext;
            var identity = await IdentityRequestAuthorization.ResolveMerchantAsync(
                http.User, http.RequestServices.GetRequiredService<IIdentityAccessQuery>(), http.RequestAborted);
            if (identity is null && !Guid.TryParse(http.User.FindFirstValue("merchant_id"), out _))
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "A verified merchant context is required.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "merchant_context_missing",
                    });

            if (identity is null)
                return Results.Forbid();
            var authorization = identity.Snapshot;
            var scoped = http.User.FindAll("scope").SelectMany(value =>
                value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var systemToken = http.User.FindFirst("client_id") is not null;
            var allowed = systemToken
                ? authorization is not null && scoped.Contains(systemScope, StringComparer.Ordinal)
                : authorization?.Permissions.Contains(humanPermission, StringComparer.Ordinal) == true;
            if (authorization is null || !allowed)
                return Results.Forbid();
            http.Items[typeof(CommerceAuthorizationProof)] = new CommerceAuthorizationProof(
                identity.AccountId,
                authorization.AuthorizationVersion,
                identity.MerchantId,
                identity.IsSystemClient ? http.User.FindFirstValue("client_id") : null,
                identity.IsSystemClient ? null : humanPermission,
                identity.IsSystemClient ? systemScope : null);
            using var actorBinding = http.RequestServices.GetRequiredService<IActorScope>()
                .Begin(identity.MerchantId, identity.AccountId);
            using var identityScope = http.RequestServices.GetRequiredService<IOrderIdentityAccessScope>()
                .Begin(identity.AccountId, authorization);
            return await next(context);
    }
}
