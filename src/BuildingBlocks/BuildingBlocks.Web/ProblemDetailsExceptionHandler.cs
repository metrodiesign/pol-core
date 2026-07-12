using BuildingBlocks.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Web;

/// <summary>
/// The single shared exception -> RFC7807 ProblemDetails handler for the HTTP hosts. Known application
/// exceptions map to a precise status with a FIXED, caller-safe detail; everything else is an opaque 500.
/// No exception message, stack trace, or internal identifier is ever written to the response for the
/// unknown or security buckets — full detail is logged server-side only. This is the only place HTTP
/// status is derived from an exception type, so the contract stays consistent across every endpoint.
/// </summary>
public sealed class ProblemDetailsExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<ProblemDetailsExceptionHandler> _logger;

    public ProblemDetailsExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<ProblemDetailsExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // A cancelled request usually has no live response to write to; let it propagate untouched.
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return false;

        var (status, title, detail) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception mapped to {Status}", status);
        else
            _logger.LogWarning(exception, "Handled exception mapped to {Status}", status);

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }

    // Detail is a FIXED string per bucket — NEVER exception.Message — so merchant ids, PSP charge ids, SQL
    // text, or merchant-binding state cannot leak. MerchantBindingException is an opaque 500 by design.
    private static (int Status, string Title, string? Detail) Map(Exception exception) => exception switch
    {
        NotFoundException =>
            (StatusCodes.Status404NotFound, "Resource not found", null),
        GoneException =>
            (StatusCodes.Status410Gone, "Gone", "This link has expired."),
        ConcurrencyConflictException =>
            (StatusCodes.Status409Conflict, "Conflict", "The resource was modified concurrently; please retry."),
        ConflictException =>
            (StatusCodes.Status409Conflict, "Conflict", "A resource with the same identifier already exists."),
        MerchantBindingException =>
            (StatusCodes.Status500InternalServerError, "An unexpected error occurred", null),
        ArgumentException =>
            (StatusCodes.Status400BadRequest, "Invalid request", null),
        InvalidOperationException =>
            (StatusCodes.Status409Conflict, "The operation is not allowed in the resource's current state", null),
        _ =>
            (StatusCodes.Status500InternalServerError, "An unexpected error occurred", null),
    };
}

/// <summary>Registers the shared ProblemDetails exception handling on a host.</summary>
public static class ProblemDetailsExtensions
{
    public static IServiceCollection AddProblemDetailsHandling(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        return services;
    }
}
