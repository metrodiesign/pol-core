using Microsoft.AspNetCore.Http;

namespace Api.BackgroundDispatch;

/// <summary>
/// Single scope-discriminator shared by the <see cref="IActorContext"/> and <c>IWriteAuthorizer</c>
/// registrations in <c>Program.cs</c> (multi-tier-deployment task 1 — Worker's hosted services merged
/// into this process). An HTTP request scope always has <see cref="IHttpContextAccessor.HttpContext"/> set
/// (Kestrel/middleware sets it before user code runs); a scope the outbox dispatcher creates via
/// <c>IServiceScopeFactory.CreateScope()</c> for a background batch never does. Extracted to one place so
/// both registrations branch on the exact same condition — see design.md's "highest-risk part of this
/// design" note on the security-boundary risk of the two branches ever disagreeing.
/// </summary>
internal static class BackgroundDispatchScope
{
    public static bool IsHttpRequest(IServiceProvider sp) =>
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext is not null;
}
