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

/// <summary>The Graph <c>/me</c> profile fields the login reads: the mandatory <c>employeeId</c> (governs login
/// success) plus an optional contact <c>Email</c> (RAW, from <c>mail</c> or <c>userPrincipalName</c>) used ONLY as a
/// fallback when the id_token carried no email claim. Neither value is normalised here.</summary>
internal readonly record struct GraphProfile(string EmployeeId, string? Email);

/// <summary>
/// Reads the Graph <c>/me</c> profile with the login's short-lived access token:
/// <c>GET {GraphBaseUrl}/v1.0/me?$select=employeeId,mail,userPrincipalName</c> over the named
/// <see cref="ClientName"/> <see cref="HttpClient"/> (10 s timeout, REQ-1.12), parsed with System.Text.Json
/// (REQ-1.13). Called from <c>OnTokenValidated</c> BEFORE any database access (REQ-1.9-1.11); one attempt only
/// (REQ-1.18). <c>employeeId</c> is mandatory and every failure becomes an <see cref="EmployeeProfileException"/>;
/// <c>mail</c>/<c>userPrincipalName</c> are best-effort and never fail the read. The log carries only the failure
/// category, the HTTP status class and the correlation id (REQ-1.22, 9.1-9.6) — never the token, URL, body or claim.
/// </summary>
internal sealed class MicrosoftGraphEmployeeIdReader(
    IHttpClientFactory factory,
    IOptions<AdminAuthOptions> options,
    ILogger<MicrosoftGraphEmployeeIdReader> logger)
{
    public const string ClientName = "microsoft-graph";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Select = "/v1.0/me?$select=employeeId,mail,userPrincipalName";

    /// <summary>Returns the RAW employeeId (governs success) and an optional RAW contact email fallback; neither is
    /// normalised — <c>EmployeeIdPolicy</c> and <c>AdminContactEmail</c> do that at the call site.</summary>
    public async Task<GraphProfile> ReadAsync(string accessToken, string correlationId, CancellationToken cancellationToken)
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
                var root = document.RootElement;
                if (!root.TryGetProperty("employeeId", out var employeeId)
                    || employeeId.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new EmployeeProfileException(EmployeeProfileException.Missing); // REQ-1.17
                if (employeeId.ValueKind != JsonValueKind.String)
                    throw new EmployeeProfileException(EmployeeProfileException.Invalid);
                var id = employeeId.GetString() ?? throw new EmployeeProfileException(EmployeeProfileException.Missing);
                return new GraphProfile(id, ReadContactEmail(root));
            }
            catch (JsonException)
            {
                throw Fail("parse", statusClass, correlationId); // REQ-1.16
            }
        }
    }

    // Best-effort contact fallback: prefer mail, else userPrincipalName. Returned RAW; AdminContactEmail decides
    // whether it is a usable address. A missing/blank/non-string value simply yields null (never fails the read).
    private static string? ReadContactEmail(JsonElement root)
    {
        if (root.TryGetProperty("mail", out var mail)
            && mail.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mail.GetString()))
            return mail.GetString();
        if (root.TryGetProperty("userPrincipalName", out var upn)
            && upn.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(upn.GetString()))
            return upn.GetString();
        return null;
    }

    private EmployeeProfileException Fail(string category, int? statusClass, string correlationId)
    {
        logger.LogWarning(
            "Graph employee lookup failed. Category {Category} StatusClass {StatusClass} CorrelationId {CorrelationId}",
            category, statusClass, correlationId);
        return new EmployeeProfileException(EmployeeProfileException.Unavailable);
    }
}
