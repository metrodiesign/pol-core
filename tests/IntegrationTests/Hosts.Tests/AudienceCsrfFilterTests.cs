extern alias ApiHost;
using ApiHost::Api.Iam;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Http;

namespace Hosts.Tests;

public sealed class AudienceCsrfFilterTests
{
    private static readonly AudienceCsrfFilter Filter = new();
    private static readonly object Passed = new();

    private static async Task<object?> Run(ConsoleAudience? audience, string? cookie, string? header)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        if (cookie is not null)
            http.Request.Headers.Cookie = $"{UserSessionCookies.CsrfCookieName}={cookie}";
        if (header is not null)
            http.Request.Headers[UserCsrfFilter.HeaderName] = header;
        if (audience is { } value)
            http.Features.Set(new SelectedConsoleAudience(value));

        return await Filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ => ValueTask.FromResult<object?>(Passed));
    }

    private static int StatusOf(object? result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public async Task Admin_audience_is_bearer_authenticated_and_needs_no_csrf_pair() =>
        Assert.Same(Passed, await Run(ConsoleAudience.Admin, cookie: null, header: null));

    [Fact]
    public async Task Merchant_audience_reads_the_merchant_csrf_cookie() =>
        Assert.Same(Passed, await Run(ConsoleAudience.Merchant, "merchant-token", "merchant-token"));

    [Fact]
    public async Task Merchant_audience_without_the_pair_is_403() =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run(ConsoleAudience.Merchant, null, null)));

    [Fact]
    public async Task Missing_selected_audience_fails_closed() =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run(null, "merchant-token", "merchant-token")));
}
