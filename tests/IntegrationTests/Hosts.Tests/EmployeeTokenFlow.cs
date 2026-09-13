extern alias ApiHost;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

/// <summary>Drives the real workforce login against a SQL-backed test host the way the SPA does, minus Entra:
/// the verified account is placed in the short-lived login cookie (what the OIDC callback does), then
/// <c>GET /oauth/authorize</c> (code + PKCE) and <c>POST /oauth/token</c> run through OpenIddict unchanged.</summary>
internal static class EmployeeTokenFlow
{
    public const string DefaultRedirectUri = "https://spa.test/auth/callback";
    public const string ClientId = "pol-admin";

    public sealed record Tokens(string AccessToken, string RefreshToken, int ExpiresIn);

    /// <summary>The SPA callback the host accepts: the configured workforce origin when the host has one (so hosts
    /// sharing a database register the same client row), otherwise a fixed test origin.</summary>
    public static string RedirectUriFor(IServiceProvider services)
    {
        var origin = services.GetRequiredService<IOptions<ApiIdentity.IdentityAccessOptions>>().Value.WorkforceWebAppBaseUrl;
        return string.IsNullOrEmpty(origin)
            ? DefaultRedirectUri
            : origin.TrimEnd('/') + ApiIdentity.WorkforceClientRegistration.CallbackPath;
    }

    /// <summary>Registers the SPA client with the test redirect URI (the hosted registration only runs when the
    /// Entra provider is configured, which SQL test hosts do not need).</summary>
    public static async Task EnsureClientAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ApiIdentity.IdentityAccessOptions>>().Value;
        var descriptor = ApiIdentity.WorkforceClientRegistration.Describe(options);
        var redirectUri = new Uri(RedirectUriFor(services));
        if (!descriptor.RedirectUris.Contains(redirectUri))
            descriptor.RedirectUris.Add(redirectUri);
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var existing = await applications.FindByClientIdAsync(descriptor.ClientId!, default);
        if (existing is null)
            await applications.CreateAsync(descriptor, default);
        else
            await applications.UpdateAsync(existing, descriptor, default);
    }

    public static string LoginCookie(IServiceProvider services, Guid accountId)
    {
        var scheme = ApiIdentity.IdentityAccessWiring.LoginCookieScheme;
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var identity = new ClaimsIdentity(scheme);
        identity.AddClaim(new Claim("sub", accountId.ToString("D")));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2) },
            scheme);
        return $"{ApiIdentity.IdentityAccessWiring.LoginCookieName}={options.TicketDataFormat.Protect(ticket)}";
    }

    public static async Task<Tokens> LoginAsync(HttpClient client, IServiceProvider services, Guid accountId)
    {
        await EnsureClientAsync(services);
        var redirectUri = RedirectUriFor(services);
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var authorize = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(challenge, redirectUri));
        authorize.Headers.Add("Cookie", LoginCookie(services, accountId));
        using var authorizeResponse = await client.SendAsync(authorize);
        Assert.True(authorizeResponse.StatusCode == HttpStatusCode.Found,
            $"{(int)authorizeResponse.StatusCode}: {await authorizeResponse.Content.ReadAsStringAsync()}");
        var location = authorizeResponse.Headers.Location!.ToString();
        Assert.StartsWith(redirectUri, location, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location.Query);
        Assert.Equal("flow-state", query["state"].ToString());
        // The login cookie is single use: the authorize response discards it.
        Assert.Contains(authorizeResponse.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ApiIdentity.IdentityAccessWiring.LoginCookieName + "=", StringComparison.Ordinal)
                && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        return await ExchangeAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = query["code"].ToString(),
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = ClientId,
        });
    }

    public static string AuthorizeUrl(string challenge, string redirectUri = DefaultRedirectUri) => QueryHelpers.AddQueryString("/oauth/authorize",
        new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid offline_access",
            ["state"] = "flow-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

    public static Task<Tokens> RefreshAsync(HttpClient client, string refreshToken, Guid? merchantId = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        if (merchantId is { } selected)
            form["merchant_id"] = selected.ToString("D");
        return ExchangeAsync(client, form);
    }

    public static Task<HttpResponseMessage> TokenAsync(HttpClient client, Dictionary<string, string> form) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    private static async Task<Tokens> ExchangeAsync(HttpClient client, Dictionary<string, string> form)
    {
        using var response = await TokenAsync(client, form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return new Tokens(
            json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!,
            json.RootElement.GetProperty("expires_in").GetInt32());
    }
}
