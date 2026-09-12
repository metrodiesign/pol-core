using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Api.Admins;

/// <summary>A Tier 0 employee-profile failure decided at the OIDC event (tier0-graph-employee-profile REQ-1/2). The
/// <see cref="Reason"/> IS the browser reason: <c>employee-profile-unavailable</c> (Graph transport/status/parse,
/// missing access token), <c>employee-profile-missing</c> (no employeeId), <c>employee-profile-invalid</c> (shape).
/// Carries no token, employeeId, URL or response body.</summary>
internal sealed class EmployeeProfileException(string reason) : Exception("Employee profile acquisition failed.")
{
    public const string Unavailable = "employee-profile-unavailable";
    public const string Missing = "employee-profile-missing";
    public const string Invalid = "employee-profile-invalid";

    public string Reason { get; } = reason;
}

/// <summary>
/// Reads <c>employeeId</c> from Microsoft Graph with the login's short-lived access token:
/// <c>GET {GraphBaseUrl}/v1.0/me?$select=employeeId</c> (REQ-1.3) over the named <see cref="ClientName"/>
/// <see cref="HttpClient"/> (10 s timeout, REQ-1.12), parsed with System.Text.Json (REQ-1.13). Called from
/// <c>OnTokenValidated</c> BEFORE any database access (REQ-1.9-1.11); one attempt only (REQ-1.18). Every failure
/// becomes an <see cref="EmployeeProfileException"/>; the log carries only the failure category, the HTTP status
/// class and the correlation id (REQ-1.22, 9.1-9.6) — never the token, the URL, the body or the exception.
/// </summary>
internal sealed class MicrosoftGraphEmployeeIdReader(
    IHttpClientFactory factory,
    IOptions<AdminAuthOptions> options,
    ILogger<MicrosoftGraphEmployeeIdReader> logger)
{
    public const string ClientName = "microsoft-graph";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Select = "/v1.0/me?$select=employeeId";

    /// <summary>Returns the RAW employeeId string (not yet normalised — <c>EmployeeIdPolicy</c> does that).</summary>
    public async Task<string> ReadAsync(string accessToken, string correlationId, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.GraphBaseUrl.TrimEnd('/') + Select);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw Fail("transport", statusClass: null, correlationId);
        }

        using (response)
        {
            var statusClass = (int)response.StatusCode / 100;
            if ((int)response.StatusCode != 200)
                throw Fail("status", statusClass, correlationId);

            try
            {
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("employeeId", out var employeeId)
                    || employeeId.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new EmployeeProfileException(EmployeeProfileException.Missing); // REQ-1.17
                if (employeeId.ValueKind != JsonValueKind.String)
                    throw new EmployeeProfileException(EmployeeProfileException.Invalid);
                return employeeId.GetString() ?? throw new EmployeeProfileException(EmployeeProfileException.Missing);
            }
            catch (JsonException)
            {
                throw Fail("parse", statusClass, correlationId); // REQ-1.16
            }
        }
    }

    private EmployeeProfileException Fail(string category, int? statusClass, string correlationId)
    {
        logger.LogWarning(
            "Graph employee lookup failed. Category {Category} StatusClass {StatusClass} CorrelationId {CorrelationId}",
            category, statusClass, correlationId);
        return new EmployeeProfileException(EmployeeProfileException.Unavailable);
    }
}
