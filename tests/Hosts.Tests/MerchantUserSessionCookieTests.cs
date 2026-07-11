extern alias ApiHost;
using ApiHost::Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// Opaque token + cookie posture for the merchant-user BFF (REQ-10.2/10.3/13.1). The raw token is never the stored value
/// (only its SHA-256 hash); the session cookie is HttpOnly + Secure + __Host- prefixed; the CSRF cookie is
/// JS-readable; dev-http (Development over plain http) drops Secure + the prefix for localhost only. Names are
/// distinct from Admin's (REQ-14.4).
/// </summary>
public sealed class MerchantUserSessionCookieTests
{
    private static MerchantUserSessionCookies Cookies(string environment, string sameSite = "Lax") =>
        new(Options.Create(new MerchantUserSessionOptions { SameSite = sameSite }), new Env { EnvironmentName = environment });

    private static HttpContext Context(bool https)
    {
        var http = new DefaultHttpContext();
        http.Request.IsHttps = https;
        return http;
    }

    private static string SessionSetCookie(HttpContext http) =>
        http.Response.Headers.SetCookie.Single(v => v!.Contains("mch_session", StringComparison.Ordinal))!;

    private static string CsrfSetCookie(HttpContext http) =>
        http.Response.Headers.SetCookie.Single(v => v!.Contains("mch_csrf", StringComparison.Ordinal))!;

    [Fact]
    public void Opaque_token_is_random_and_only_its_hash_is_stored()
    {
        var a = MerchantUserTokens.NewOpaqueToken();
        var b = MerchantUserTokens.NewOpaqueToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        var hash = MerchantUserTokens.Hash(a);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, MerchantUserTokens.Hash(a));
        Assert.NotEqual(hash, MerchantUserTokens.Hash(b));
    }

    [Fact]
    public void Https_session_cookie_is_host_prefixed_httponly_secure_lax()
    {
        var http = Context(https: true);
        Cookies(Environments.Development).Write(http, "raw-token", "csrf-token");

        var session = SessionSetCookie(http);
        Assert.Contains("__Host-mch_session=raw-token", session, StringComparison.Ordinal);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", session, StringComparison.OrdinalIgnoreCase);

        var csrf = CsrfSetCookie(http);
        Assert.Contains("mch_csrf=csrf-token", csrf, StringComparison.Ordinal);
        Assert.DoesNotContain("httponly", csrf, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", csrf, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dev_http_drops_secure_and_the_host_prefix_for_localhost_only()
    {
        var http = Context(https: false);
        Cookies(Environments.Development).Write(http, "raw-token", "csrf-token");

        var session = SessionSetCookie(http);
        Assert.Contains("mch_session=raw-token", session, StringComparison.Ordinal);
        Assert.DoesNotContain("__Host-", session, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Outside_development_never_drops_secure_even_over_http()
    {
        var http = Context(https: false);
        Cookies(Environments.Production).Write(http, "raw-token", "csrf-token");

        var session = SessionSetCookie(http);
        Assert.Contains("__Host-mch_session=raw-token", session, StringComparison.Ordinal);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameSite_none_falls_back_to_lax_over_dev_http()
    {
        var http = Context(https: false);
        Cookies(Environments.Development, sameSite: "None").Write(http, "raw-token", "csrf-token");

        Assert.Contains("samesite=lax", SessionSetCookie(http), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameSite_none_is_honored_over_https()
    {
        var http = Context(https: true);
        Cookies(Environments.Production, sameSite: "None").Write(http, "raw-token", "csrf-token");

        Assert.Contains("samesite=none", SessionSetCookie(http), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_round_trips_the_session_and_csrf_cookies()
    {
        var cookies = Cookies(Environments.Production);
        var http = Context(https: true);
        http.Request.Headers.Cookie = "__Host-mch_session=tok-123; mch_csrf=csrf-123";

        Assert.Equal("tok-123", cookies.ReadSessionToken(http));
        Assert.Equal("csrf-123", cookies.ReadCsrfCookie(http));
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
