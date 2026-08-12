extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Admins;
using ApiHost::Api.Iam;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Http;

namespace Hosts.Tests;

public sealed class AudienceCsrfFilterTests
{
    private static readonly AudienceCsrfFilter Filter = new();
    private static readonly object Passed = new();

    private static async Task<object?> Run(
        ConsoleAudience? audience, string cookieName, string cookie, string header)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers.Cookie = $"{cookieName}={cookie}";
        http.Request.Headers[CsrfFilter.HeaderName] = header;
        if (audience is { } value)
            http.Features.Set(new SelectedConsoleAudience(value));

        return await Filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => ValueTask.FromResult<object?>(Passed));
    }

    private static int StatusOf(object? result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public async Task Admin_audience_reads_only_the_admin_csrf_cookie() =>
        Assert.Same(Passed, await Run(ConsoleAudience.Admin,
            SessionCookies.CsrfCookieName, "admin-token", "admin-token"));

    [Fact]
    public async Task Merchant_audience_reads_only_the_merchant_csrf_cookie() =>
        Assert.Same(Passed, await Run(ConsoleAudience.Merchant,
            UserSessionCookies.CsrfCookieName, "merchant-token", "merchant-token"));

    [Fact]
    public async Task Missing_selected_audience_fails_closed() =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run(
            null, SessionCookies.CsrfCookieName, "admin-token", "admin-token")));
}
