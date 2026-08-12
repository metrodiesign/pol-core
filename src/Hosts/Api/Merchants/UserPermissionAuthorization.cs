using Merchants.Application;
using Admins.Application;
using Api.Admins;
using Api.Iam;

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
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var selected = context.HttpContext.Features.Get<SelectedConsoleAudience>()?.Value;
        var services = context.HttpContext.RequestServices;
        var bound = selected == ConsoleAudience.Admin
            ? services.GetRequiredService<IAdminScope>().IsBound
            : services.GetRequiredService<IUserScope>().IsBound;
        return bound
            ? await next(context)
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "The selected console account is not active.");
    }
}
