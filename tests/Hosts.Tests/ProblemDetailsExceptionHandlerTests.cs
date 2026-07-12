using BuildingBlocks.Application;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hosts.Tests;

/// <summary>
/// The shared exception -> ProblemDetails handler is the ONE place HTTP status is derived from an
/// exception type, so these are the regression guards for that contract: each known exception maps to its
/// status, and the unknown / security buckets never leak the exception message into the response body.
/// </summary>
public sealed class ProblemDetailsExceptionHandlerTests
{
    public static TheoryData<Exception, int> Mappings() => new()
    {
        { new NotFoundException("PaymentSession 123 not found."), StatusCodes.Status404NotFound },
        { new ConcurrencyConflictException("rowversion clash"), StatusCodes.Status409Conflict },
        { new TenantBindingException("no tenant bound"), StatusCodes.Status500InternalServerError },
        { new ArgumentException("bad arg"), StatusCodes.Status400BadRequest },
        { new InvalidOperationException("illegal state"), StatusCodes.Status409Conflict },
        { new Exception("some internal failure"), StatusCodes.Status500InternalServerError },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task Maps_each_exception_to_its_status(Exception exception, int expectedStatus)
    {
        var (handler, context) = Build();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }

    public static TheoryData<Exception> OpaqueBuckets() => new()
    {
        new Exception(LeakProbe),                                  // unknown -> 500
        new TenantBindingException(LeakProbe),                    // security-floor signal -> opaque 500
        new ConflictException(LeakProbe),                        // 409, no SafeDetail -> generic detail, message stays off the wire
        new ConflictException(LeakProbe, safeDetail: null),     // 409, explicit null SafeDetail -> same
        new ConcurrencyConflictException(LeakProbe),           // 409, no SafeDetail -> generic detail, message stays off the wire
    };

    private const string LeakProbe = "do-not-leak-internal-detail-xyz";

    [Theory]
    [MemberData(nameof(OpaqueBuckets))]
    public async Task Does_not_leak_the_exception_message_for_opaque_buckets(Exception exception)
    {
        var (handler, context) = Build();

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain(LeakProbe, body);
        Assert.DoesNotContain("StackTrace", body);
    }

    [Fact]
    public async Task A_conflict_with_a_safe_detail_surfaces_it_without_leaking_the_message()
    {
        var (handler, context) = Build();
        // Message carries an internal probe (as if it interpolated an email/id); SafeDetail is the vetted wire string.
        var exception = new ConflictException(LeakProbe, safeDetail: "The super_admin role cannot be deactivated.");

        await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("The super_admin role cannot be deactivated.", body);
        Assert.DoesNotContain(LeakProbe, body);
    }

    [Fact]
    public async Task A_conflict_without_a_safe_detail_falls_back_to_the_generic_detail()
    {
        var (handler, context) = Build();

        await handler.TryHandleAsync(context, new ConflictException(LeakProbe), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("A resource with the same identifier already exists.", body);
        Assert.DoesNotContain(LeakProbe, body);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_handled()
    {
        var (handler, context) = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), cts.Token);

        Assert.False(handled);
    }

    private static (ProblemDetailsExceptionHandler Handler, DefaultHttpContext Context) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Accept = "application/json";
        context.Response.Body = new MemoryStream();

        var handler = new ProblemDetailsExceptionHandler(
            provider.GetRequiredService<IProblemDetailsService>(),
            NullLogger<ProblemDetailsExceptionHandler>.Instance);

        return (handler, context);
    }
}
