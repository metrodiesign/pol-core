extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class ApiOperationsContractTests
{
    private static readonly HashSet<string> HttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"];

    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-10.1")]
    [Trait("Requirement", "REQ-10.9")]
    public async Task Canonical_v1_operations_are_present_and_deferred_routes_are_absent()
    {
        using var factory = new ApiOperationsFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        var document = JsonDocument.Parse(body).RootElement;
        var actual = OpenApiOperations(document);
        var scope = LoadScope();
        var expected = scope.Where(x => x.Release == "v1").Select(x => Key(x.Method, x.Path)).ToHashSet();
        var deferred = scope.Where(x => x.Release != "v1").Select(x => Key(x.Method, x.Path)).ToHashSet();

        Assert.Equal(111, expected.Count);
        Assert.Equal(5, deferred.Count);
        var missing = expected.Except(actual.Keys).Order(StringComparer.Ordinal).ToArray();
        var exposedDeferred = deferred.Intersect(actual.Keys).Order(StringComparer.Ordinal).ToArray();
        Console.WriteLine($"API_COMPARATOR expected={expected.Count} actual={actual.Count} overlap={expected.Intersect(actual.Keys).Count()} missing={missing.Length} deferred={exposedDeferred.Length}");
        Assert.True(missing.Length == 0, "Missing canonical operations:\n" + string.Join("\n", missing));
        Assert.True(exposedDeferred.Length == 0, "Deferred operations exposed:\n" + string.Join("\n", exposedDeferred));
        Assert.Equal(actual.Count, actual.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expected, operation =>
        {
            Assert.True(actual.TryGetValue(operation, out var metadata), operation);
            Assert.False(string.IsNullOrWhiteSpace(metadata.OperationId), operation);
            Assert.NotEmpty(metadata.Tags);
        });
    }

    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-10.1")]
    [Trait("Requirement", "REQ-10.9")]
    public void EndpointDataSource_contains_the_canonical_operations_once()
    {
        using var factory = new ApiOperationsFactory();
        using var _ = factory.CreateClient();
        var actual = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?
                .HttpMethods.Select(method => Key(method, Normalize(endpoint.RoutePattern.RawText ?? string.Empty)))
                ?? [])
            .Where(x => HttpMethods.Contains(x.Split(' ', 2)[0], StringComparer.Ordinal))
            .ToList();
        var scope = LoadScope();
        var expected = scope.Where(x => x.Release == "v1").Select(x => Key(x.Method, x.Path)).ToHashSet();
        var deferred = scope.Where(x => x.Release != "v1").Select(x => Key(x.Method, x.Path)).ToHashSet();
        var missing = expected.Except(actual).Distinct(StringComparer.Ordinal).ToArray();
        var exposedDeferred = deferred.Intersect(actual).Distinct(StringComparer.Ordinal).ToArray();
        Console.WriteLine($"ENDPOINT_COMPARATOR expected={expected.Count} actual={actual.Count} overlap={expected.Intersect(actual).Count()} missing={missing.Length} deferred={exposedDeferred.Length}");

        Assert.True(missing.Length == 0,
            $"Endpoint comparator expected={expected.Count} actual={actual.Count} "
            + $"overlap={expected.Intersect(actual).Count()} missing={missing.Length} deferred={exposedDeferred.Length}: "
            + string.Join("; ", missing));
        Assert.True(exposedDeferred.Length == 0,
            $"Endpoint comparator exposed deferred={exposedDeferred.Length}: "
            + string.Join("; ", exposedDeferred));
        Assert.Equal(expected.Count, actual.Where(expected.Contains).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-10.1")]
    [Trait("Requirement", "REQ-10.2")]
    [Trait("Requirement", "REQ-10.3")]
    [Trait("Requirement", "REQ-10.6")]
    [Trait("Requirement", "REQ-10.9")]
    public async Task Every_v1_row_has_data_driven_auth_concurrency_error_and_schema_metadata()
    {
        using var factory = new ApiOperationsFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        var openapi = OpenApiOperations(document.RootElement);
        var scope = LoadScope().Where(x => x.Release == "v1").ToArray();
        var endpoints = EndpointMetadata(factory.Services);

        Assert.Equal(scope.Length, scope.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(scope.Length, scope.Select(x => Key(x.Method, x.Path)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(scope.Length, scope.Select(x => openapi[Key(x.Method, x.Path)].OperationId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count());

        var expectedBodySchemas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["API-012"] = "MerchantContextRequest",
            ["API-019"] = "AccountPatchRequest",
            ["API-022"] = "SystemClientCreateRequest",
            ["API-024"] = "SystemClientPatchRequest",
            ["API-025"] = "SystemClientAccessRequest",
            ["API-027"] = "ClientKeyCreateRequest",
            ["API-030"] = "RegistrationDraftRequest",
            ["API-038"] = "AgentRegistrationApproveRequest",
            ["API-039"] = "AgentRegistrationRejectRequest",
            ["API-044"] = "CanonicalRoleCreateRequest",
            ["API-046"] = "CanonicalRoleUpdateRequest",
            ["API-048"] = "MerchantAccessReplaceRequest",
            ["API-051"] = "PlatformAccessReplaceRequest",
            ["API-053"] = "ProvisionMerchantRequest",
            ["API-055"] = "CanonicalMerchantPatchRequest",
            ["API-057"] = "CreateBranchRequest",
            ["API-058"] = "PatchBranchRequest",
            ["API-060"] = "CreateSaleRequest",
            ["API-061"] = "PatchSaleRequest",
            ["API-065"] = "CanonicalProviderAccountCreateRequest",
            ["API-067"] = "CanonicalProviderAccountPatchRequest",
            ["API-069"] = "CanonicalCredentialVersionCreateRequest",
            ["API-071"] = "CanonicalDisableProviderAccountRequest",
            ["API-073"] = "CanonicalPaymentSettingChangeRequest",
            ["API-076"] = "CanonicalPaymentSettingDecisionRequest",
            ["API-077"] = "CanonicalPaymentSettingDecisionRequest",
            ["API-079"] = "CreateOrderRequest",
            ["API-081"] = "CanonicalPatchDraftOrderRequest",
            ["API-084"] = "CancelOrderRequest",
            ["API-087"] = "RotatePaymentLinkRequest",
            ["API-088"] = "RevokePaymentLinkRequest",
            ["API-089"] = "CheckoutAccessRequest",
            ["API-092"] = "CheckoutConfirmRequest",
            ["API-099"] = "CanonicalTransactionReviewRequest",
            ["API-107"] = "CanonicalNotificationRetryRequest",
            ["API-113"] = "CanonicalEventEndpointRequest",
        };
        var expectedRequiredFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["API-079"] = ["businessType", "currency", "items"],
            ["API-084"] = ["reason"],
            ["API-088"] = ["reason"],
            ["API-099"] = ["note"],
            ["API-107"] = ["reason"],
            ["API-113"] = ["enabled"],
        };
        var forbiddenServerFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["API-022"] = ["accountId", "status", "version", "secret"],
            ["API-065"] = ["merchantId", "status", "version"],
            ["API-081"] = ["orderId", "merchantId", "version", "totalAmount", "subtotalAmount"],
        };
        var expectedIfMatch = scope.Where(x => x.Rules.Contains("If-Match", StringComparison.Ordinal))
            .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var expectedIdempotency = scope.Where(x => x.Rules.Contains("Idempotency-Key", StringComparison.Ordinal))
            .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        expectedIfMatch.UnionWith(["API-019", "API-025", "API-048", "API-049", "API-051", "API-058",
            "API-067", "API-069", "API-070", "API-071", "API-073", "API-077", "API-084",
            "API-098", "API-099", "API-113"]);
        expectedIdempotency.UnionWith(["API-016", "API-019", "API-022", "API-024", "API-025", "API-027",
            "API-028", "API-044", "API-046", "API-048", "API-049", "API-051", "API-055", "API-057",
            "API-058", "API-060", "API-061", "API-065", "API-067", "API-069", "API-070", "API-071",
            "API-073", "API-076", "API-077", "API-084", "API-088", "API-094", "API-098",
            "API-099", "API-113"]);
        var concurrencyMismatches = new List<string>();
        var metadataMismatches = new List<string>();

        foreach (var row in scope)
        {
            var key = Key(row.Method, row.Path);
            if (!openapi.TryGetValue(key, out var operation))
            {
                metadataMismatches.Add($"OpenAPI missing {row.Id} {key}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(operation.OperationId))
                metadataMismatches.Add($"operationId missing {row.Id} {key}");
            if (operation.Tags.Count == 0)
                metadataMismatches.Add($"tags missing {row.Id} {key}");
            if (operation.Responses.Count == 0)
                metadataMismatches.Add($"responses missing {row.Id} {key}");
            if (!endpoints.TryGetValue(key, out var endpoint))
            {
                metadataMismatches.Add($"Endpoint metadata missing {row.Id} {key}");
                continue;
            }

            var anonymous = IsAnonymous(row);
            if (anonymous)
            {
                if (!endpoint.AllowAnonymous && endpoint.AuthorizationPolicies.Count != 0)
                    metadataMismatches.Add($"anonymous classification {row.Id} {key}");
            }
            else
            {
                if (endpoint.AuthorizationPolicies.Count == 0)
                    metadataMismatches.Add($"authorization policy missing {row.Id} {key}");
            }

            var permissionRequired = row.Id != "API-053" && (
                row.CallerAndPermission.Contains("ACCOUNT_MANAGE", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("ACCESS_MANAGE", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("MERCHANT_MANAGE", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("PROVIDER_MANAGE", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("AGENT_REVIEW", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("ORDER_", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("TRANSACTION_", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("NOTIFICATION_", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("AUDIT_READ", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("SETTINGS_MANAGE", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("order.write", StringComparison.Ordinal)
                || row.CallerAndPermission.Contains("transaction.verify", StringComparison.Ordinal));
            if (permissionRequired)
            {
                if (string.IsNullOrWhiteSpace(endpoint.Permission))
                    metadataMismatches.Add($"permission missing {row.Id} {key}");
            }

            var unsafeMethod = !HttpMethods.Contains(row.Method.ToUpperInvariant())
                || row.Method is "POST" or "PUT" or "PATCH" or "DELETE";
            var csrfExpected = unsafeMethod && !anonymous
                && !row.Path.StartsWith("/oauth/", StringComparison.Ordinal)
                && !row.Path.StartsWith("/api/v1/webhooks/", StringComparison.Ordinal)
                && !row.Path.StartsWith("/api/v1/payment-returns/", StringComparison.Ordinal);
            if (csrfExpected)
            {
                if (!endpoint.Csrf)
                    metadataMismatches.Add($"CSRF missing {row.Id} {key}");
            }

            if (expectedIfMatch.Contains(row.Id) != endpoint.IfMatch)
                concurrencyMismatches.Add($"If-Match {row.Id} expected={expectedIfMatch.Contains(row.Id)} actual={endpoint.IfMatch}");
            if (expectedIdempotency.Contains(row.Id) != endpoint.Idempotency)
                concurrencyMismatches.Add($"Idempotency {row.Id} expected={expectedIdempotency.Contains(row.Id)} actual={endpoint.Idempotency}");
            if (expectedBodySchemas.TryGetValue(row.Id, out var schemaName))
            {
                var requestRef = operation.RequestBodySchemaRef;
                if (requestRef is null || !requestRef.EndsWith(schemaName, StringComparison.Ordinal))
                    metadataMismatches.Add($"request schema {row.Id} expected={schemaName} actual={requestRef ?? "<none>"}");
                else
                {
                    var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
                        .GetProperty(schemaName);
                    if (expectedRequiredFields.TryGetValue(row.Id, out var required))
                    {
                        var requiredFields = schema.TryGetProperty("required", out var values)
                            ? values.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal)
                            : [];
                        foreach (var field in required)
                            if (!requiredFields.Contains(field))
                                metadataMismatches.Add($"required field {row.Id}.{field} missing");
                    }
                    if (forbiddenServerFields.TryGetValue(row.Id, out var forbidden)
                        && schema.TryGetProperty("properties", out var properties))
                    {
                        foreach (var field in forbidden)
                            if (properties.TryGetProperty(field, out _))
                                metadataMismatches.Add($"server field {row.Id}.{field} is exposed in request schema");
                    }
                }
            }
        }

        Assert.True(metadataMismatches.Count == 0, string.Join("\n", metadataMismatches));
        Assert.True(concurrencyMismatches.Count == 0, string.Join("; ", concurrencyMismatches));

        Console.WriteLine("API_METADATA_PROVED " + string.Join(",", scope.Select(x =>
            $"{x.Id}={openapi[Key(x.Method, x.Path)].OperationId}")));
    }

    private static Dictionary<string, OperationMetadata> OpenApiOperations(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(method => HttpMethods.Contains(method.Name.ToUpperInvariant()))
                .Select(method =>
                {
                    var operation = method.Value;
                    var tags = operation.TryGetProperty("tags", out var tagsElement)
                        ? tagsElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
                        : [];
                    var operationId = operation.TryGetProperty("operationId", out var id)
                        ? id.GetString()
                        : null;
                    var responses = operation.TryGetProperty("responses", out var responseElement)
                        ? responseElement.EnumerateObject().Select(x => x.Name).ToArray()
                        : [];
                    var requestRef = operation.TryGetProperty("requestBody", out var requestBody)
                        && requestBody.TryGetProperty("content", out var content)
                        && content.EnumerateObject().Select(x => x.Value)
                            .FirstOrDefault(x => x.TryGetProperty("schema", out _)) is { } contentElement
                        && contentElement.TryGetProperty("schema", out var schema)
                        && schema.TryGetProperty("$ref", out var reference)
                        ? reference.GetString()
                        : null;
                    return new
                    {
                        Key = Key(method.Name, Normalize(path.Name)),
                        Metadata = new OperationMetadata(operationId, tags, responses, requestRef),
                    };
                }))
            .ToDictionary(x => x.Key, x => x.Metadata, StringComparer.Ordinal);

    private static IReadOnlyList<ScopeOperation> LoadScope()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, ".ai", "specs", "platform-restructure-v1", "api-scope.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!.FullName, ".ai", "specs", "platform-restructure-v1", "api-scope.json")));
        return document.RootElement.GetProperty("operations").EnumerateArray()
            .Select(x => new ScopeOperation(
                x.GetProperty("id").GetString()!,
                x.GetProperty("method").GetString()!,
                x.GetProperty("path").GetString()!,
                x.GetProperty("release").GetString()!,
                x.GetProperty("callerAndPermission").GetString()!,
                x.GetProperty("rules").GetString()!))
            .ToArray();
    }

    private static Dictionary<string, EndpointMetadataView> EndpointMetadata(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
                return methods.Select(method => new
                {
                    Key = Key(method, Normalize(endpoint.RoutePattern.RawText ?? string.Empty)),
                    View = new EndpointMetadataView(
                        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                            .Select(x => x.Policy ?? x.AuthenticationSchemes ?? string.Empty).ToArray(),
                        endpoint.Metadata.FirstOrDefault(x => x.GetType().Name == "RequiredPermission") is { } required
                            ? required.GetType().GetProperty("Permission")?.GetValue(required)?.ToString()
                            : null,
                        endpoint.Metadata.Any(x => x.GetType().Name is "CsrfProtected" or "BffCsrfProtected"),
                        endpoint.Metadata.Any(x => x.GetType().Name is "IfMatchMutationMarker" or "AdminIfMatchMutationMarker"),
                        endpoint.Metadata.Any(x => x.GetType().Name is "IdempotencyMutationMarker" or "AdminIdempotencyMutationMarker")),
                }).ToArray();
            })
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single().View, StringComparer.Ordinal);

    private static bool IsAnonymous(ScopeOperation row) =>
        row.Path.StartsWith("/.well-known/", StringComparison.Ordinal)
        || row.Path.StartsWith("/oauth/", StringComparison.Ordinal)
        || row.Path.StartsWith("/health/", StringComparison.Ordinal)
        || row.Path.StartsWith("/api/v1/webhooks/", StringComparison.Ordinal)
        || row.Path.StartsWith("/api/v1/payment-returns/", StringComparison.Ordinal)
        || row.Path == "/api/v1/agent-registration"
        || row.Path.StartsWith("/api/v1/agent-registration/", StringComparison.Ordinal)
        || row.CallerAndPermission.StartsWith("ทุกคน", StringComparison.Ordinal)
        || row.CallerAndPermission.StartsWith("N —", StringComparison.Ordinal)
        || row.Id is "API-089" or "API-090" or "API-092" or "API-093" or "API-094"
        || row.CallerAndPermission.Contains("ผ่าน browser", StringComparison.Ordinal)
        || row.Path is "/api/v1/auth/employees/login" or "/api/v1/auth/agents/login"
            or "/api/v1/auth/employees/callback" or "/api/v1/auth/agents/callback";

    private static string Key(string method, string path) =>
        $"{method.Trim().ToUpperInvariant()} {Normalize(path)}";

    private static string Normalize(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path.TrimEnd('/'), @"\{([^}:]+)(?::[^}]+)?\}", "{$1}");

    private sealed record ScopeOperation(string Id, string Method, string Path, string Release,
        string CallerAndPermission, string Rules);
    private sealed record OperationMetadata(string? OperationId, IReadOnlyList<string> Tags,
        IReadOnlyList<string> Responses, string? RequestBodySchemaRef);
    private sealed record EndpointMetadataView(bool AllowAnonymous, IReadOnlyList<string> AuthorizationPolicies,
        string? Permission, bool Csrf, bool IfMatch, bool Idempotency);
}

file sealed class ApiOperationsFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
