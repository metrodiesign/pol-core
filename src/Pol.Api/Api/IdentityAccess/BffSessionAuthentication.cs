using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Api.IdentityAccess;

internal sealed record ProtectedBffTicket(
    Guid AccountId,
    string CsrfHash,
    string? AccessToken,
    string? RefreshToken,
    Guid? MerchantId,
    string ReturnTo);

internal sealed record BffSessionContext(
    BffSessionTicket Ticket,
    ProtectedBffTicket ProtectedTicket);

internal sealed record BffSessionIssue(string SessionToken, string CsrfToken, BffSessionTicket Ticket);

internal sealed class BffSessionManager(
    IBffSessionStore sessions,
    IDataProtectionProvider dataProtection,
    IClock clock,
    IOptions<IdentityAccessOptions> options)
{
    public const string SessionCookieName = "__Host-pol_session";
    public const string SessionCookieNameDevHttp = "pol_session";
    public const string CsrfCookieName = "pol_csrf";
    public const string HeaderName = "X-CSRF-Token";

    private readonly IDataProtector _protector =
        dataProtection.CreateProtector("pol.identity-access.bff.ticket.v1");

    public async Task<BffSessionIssue> CreateAsync(
        Account account,
        string? clientId,
        string? accessToken,
        string? refreshToken,
        Guid? merchantId,
        string returnTo,
        CancellationToken cancellationToken)
    {
        var sessionToken = NewToken();
        var csrfToken = NewToken();
        var payload = new ProtectedBffTicket(
            account.Id,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csrfToken))),
            accessToken,
            refreshToken,
            merchantId,
            returnTo);
        var protectedTicket = _protector.Protect(JsonSerializer.Serialize(payload));
        var now = clock.UtcNow;
        var ticket = BffSessionTicket.Create(
            Hash(sessionToken), account.Id, clientId, protectedTicket, account.AuthorizationVersion,
            now, now.AddMinutes(options.Value.BffSessionMinutes));
        sessions.Add(ticket);
        await sessions.SaveChangesAsync(cancellationToken);
        return new BffSessionIssue(sessionToken, csrfToken, ticket);
    }

    public async Task<BffSessionIssue> RotateAsync(
        BffSessionTicket current,
        Account account,
        string? accessToken,
        string? refreshToken,
        Guid? merchantId,
        string returnTo,
        CancellationToken cancellationToken)
    {
        var sessionToken = NewToken();
        var csrfToken = NewToken();
        var payload = new ProtectedBffTicket(
            account.Id,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csrfToken))),
            accessToken,
            refreshToken,
            merchantId,
            returnTo);
        var protectedTicket = _protector.Protect(JsonSerializer.Serialize(payload));
        var now = clock.UtcNow;
        var replacement = BffSessionTicket.Create(
            Hash(sessionToken), account.Id, current.ClientId, protectedTicket, account.AuthorizationVersion,
            now, now.AddMinutes(options.Value.BffSessionMinutes));
        await sessions.ReplaceAsync(current, replacement, now, cancellationToken);
        return new BffSessionIssue(sessionToken, csrfToken, replacement);
    }

    public ProtectedBffTicket? Unprotect(BffSessionTicket ticket)
    {
        try
        {
            return JsonSerializer.Deserialize<ProtectedBffTicket>(
                _protector.Unprotect(ticket.ProtectedAuthenticationTicket));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string NewToken() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool Matches(string raw, string expectedHash)
    {
        var actual = Convert.ToHexString(Hash(raw));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expectedHash));
    }

    public string ReadSessionToken(HttpContext http)
    {
        var cookieName = http.Request.IsHttps ? SessionCookieName : SessionCookieNameDevHttp;
        return http.Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrWhiteSpace(token)
            ? token
            : string.Empty;
    }

    public void WriteCookies(HttpContext http, string sessionToken, string csrfToken)
    {
        var secure = http.Request.IsHttps;
        var cookieName = secure ? SessionCookieName : SessionCookieNameDevHttp;
        var sameSite = secure ? SameSiteMode.Lax : SameSiteMode.Lax;
        http.Response.Cookies.Append(cookieName, sessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            Path = "/",
            IsEssential = true,
        });
        http.Response.Cookies.Append(CsrfCookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = secure,
            SameSite = sameSite,
            Path = "/",
            IsEssential = true,
        });
    }

    public void ClearCookies(HttpContext http)
    {
        var secure = http.Request.IsHttps;
        var cookieName = secure ? SessionCookieName : SessionCookieNameDevHttp;
        http.Response.Cookies.Delete(cookieName, new CookieOptions { Secure = secure, Path = "/" });
        http.Response.Cookies.Delete(CsrfCookieName, new CookieOptions { Secure = secure, Path = "/" });
    }
}

internal sealed class BffSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IdentityAccessBff";

    private readonly IBffSessionStore _sessions;
    private readonly IIdentityAccessQuery _identities;
    private readonly BffSessionManager _manager;
    private readonly IClock _clock;

    public BffSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IBffSessionStore sessions,
        IIdentityAccessQuery identities,
        BffSessionManager manager,
        IClock clock)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _identities = identities;
        _manager = manager;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasBearer = Context.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var rawSession = _manager.ReadSessionToken(Context);
        if (hasBearer && !string.IsNullOrEmpty(rawSession))
        {
            Context.Features.Set(new IdentityAccessAmbiguousAuthentication());
            return AuthenticateResult.Fail("ambiguous_authentication_context");
        }
        if (string.IsNullOrEmpty(rawSession))
            return AuthenticateResult.NoResult();

        var ticket = await _sessions.FindByHashAsync(BffSessionManager.Hash(rawSession), Context.RequestAborted);
        if (ticket is null)
            return AuthenticateResult.Fail("Unknown BFF session.");

        var account = await _identities.FindAccountAsync(ticket.AccountId, Context.RequestAborted);
        if (account is null || account.Status != AccountStatus.Active
            || !ticket.IsLiveAt(_clock.UtcNow, account.AuthorizationVersion))
            return AuthenticateResult.Fail("BFF session is inactive or stale.");

        var payload = _manager.Unprotect(ticket);
        if (payload is null || payload.AccountId != account.Id)
            return AuthenticateResult.Fail("BFF session ticket is invalid.");

        Context.Features.Set(new BffSessionContext(ticket, payload));

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("sub", account.Id.ToString("D")));
        identity.AddClaim(new Claim("account_type", account.AccountType.ToString().ToUpperInvariant()));
        identity.AddClaim(new Claim("authz_version", account.AuthorizationVersion.ToString()));
        identity.AddClaim(new Claim("token_context", payload.MerchantId is null ? "ACCOUNT_SELF" : "MERCHANT"));
        identity.AddClaim(new Claim("scope", payload.MerchantId is null ? "account.self" : "merchant"));
        if (!string.IsNullOrWhiteSpace(ticket.ClientId))
            identity.AddClaim(new Claim("client_id", ticket.ClientId));
        if (payload.MerchantId is { } merchantId)
            identity.AddClaim(new Claim("merchant_id", merchantId.ToString("D")));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
