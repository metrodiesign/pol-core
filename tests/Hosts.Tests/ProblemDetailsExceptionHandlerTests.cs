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
        { new MerchantBindingException("no merchant bound"), StatusCodes.Status500InternalServerError },
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
        new Exception(LeakProbe),                 // unknown -> 500
        new MerchantBindingException(LeakProbe),    // security-floor signal -> opaque 500
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
