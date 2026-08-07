using BuildingBlocks.Application;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Products.Application.Ports;

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
        { new BadHttpRequestException("invalid JSON body"), StatusCodes.Status400BadRequest },
        { new ArgumentException("bad arg"), StatusCodes.Status400BadRequest },
        // Has no arm of its own: it reaches 400 through the ArgumentException bucket above, which is the
        // whole reason it derives from it (REQ-4.5).
        { new SpDocumentSearchRejectedException(50006, "Invalid CountMode."), StatusCodes.Status400BadRequest },
        { new InvalidOperationException("illegal state"), StatusCodes.Status409Conflict },
        { new ConflictException("duplicate unique key"), StatusCodes.Status409Conflict },
        { new UpstreamUnavailableException("hippodb refused the connection"), StatusCodes.Status503ServiceUnavailable },
        // Our own platform database being unreadable is the OTHER 503 — same wire as the upstream arm,
        // separated only in the log (probe-dependency-failure-mapping REQ-1.1, 4.2).
        { new DependencyUnavailableException("VCentralPay read failed", new Exception("inner")), StatusCodes.Status503ServiceUnavailable },
        // A raw provider exception from a WRITE has no arm on purpose: a write whose outcome is unknown
        // must stay an opaque 500, never a retryable 503 (probe-dependency-failure-mapping REQ-1.5).
        { new FakeDbException("connection reset mid-commit"), StatusCodes.Status500InternalServerError },
        { new Exception("some internal failure"), StatusCodes.Status500InternalServerError },
    };

    private sealed class FakeDbException : System.Data.Common.DbException
    {
        public FakeDbException(string message) : base(message) { }
    }

    /// <summary>The §6 error number rides along for the server log and for tests; the client only ever
    /// sees the handler's fixed 400 detail (REQ-4.5).</summary>
    [Fact]
    public void A_rejected_sp_search_carries_its_error_number()
    {
        var exception = new SpDocumentSearchRejectedException(50007, "Invalid PaymentStatus.");

        Assert.Equal(50007, exception.SpErrorNumber);
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

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
        new UpstreamUnavailableException(LeakProbe),  // upstream fault -> 503 without the SQL/infra text (REQ-4.6)
        // platform DB fault -> 503 whose detail never carries the SQL error text (probe-dependency-failure-mapping REQ-3.1)
        new DependencyUnavailableException(LeakProbe, new Exception(LeakProbe)),
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
