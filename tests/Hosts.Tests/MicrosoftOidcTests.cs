extern alias ApiHost;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// Shared Microsoft provider helpers plus the Admin Tier 0 tenant-aware immutable-identity gate.
public sealed class MicrosoftOidcTests
{
    private const string Tid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string Oid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

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

    [Fact]
    public void Workforce_claims_require_tid_and_oid_and_keep_email_as_trimmed_contact_only()
    {
        var result = ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid.ToUpperInvariant()), ("oid", Oid.ToUpperInvariant()),
                ("email", "  Employee@VIRIYAH.CO.TH  ")),
            Guid.Parse(Tid), out var claims);

        Assert.True(result);
        Assert.Equal(Guid.Parse(Tid), claims.TenantId);
        Assert.Equal(Guid.Parse(Oid), claims.ObjectId);
        Assert.Equal(Oid, claims.Subject);
        Assert.Equal("Employee@VIRIYAH.CO.TH", claims.Email);
    }

    [Fact]
    public void Workforce_claims_allow_missing_email_and_never_fall_back_to_preferred_username()
    {
        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid),
                ("preferred_username", "employee@viriyah.co.th")),
            Guid.Parse(Tid), out var claims));
        Assert.Null(claims.Email);
    }

    [Theory]
    [InlineData("tid")]
    [InlineData("oid")]
    public void Workforce_claims_reject_duplicate_identity_claims(string duplicatedType)
    {
        var claims = new List<(string Type, string Value)>
        {
            ("tid", Tid), ("oid", Oid),
        };
        claims.Add((duplicatedType, claims.First(c => c.Type == duplicatedType).Value));

        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal([.. claims]), Guid.Parse(Tid), out _));
    }

    [Fact]
    public void Duplicate_blank_or_overlength_email_is_ignored_without_invalidating_the_identity_tuple()
    {
        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid), ("email", "a@example.com"), ("email", "b@example.com")),
            Guid.Parse(Tid), out var duplicate));
        Assert.Null(duplicate.Email);

        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid), ("email", "  ")),
            Guid.Parse(Tid), out var blank));
        Assert.Null(blank.Email);

        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid), ("email", new string('a', 321))),
            Guid.Parse(Tid), out var overlength));
        Assert.Null(overlength.Email);
    }

    [Fact]
    public void A_single_trimmed_email_claim_has_no_domain_or_address_shape_gate()
    {
        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid), ("email", "  contact-label  ")),
            Guid.Parse(Tid), out var claims));
        Assert.Equal("contact-label", claims.Email);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("roles", "")]
    [InlineData("roles", "unrelated")]
    [InlineData("preferred_username", "other@example.com")]
    public void Workforce_claims_ignore_unrelated_claims(string? claimType, string? claimValue)
    {
        var claims = new List<(string Type, string Value)> { ("tid", Tid), ("oid", Oid) };
        if (claimType is not null)
            claims.Add((claimType, claimValue!));

        Assert.True(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal([.. claims]), Guid.Parse(Tid), out _));
    }

    [Fact]
    public void Workforce_claims_require_one_non_empty_guid_for_both_tuple_dimensions_and_the_pinned_tenant()
    {
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Guid.NewGuid().ToString()), ("oid", Oid)), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", "not-a-guid"), ("oid", Oid)), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Guid.Empty.ToString()), ("oid", Oid)), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid)), configuredTenant: null, out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Oid)), Guid.Empty, out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", "not-a-guid")), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid), ("oid", Guid.Empty.ToString())), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("oid", Oid)), Guid.Parse(Tid), out _));
        Assert.False(ApiHost::Api.Admins.MicrosoftWorkforceClaimsValidator.TryValidate(
            Principal(("tid", Tid)), Guid.Parse(Tid), out _));
    }

    [Fact]
    public void Workforce_failure_classifier_exposes_only_policy_or_protocol_reasons()
    {
        var policy = new DefaultHttpContext();
        ApiHost::Api.Admins.MicrosoftOidcFailureClassifier.MarkPolicyFailure(policy);
        Assert.Equal("workforce-access-denied",
            ApiHost::Api.Admins.MicrosoftOidcFailureClassifier.BrowserReason(policy, null));

        var issuer = new DefaultHttpContext();
        Assert.Equal("workforce-access-denied",
            ApiHost::Api.Admins.MicrosoftOidcFailureClassifier.BrowserReason(
                issuer, new Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException()));

        Assert.Equal("auth-failed",
            ApiHost::Api.Admins.MicrosoftOidcFailureClassifier.BrowserReason(
                new DefaultHttpContext(), new InvalidOperationException("signature details must not be parsed")));
    }

    [Fact]
    public void Admin_oidc_registration_ignores_google_even_when_google_is_configured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminAuth:Providers:Google:ClientId"] = "google-client",
            ["AdminAuth:Providers:Google:ClientSecret"] = "google-secret",
            ["AdminAuth:Providers:Google:Authority"] = "https://accounts.google.com",
            ["AdminAuth:Providers:Google:CallbackPath"] = "/api/v1/admins/auth/google/callback",
            ["AdminAuth:Providers:Microsoft:Authority"] = $"https://login.microsoftonline.com/{Tid}/v2.0",
            ["AdminAuth:Providers:Microsoft:ClientId"] = "microsoft-client",
            ["AdminAuth:Providers:Microsoft:ClientSecret"] = "microsoft-secret",
            ["AdminAuth:Providers:Microsoft:CallbackPath"] = "/api/v1/admins/auth/microsoft/callback",
        }).Build();
        var services = new ServiceCollection();

        ApiHost::Api.Admins.OidcAuthentication.AddAdminOidcAuthentication(services, config, new TestEnvironment());

        var providers = Assert.IsType<ApiHost::Api.Admins.AdminOidcProviders>(
            services.Single(descriptor => descriptor.ServiceType == typeof(ApiHost::Api.Admins.AdminOidcProviders))
                .ImplementationInstance);
        Assert.Equal("AdminMicrosoft", providers["microsoft"]);
        Assert.DoesNotContain("google", providers.Keys);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Hosts.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
