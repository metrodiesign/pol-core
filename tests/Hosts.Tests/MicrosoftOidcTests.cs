extern alias ApiHost;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hosts.Tests;

// The Entra deltas (OidcProviderOptions.cs): tid-consistent issuer validation (multi-tenant metadata publishes a
// TEMPLATE issuer, so a literal list can never match), oid-as-subject, and the email -> preferred_username fallback.
public sealed class MicrosoftOidcTests
{
    private const string Tid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private static JsonWebToken TokenWithTid(string? tid)
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var claims = new Dictionary<string, object>();
        if (tid is not null)
            claims["tid"] = tid;
        return new JsonWebToken(handler.CreateToken(System.Text.Json.JsonSerializer.Serialize(claims)));
    }

    [Fact]
    public void A_tid_consistent_issuer_passes()
    {
        var issuer = $"https://login.microsoftonline.com/{Tid}/v2.0";
        Assert.Equal(issuer, ApiHost::Api.MicrosoftOidc.ValidateIssuer(issuer, TokenWithTid(Tid), []));
    }

    [Fact]
    public void An_issuer_for_a_different_tenant_than_the_tid_claim_fails()
    {
        var issuer = "https://login.microsoftonline.com/aaaaaaaa-0000-0000-0000-000000000000/v2.0";
        Assert.Throws<SecurityTokenInvalidIssuerException>(() =>
            ApiHost::Api.MicrosoftOidc.ValidateIssuer(issuer, TokenWithTid(Tid), []));
    }

    [Fact]
    public void A_token_without_a_tid_claim_fails()
    {
        var issuer = $"https://login.microsoftonline.com/{Tid}/v2.0";
        Assert.Throws<SecurityTokenInvalidIssuerException>(() =>
            ApiHost::Api.MicrosoftOidc.ValidateIssuer(issuer, TokenWithTid(null), []));
    }

    [Fact]
    public void The_allowed_tenants_gate_rejects_a_foreign_tenant_and_admits_a_listed_one()
    {
        var issuer = $"https://login.microsoftonline.com/{Tid}/v2.0";
        Assert.Throws<SecurityTokenInvalidIssuerException>(() =>
            ApiHost::Api.MicrosoftOidc.ValidateIssuer(issuer, TokenWithTid(Tid), ["bbbbbbbb-0000-0000-0000-000000000000"]));
        Assert.Equal(issuer, ApiHost::Api.MicrosoftOidc.ValidateIssuer(issuer, TokenWithTid(Tid), [Tid]));
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value))));

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
