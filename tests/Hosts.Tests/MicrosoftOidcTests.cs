extern alias ApiHost;
using System.Security.Claims;

namespace Hosts.Tests;

// The Entra deltas (OidcProviderOptions.cs): the OPTIONAL AllowedTenants tid gate (issuer validation itself is
// the framework default against the tenant-pinned Authority's metadata issuer — no custom validator to test),
// oid-as-subject, and the email -> preferred_username fallback.
public sealed class MicrosoftOidcTests
{
    private const string Tid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value))));

    [Fact]
    public void An_empty_allowlist_admits_even_a_token_without_tid()
    {
        // Pinned Authority + empty allowlist must log in: tenant isolation comes from issuer validation (REQ-2.4).
        Assert.Null(ApiHost::Api.MicrosoftOidc.TenantGate(Principal(), []));
        Assert.Null(ApiHost::Api.MicrosoftOidc.TenantGate(Principal(("tid", Tid)), []));
    }

    [Fact]
    public void A_non_empty_allowlist_requires_a_tid_claim()
    {
        Assert.Equal("tid-required", ApiHost::Api.MicrosoftOidc.TenantGate(Principal(), [Tid]));
        Assert.Equal("tid-required", ApiHost::Api.MicrosoftOidc.TenantGate(Principal(("tid", "")), [Tid]));
    }

    [Fact]
    public void A_non_empty_allowlist_rejects_an_outside_tenant_and_admits_a_listed_one()
    {
        Assert.Equal("tenant-not-allowed",
            ApiHost::Api.MicrosoftOidc.TenantGate(Principal(("tid", Tid)), ["bbbbbbbb-0000-0000-0000-000000000000"]));
        Assert.Null(ApiHost::Api.MicrosoftOidc.TenantGate(Principal(("tid", Tid)), [Tid]));
        Assert.Null(ApiHost::Api.MicrosoftOidc.TenantGate(Principal(("tid", Tid.ToUpperInvariant())), [Tid]));
    }

    [Fact]
    public void Subject_is_oid_never_sub()
    {
        var principal = Principal(("sub", "pairwise-sub"), ("oid", "stable-oid"));
        Assert.Equal("stable-oid", ApiHost::Api.MicrosoftOidc.Subject(principal));
    }

    [Fact]
    public void Email_prefers_the_email_claim_then_an_at_shaped_preferred_username()
    {
        Assert.Equal("a@org.com", ApiHost::Api.MicrosoftOidc.Email(Principal(("email", "a@org.com"), ("preferred_username", "b@org.com"))));
        Assert.Equal("b@org.com", ApiHost::Api.MicrosoftOidc.Email(Principal(("preferred_username", "b@org.com"))));
        Assert.Null(ApiHost::Api.MicrosoftOidc.Email(Principal(("preferred_username", "host/machine-account"))));
        Assert.Null(ApiHost::Api.MicrosoftOidc.Email(Principal()));
    }
}
