using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payments.Tests.Psp;

/// <summary>What a request looked like at send time (snapshotted so it survives the adapter disposing it).</summary>
internal sealed record CapturedRequest(
    HttpMethod Method, Uri? Uri, string Body, string? IdempotencyKey, string? Authorization);

/// <summary>
/// A canned-response HttpMessageHandler test double — zero new test dependency (no WireMock). Records each
/// outbound request (so tests can assert the request shape + call count) and returns whatever the responder
/// delegate decides per (request, body). Routes by absolute path where it matters.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) =>
        _responder = responder;

    public List<CapturedRequest> Calls { get; } = [];

    public int CallCount => Calls.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.TryGetValues("Idempotency-Key", out var ik) ? ik.FirstOrDefault() : null,
            request.Headers.Authorization?.ToString()));
        return _responder(request, body);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>An IHttpClientFactory over a single stub handler. disposeHandler:false so the handler (and its
/// call log) survive the adapter's per-call `using var client`.</summary>
internal sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>Builds + decodes 2C2P-shape HS256 JWTs in tests (the adapter's codec is the contract under test,
/// so we independently re-derive it here rather than reach into the adapter).</summary>
internal static class JwtTestHelper
{
    public static string EncodeHs256(string claimsJson, string secret, string alg = "HS256")
    {
        var header = Base64Url(Encoding.UTF8.GetBytes($"{{\"alg\":\"{alg}\",\"typ\":\"JWT\"}}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(claimsJson));
        var signingInput = header + "." + payload;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + sig;
    }

    /// <summary>Wraps a signed JWT in the 2C2P {"payload": jwt} envelope.</summary>
    public static string Envelope(string jwt) => JsonSerializer.Serialize(new { payload = jwt });

    /// <summary>Decodes a JWT's payload claims (no verification) — for asserting an outbound request body.</summary>
    public static JsonElement DecodePayload(string jwt)
    {
        var middle = jwt.Split('.')[1];
        var s = middle.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    /// <summary>Pulls the inner JWT out of a {"payload": jwt} envelope body.</summary>
    public static string PayloadOf(string envelopeBody) =>
        JsonDocument.Parse(envelopeBody).RootElement.GetProperty("payload").GetString()!;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
