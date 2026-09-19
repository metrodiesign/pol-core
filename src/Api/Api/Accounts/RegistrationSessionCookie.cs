namespace Api.Accounts;

internal static class RegistrationSessionCookie
{
    public const string Name = "pol_registration_session";

    public static void Append(HttpContext http, string rawReference, TimeSpan lifetime) =>
        http.Response.Cookies.Append(Name, rawReference, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = lifetime,
        });
}
