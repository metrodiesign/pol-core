using Microsoft.AspNetCore.Builder;

namespace Api.IdentityAccess;

/// <summary>Mutation guard for identity-platform routes. Every identity caller presents a platform JWT in the
/// Authorization header, which a cross-site form or fetch cannot attach, so the guard only has to refuse
/// anything that is not a Bearer request; the CsrfProtected marker keeps the operation contract explicit.</summary>
internal static class IdentityPlatformMutationProtection
{
    public static TBuilder RequireIdentityPlatformMutation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new Api.Iam.CsrfProtected("IdentityPlatform"))
            .AddEndpointFilter<TBuilder, IdentityPlatformMutationFilter>();
}

internal sealed class IdentityPlatformMutationFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? next(context)
            : ValueTask.FromResult<object?>(Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "A platform Bearer token is required.",
                extensions: new Dictionary<string, object?> { ["code"] = "bearer_required" }));
}
