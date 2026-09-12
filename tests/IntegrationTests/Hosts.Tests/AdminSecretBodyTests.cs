extern alias ApiHost;
using System.Text;
using ApiHost::Api.ControlPlane;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings REQ-4.12/4.13: credential-bearing bodies are bounded to 16 KiB before any
/// processing, and the response is marked <c>Cache-Control: no-store</c> whether the body is accepted or not.
/// Driven on a bare HttpContext: the limit is enforced on the byte stream, so a chunked request without
/// Content-Length is bounded exactly like one that declares it.
/// </summary>
public sealed class AdminSecretBodyTests
{
    private sealed record Body(Guid MerchantId, string? Psp, IReadOnlyDictionary<string, string>? Secrets);

    private static DefaultHttpContext Context(byte[] payload, bool declareLength)
    {
        var services = new ServiceCollection();
        services.Configure<JsonOptions>(_ => { });
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Body = new MemoryStream(payload);
        if (declareLength)
            http.Request.ContentLength = payload.Length;
        return http;
    }

    [Fact]
    public async Task A_body_within_the_limit_is_parsed_and_marked_no_store()
    {
        var http = Context(Encoding.UTF8.GetBytes("""{"merchantId":"a1000000-0000-4000-8000-000000000011","psp":"2c2p","secrets":{"secretKey":"k"}}"""), true);

        var body = await AdminControlEndpoints.ReadSecretBodyAsync<Body>(http, default);

        Assert.Equal("2c2p", body.Psp);
        Assert.Equal("k", body.Secrets!["secretKey"]);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_body_over_16_KiB_is_refused_before_parsing_with_or_without_content_length(bool declareLength)
    {
        var oversized = Encoding.UTF8.GetBytes("{\"secrets\":{\"secretKey\":\"" + new string('k', AdminControlEndpoints.SecretBodyLimit) + "\"}}");
        var http = Context(oversized, declareLength);

        await Assert.ThrowsAsync<AdminControlEndpoints.SecretBodyTooLargeException>(() =>
            AdminControlEndpoints.ReadSecretBodyAsync<Body>(http, default));

        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    public async Task An_empty_or_malformed_body_is_a_400_validation_failure(string raw)
    {
        var http = Context(Encoding.UTF8.GetBytes(raw), true);

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            AdminControlEndpoints.ReadSecretBodyAsync<Body>(http, default));

        Assert.Equal("validation_failed", error.Code);
    }
}
