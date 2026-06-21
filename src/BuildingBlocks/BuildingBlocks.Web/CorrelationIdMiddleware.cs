using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Web;

/// <summary>
/// Stamps every request with a correlation id: it reuses a caller-supplied <c>X-Correlation-ID</c> (when
/// well-formed) or mints one, echoes it on the response, and pushes it — plus the bound tenant id when one
/// exists — into the logging scope so every log line for the request shares it. It logs ONLY identifiers;
/// request bodies, headers, tokens, and PII are never read into the scope.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsWellFormed(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;

        // OnStarting echoes the id even on error/short-circuit paths (e.g. a 401 before the endpoint runs).
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            ctx.Response.Headers[HeaderName] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        }, context);

        var scope = new Dictionary<string, object> { ["CorrelationId"] = correlationId };
        if (tenant.HasTenant)
            scope["TenantId"] = tenant.TenantId;

        using (_logger.BeginScope(scope))
            await _next(context);
    }

    private static bool IsWellFormed(string value) =>
        value.Length is > 0 and <= MaxLength && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Built-in structured (JSON console) logging + the correlation-id middleware — no third-party logger.</summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Replaces the default console providers with the framework JSON console formatter and turns on scope
    /// rendering, so the correlation id / tenant id pushed by <see cref="CorrelationIdMiddleware"/> appear
    /// on every line. Levels still come from the existing "Logging" configuration section.
    /// </summary>
    public static IHostApplicationBuilder AddJsonConsoleLogging(this IHostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
        });
        return builder;
    }

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
