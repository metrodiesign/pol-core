using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.OpenApi;

namespace Api;

internal sealed record EtagResponseMarker(string ResponseStatus);
internal sealed record IfMatchMutationMarker(string ResponseStatus, bool EmitsEtag = true);
internal sealed record IdempotencyMutationMarker;
internal sealed record AdminEtagResponseMarker(string ResponseStatus);
internal sealed record AdminIfMatchMutationMarker(string ResponseStatus, bool EmitsEtag = true);
internal sealed record AdminIdempotencyMutationMarker;

internal static class VersionEtags
{
    public static string Format(long version) => $"\"v{version}\"";

    public static long Require(HttpContext http)
    {
        var value = http.Request.Headers.IfMatch.ToString();
        if (value.Length < 4 || value[0] != '"' || value[^1] != '"' || value[1] != 'v'
            || !long.TryParse(value.AsSpan(2, value.Length - 3), NumberStyles.None,
                CultureInfo.InvariantCulture, out var version) || version < 0)
            throw new InvalidRequestException("If-Match must contain a current strong resource ETag.", "invalid_etag");
        return version;
    }

    public static void Set(HttpContext http, long version) => http.Response.Headers.ETag = Format(version);
}

internal static class IdempotencyKeys
{
    public static string Require(HttpContext http)
    {
        var value = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new InvalidRequestException(
                "Idempotency-Key must be a non-empty value of at most 200 characters.",
                "invalid_idempotency_key");
        return value.Trim();
    }
}

internal static class ConcurrencyOpenApi
{
    public static void Apply(OpenApiOperation operation, EtagResponseMarker marker) =>
        AddEtag(operation, marker.ResponseStatus);

    public static void Apply(OpenApiOperation operation, IfMatchMutationMarker marker)
    {
        RequireHeader(operation, "If-Match", "Current strong ETag from resource detail.");
        if (marker.EmitsEtag)
            AddEtag(operation, marker.ResponseStatus);
    }

    public static void Apply(OpenApiOperation operation, IdempotencyMutationMarker _) =>
        RequireHeader(operation, "Idempotency-Key", "Retry key scoped to actor and operation.");

    public static void Apply(OpenApiOperation operation, AdminEtagResponseMarker marker) =>
        AddEtag(operation, marker.ResponseStatus, required: false);

    public static void Apply(OpenApiOperation operation, AdminIfMatchMutationMarker marker)
    {
        RequireHeader(operation, "If-Match", "Required for AdminSession; omitted by MerchantUserSession.", false);
        if (marker.EmitsEtag)
            AddEtag(operation, marker.ResponseStatus, required: false);
    }

    public static void Apply(OpenApiOperation operation, AdminIdempotencyMutationMarker _) =>
        RequireHeader(operation, "Idempotency-Key",
            "Required for AdminSession; omitted by MerchantUserSession.", false);

    private static void AddEtag(OpenApiOperation operation, string responseStatus, bool required = true)
    {
        if (operation.Responses?.TryGetValue(responseStatus, out var response) != true
            || response is not OpenApiResponse concrete)
            return;
        concrete.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.OrdinalIgnoreCase);
        concrete.Headers["ETag"] = new OpenApiHeader
        {
            Required = required,
            Description = required
                ? "Optimistic resource version for If-Match."
                : "Present for AdminSession responses; use as If-Match on Admin mutations.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^\\\"v[0-9]+\\\"$" },
        };
    }

    private static void RequireHeader(
        OpenApiOperation operation, string name, string description, bool required = true)
    {
        var parameters = operation.Parameters ??= [];
        if (parameters.Any(x => x.In == ParameterLocation.Header
                                && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;
        parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}
