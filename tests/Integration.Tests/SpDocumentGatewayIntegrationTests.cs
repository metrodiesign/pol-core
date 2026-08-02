using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Products.Application.Ports;
using Products.Domain;
using Products.Infrastructure.Sp;

namespace Integration.Tests;

/// <summary>
/// products-sp-gateway task 5 (REQ-5.*, REQ-6.6, REQ-10.2) — the REAL adapter against the simulated
/// upstream. <see cref="SpDocumentContractTests"/> proves what the procedures promise; this file proves that
/// <see cref="SpDocumentGateway"/> collects that promise correctly: both result sets in order, every column
/// resolved by name into the right field, and each failure shape turned into the exception the HTTP boundary
/// expects (50001-50009 -> 400, everything else -> 503, cancellation -> neither).
/// <para>The options are built with <see cref="IntegrationDb.ForCatalog"/>, so the adapter runs as pol_app
/// with EXECUTE and nothing else — the same principal a deployment uses.</para>
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SpDocumentGatewayIntegrationTests
{
    private const string Motor = "Motor";
    private const string NonMotor = "NonMotor";

    /// <summary>One simulated side plus the axis row this file reads field by field. The row is pulled by
    /// its policy number rather than by page position: ORDER BY DocumentNo sorts on the abbreviation
    /// embedded in the number (Thai on Motor, ASCII on Non-Motor), so "the first row of page 1" is not the
    /// row anyone means.</summary>
    private sealed record Side(
        InsuranceType Target,
        string SaleCode,
        long TotalRows,
        string InsuranceTypeWire,
        string PolicyYear,
        string AxisPolicyNumber,
        string AxisDocumentNo,
        string AxisSourceSystem,
        string AxisShowName,
        decimal AxisTotalPremium,
        string? AxisLicensePlate,
        string? AxisPolicyType,
        string AxisReferenceBranch,
        string PaidPolicyNumber);

    private static readonly Side MotorSide = new(
        Target: InsuranceType.Motor,
        SaleCode: "77001",
        TotalRows: 42,
        InsuranceTypeWire: "Motor",
        PolicyYear: "69",
        AxisPolicyNumber: "77001-69900/910001",
        AxisDocumentNo: "69900/กธ/910001",
        AxisSourceSystem: "VMI",
        AxisShowName: "นายสมชาย ใจดีมงคล",
        AxisTotalPremium: 12500.00m,
        AxisLicensePlate: "1กก 1001",
        AxisPolicyType: "90",                       // seeded for VMI only
        AxisReferenceBranch: "900",                  // SaleCode 77001 -> broker 701 -> branch 900
        PaidPolicyNumber: "77001-69900/910007");

    private static readonly Side NonMotorSide = new(
        Target: InsuranceType.NonMotor,
        SaleCode: "77001",
        TotalRows: 40,
        InsuranceTypeWire: "NonMotor",
        PolicyYear: "26",
        AxisPolicyNumber: "77001-26900/000001",
        AxisDocumentNo: "26900/POL/000001",
        AxisSourceSystem: "FIRE",
        AxisShowName: "บริษัท เจริญทรัพย์ พร็อพเพอร์ตี้ จำกัด",
        AxisTotalPremium: 18500.00m,
        AxisLicensePlate: null,                     // §5.2: the Non-Motor procedure returns a constant null
        AxisPolicyType: null,                       // policy-type codes are a Motor concept in this catalogue
        AxisReferenceBranch: "900",                  // SaleCode 77001 -> broker 701 -> branch 900
        PaidPolicyNumber: "77001-26900/000007");

    // ------------------------------------------------------------------ mapping (REQ-5.1, 5.2, 5.3)

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_default_search_maps_the_metadata_row_and_the_page(string key)
    {
        var side = SideOf(key);

        var result = await Gateway().SearchAsync(Request(side), CancellationToken.None);

        // Straight out of result set 1 — nothing here is recomputed on our side (REQ-8.1).
        Assert.Equal(side.TotalRows, result.Page.TotalRows);
        Assert.Equal(2L, result.Page.TotalPages);
        Assert.Equal(1, result.Page.PageNo);
        Assert.Equal(25, result.Page.PageSize);
        Assert.True(result.Page.HasNextPage);
        Assert.False(result.Page.HasPreviousPage);
        Assert.Equal("EXACT", result.Page.CountMode);
        Assert.Equal(6, result.Page.SearchWindowMonths);

        Assert.Equal(25, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(side.InsuranceTypeWire, item.InsuranceType));
        Assert.All(result.Items, item => Assert.Equal("UNPAID", item.PaymentStatus));
        Assert.All(result.Items, item => Assert.Equal(side.SaleCode, item.SaleCode));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Every_column_of_a_row_lands_in_its_own_field(string key)
    {
        var side = SideOf(key);

        // Fetched by policy number so the assertion does not depend on where the row falls in the ordering.
        var result = await Gateway().SearchAsync(
            Request(side) with { PolicyNo = side.AxisPolicyNumber }, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(side.InsuranceTypeWire, item.InsuranceType);
        Assert.Equal(side.AxisSourceSystem, item.SourceSystem);
        Assert.Equal("POLICY", item.DocumentType);
        Assert.Equal(side.AxisDocumentNo, item.DocumentNo);
        Assert.Equal(side.PolicyYear, item.PolicyYear);
        Assert.Equal(side.AxisReferenceBranch, item.ReferenceBranch);
        Assert.Null(item.ReferencePre);                          // seeded for ENDORSEMENT only
        Assert.Equal(side.AxisPolicyNumber[^6..], item.PolicySequenceNo);   // the sequence the number ends on
        Assert.Equal(side.PolicyYear, item.ReferenceYear);
        Assert.Equal(item.PolicySequenceNo, item.ReferenceNo);
        Assert.False(string.IsNullOrWhiteSpace(item.PolicyBranch));
        Assert.Equal(side.AxisPolicyType, item.PolicyType);
        Assert.Equal(side.SaleCode, item.SaleCode);
        Assert.False(string.IsNullOrWhiteSpace(item.SaleFullName));
        Assert.False(string.IsNullOrWhiteSpace(item.BrokerCode));
        Assert.False(string.IsNullOrWhiteSpace(item.BrokerName));
        Assert.Equal(side.AxisPolicyNumber, item.PolicyNumber);
        Assert.Null(item.ApplicationNumber);                     // a POLICY has no application number
        Assert.Null(item.PreviousPolicyNumber);                  // nor a previous policy
        Assert.Null(item.EndorsementNumber);
        Assert.Equal(side.AxisShowName, item.ShowName);
        Assert.Equal(side.AxisTotalPremium, item.TotalPremium);
        // The three components are seeded to reconstruct the total exactly, so a swapped pair of decimal
        // columns would break this even though each value on its own looks plausible.
        Assert.Equal(item.TotalPremium, item.NetPremium + item.Stamp + item.TaxVat);
        Assert.Equal(Math.Round(item.NetPremium!.Value * item.CommissionPercent!.Value / 100, 2),
            item.CommissionAmount);
        Assert.Equal("UNPAID", item.PaymentStatus);
        Assert.Null(item.PaidDate);                              // UNPAID rows carry no paid date
        Assert.Equal(side.AxisLicensePlate, item.LicensePlateNumber);
        // datetime2(0) crosses naively: same wall-clock value, no offset, no conversion (F7).
        Assert.NotNull(item.StartDate);
        Assert.NotNull(item.EndDate);
        Assert.True(item.EndDate > item.StartDate);
        Assert.Equal(DateTimeKind.Unspecified, item.StartDate!.Value.Kind);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_paid_row_carries_its_paid_date(string key)
    {
        var side = SideOf(key);

        var result = await Gateway().SearchAsync(
            Request(side) with { PaymentStatus = "PAID", PolicyNo = side.PaidPolicyNumber },
            CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("PAID", item.PaymentStatus);
        Assert.NotNull(item.PaidDate);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Fast_count_mode_leaves_the_totals_null(string key)
    {
        var side = SideOf(key);

        var result = await Gateway().SearchAsync(
            Request(side) with { CountMode = "FAST" }, CancellationToken.None);

        // DBNull has to become null here, not 0 — a 0 total would tell the client there is nothing to page.
        Assert.Null(result.Page.TotalRows);
        Assert.Null(result.Page.TotalPages);
        Assert.True(result.Page.HasNextPage);
        Assert.Equal(25, result.Items.Count);
    }

    // ------------------------------------------------------------------ @BranchCode from options (REQ-6.6)

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task The_branch_code_is_sent_from_options_and_only_validates(string key)
    {
        var side = SideOf(key);

        // '999' matches no seeded row. The full result set still comes back, which is the procedure
        // validating the branch without filtering on it (REQ-2.11) — and proof the value travelled.
        var result = await Gateway(branchCode: "999").SearchAsync(Request(side), CancellationToken.None);

        Assert.Equal(side.TotalRows, result.Page.TotalRows);
        Assert.Equal(25, result.Items.Count);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_blank_branch_code_in_options_is_rejected_by_the_procedure(string key)
    {
        var side = SideOf(key);

        // Nothing on the request can cause this, so it is the one failure that pins where @BranchCode comes
        // from: a PostConfigure that let the option go blank would 400 every search.
        var rejected = await Assert.ThrowsAsync<SpDocumentSearchRejectedException>(
            () => Gateway(branchCode: "   ").SearchAsync(Request(side), CancellationToken.None));

        Assert.Equal(50004, rejected.SpErrorNumber);
    }

    // ------------------------------------------------------------------ error mapping (REQ-5.4, 5.5, 5.6, M8)

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_procedure_error_becomes_a_rejection_carrying_its_number(string key)
    {
        var side = SideOf(key);

        var rejected = await Assert.ThrowsAsync<SpDocumentSearchRejectedException>(
            () => Gateway().SearchAsync(
                Request(side) with { CountMode = "X" }, CancellationToken.None));

        Assert.Equal(50006, rejected.SpErrorNumber);
        // It reaches the client as a 400 through the handler's ArgumentException bucket.
        Assert.IsAssignableFrom<ArgumentException>(rejected);
    }

    [Fact]
    public async Task A_catalogue_without_the_procedure_is_an_upstream_outage()
    {
        // Points the Motor side at the app database, where dbo.usp_Motor_SearchDocument does not exist —
        // the same class of failure as a wrong catalogue, a dropped procedure or a revoked GRANT. It must
        // be a 503 about the upstream, never a 500 about us.
        var gateway = new SpDocumentGateway(
            Options.Create(new SpDocumentOptions { MotorConnectionString = IntegrationDb.AppConn }),
            NullLogger<SpDocumentGateway>.Instance);

        await Assert.ThrowsAsync<UpstreamUnavailableException>(
            () => gateway.SearchAsync(Request(MotorSide), CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_stays_a_cancellation()
    {
        // M8: mapping a cancelled search to 503 would invent an upstream outage (and page someone) every
        // time a user closes the tab.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Gateway().SearchAsync(Request(MotorSide), cancelled.Token));
    }

    // ------------------------------------------------------------------ helpers

    private static Side SideOf(string key) => key == Motor ? MotorSide : NonMotorSide;

    private static SpDocumentGateway Gateway(string branchCode = "000") => new(
        Options.Create(new SpDocumentOptions
        {
            BranchCode = branchCode,
            MotorConnectionString = IntegrationDb.ForCatalog("hippodb"),
            NonMotorConnectionString = IntegrationDb.ForCatalog("mammothdb"),
        }),
        NullLogger<SpDocumentGateway>.Instance);

    /// <summary>The default search: own sale code, UNPAID, both enum axes ALL, first page, EXACT.</summary>
    private static SpDocumentSearchRequest Request(Side side) => new(
        Target: side.Target,
        SaleCode: side.SaleCode,
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
