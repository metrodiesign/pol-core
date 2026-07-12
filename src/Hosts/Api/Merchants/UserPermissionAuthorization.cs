using Merchants.Application;

namespace Api.Merchants;

// RequiredUserPermission/UserPermissionAuthorization/UserPermissionParity moved to Api.Iam (rf2 REQ-4/5) — one
// gate + one boot parity guard now serve both the admin and merchant-user consoles.

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
