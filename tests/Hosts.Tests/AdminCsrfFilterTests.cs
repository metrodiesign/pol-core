extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Admins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hosts.Tests;

/// <summary>The admin CSRF double-submit filter (REQ-7.2): an unsafe-method request must carry an
/// <c>X-CSRF-Token</c> header equal to the <c>adm_csrf</c> cookie, else 403; safe methods are exempt.</summary>
public sealed class AdminCsrfFilterTests
{
    private static readonly CsrfFilter Filter = new();
    private static readonly object Passed = new();

    private static async Task<object?> Run(string method, string? cookie, string? header)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (cookie is not null)
            http.Request.Headers.Cookie = $"{SessionCookies.CsrfCookieName}={cookie}";
        if (header is not null)
            http.Request.Headers[CsrfFilter.HeaderName] = header;

        var context = EndpointFilterInvocationContext.Create(http);
        return await Filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Passed));
    }

    private static int StatusOf(object? result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static ProblemDetails ProblemOf(object? result) =>
        Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    [Fact]
    public async Task Matching_token_on_an_unsafe_method_passes() =>
        Assert.Same(Passed, await Run("POST", "tok-abc", "tok-abc"));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Missing_header_on_an_unsafe_method_is_403(string method) =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run(method, "tok-abc", header: null)));

    [Fact]
    public async Task Mismatched_header_is_403() =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run("POST", "tok-abc", "tok-XYZ")));

    [Fact]
    public async Task Missing_cookie_is_403() =>
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await Run("POST", cookie: null, header: "tok-abc")));

    [Fact]
    public async Task Rejection_has_stable_code_and_trace_id()
    {
        var problem = ProblemOf(await Run("PUT", cookie: null, header: null));

        Assert.Equal("csrf_failed", problem.Extensions["code"]);
        Assert.NotNull(problem.Extensions["traceId"]);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Safe_methods_skip_the_check(string method) =>
        Assert.Same(Passed, await Run(method, cookie: null, header: null));
}
