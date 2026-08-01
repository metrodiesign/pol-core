using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Products.Application.Ports;
using Products.Domain;
using Products.Infrastructure.Sp;

namespace Hosts.Tests;

/// <summary>A malformed SpDocument connection string is operator configuration, not caller input, so it
/// must take the documented 503 path (<see cref="UpstreamUnavailableException"/>) rather than escape as
/// the <see cref="ArgumentException"/> the HTTP boundary turns into a 400 blaming the caller. The string
/// fails to parse before any network I/O, so this proof runs in the offline suite.</summary>
public sealed class SpDocumentGatewayConfigTests
{
    [Theory]
    [InlineData(InsuranceType.Motor)]
    [InlineData(InsuranceType.NonMotor)]
    public async Task A_malformed_connection_string_is_an_upstream_outage(InsuranceType target)
    {
        var gateway = new SpDocumentGateway(
            Options.Create(new SpDocumentOptions
            {
                MotorConnectionString = "this is not a connection string",
                NonMotorConnectionString = "this is not a connection string",
            }),
            NullLogger<SpDocumentGateway>.Instance);

        var outage = await Assert.ThrowsAsync<UpstreamUnavailableException>(
            () => gateway.SearchAsync(Request(target), CancellationToken.None));

        Assert.IsNotAssignableFrom<ArgumentException>(outage);
    }

    private static SpDocumentSearchRequest Request(InsuranceType target) => new(
        Target: target,
        SaleCode: "77001",
        SearchText: null,
        InsuredName: null,
        CoverageStartFrom: null,
        CoverageStartTo: null,
        CoverageEndFrom: null,
        CoverageEndTo: null,
        PaymentStatus: "UNPAID",
        DocumentType: "ALL",
        ProductGroup: "ALL",
        PolicyNo: null,
        ApplicationNo: null,
        PaidDateFrom: null,
        PaidDateTo: null,
        PageNo: 1,
        PageSize: 25,
        CountMode: "EXACT");
}
