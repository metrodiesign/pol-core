using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Products.Application.Ports;
using Products.Domain;
using Products.Infrastructure.Sp;

namespace Products.Tests;

/// <summary>
/// products-external-source-of-truth REQ-4.11 — a sale code that <c>@SaleCode</c> cannot carry unchanged is
/// refused instead of bound. The parameter is declared <c>varchar(20)</c> and the gateway sizes it to match, so
/// SqlClient would truncate a longer value and mangle a non-ASCII one WITHOUT raising anything: the search would
/// simply run under a different party's code and return their documents. Registration already refuses these
/// shapes (REQ-4.10), so reaching this guard means a bad value got into the database — our bug, which is why it
/// is a <see cref="SaleCodeBindingException"/> (an opaque 500) and not a 400.
/// <para>The guard runs before the connection string is even looked at, so these cases never touch a network:
/// an unconfigured gateway would otherwise answer <c>UpstreamUnavailableException</c>, and asserting the
/// binding exception instead is what proves the check comes first.</para>
/// </summary>
public sealed class SpDocumentGatewaySaleCodeBindingTests
{
    private static SpDocumentGateway Unconfigured() =>
        new(Options.Create(new SpDocumentOptions()), NullLogger<SpDocumentGateway>.Instance);

    private static SpDocumentSearchRequest RequestWith(string saleCode) => new(
        Target: InsuranceType.NonMotor, SaleCode: saleCode, SearchText: null, InsuredName: null,
        CoverageStartFrom: null, CoverageStartTo: null, CoverageEndFrom: null, CoverageEndTo: null,
        PaymentStatus: "ALL", DocumentType: "ALL", ProductGroup: "ALL", PolicyNo: null, ApplicationNo: null,
        PaidDateFrom: null, PaidDateTo: null, PageNo: 1, PageSize: 25, CountMode: "FAST");

    [Theory]
    [InlineData("123456789012345678901")] // 21 characters: SqlParameter.Size would silently keep the first 20
    [InlineData("รหัสผู้ขาย")]                  // Thai: varchar cannot represent it, so it would arrive mangled
    public async Task A_sale_code_the_parameter_cannot_carry_is_refused_before_it_is_bound(string saleCode) =>
        await Assert.ThrowsAsync<SaleCodeBindingException>(
            () => Unconfigured().SearchAsync(RequestWith(saleCode), CancellationToken.None));

    // The counterpart: a code the parameter carries verbatim gets past the guard. Proven by the failure that
    // follows it — an unconfigured gateway answering "upstream unavailable" means binding was reached.
    [Fact]
    public async Task A_20_character_ascii_sale_code_passes_the_guard() =>
        await Assert.ThrowsAsync<UpstreamUnavailableException>(
            () => Unconfigured().SearchAsync(RequestWith("12345678901234567890"), CancellationToken.None));
}
