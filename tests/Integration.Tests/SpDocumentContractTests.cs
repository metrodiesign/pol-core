using System.Data;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// products-sp-gateway task 2 (REQ-2.*, REQ-3.1, REQ-10.1) — the contract that
/// <c>docker/bootstrap/02-external-sim.sql</c> owes every caller, asserted by calling the two simulated
/// procedures directly instead of through the adapter. Everything here is the SP's promise, not ours: the
/// day we point the connection string at the real motordb/centerdb these tests become the acceptance suite
/// for the upstream, and any assertion that fails there is a genuine contract difference to negotiate.
///
/// The connection is pol_app on hippodb/mammothdb (<see cref="IntegrationDb.ForCatalog"/>), which owns
/// EXECUTE and nothing else — so every test also re-proves the GRANT and the ownership chaining that lets
/// the procedure read dbo.Documents on a principal that cannot SELECT from it.
///
/// The seed is deterministic and relative to GETDATE(), so expectations are stable on any run day. Rows are
/// identified by the running number at the tail of DocumentNo (see <see cref="Seqs"/>) — Motor's
/// ENDORSEMENT rows carry an extra un-delimited '1' after it (REQ-1.3), stripped only when the row's own
/// SourceSystem/DocumentType say so. Motor embeds a Thai abbreviation right after the shared
/// PolicyYear+ReferenceBranch prefix (กธ &lt; รย &lt; อท under Thai_CI_AS), so ORDER BY DocumentNo sorts by
/// THAT letter first — never by the running number. Only
/// <see cref="Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter"/> depends on that order;
/// everywhere else the expectation is a set. Non-Motor's abbreviation (POL/APP/END) is ASCII, so its side
/// has no equivalent letter-grouping quirk.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SpDocumentContractTests
{
    private const string Motor = "Motor";
    private const string NonMotor = "NonMotor";

    /// <summary>One simulated upstream and the seed landmarks that make its rules observable. Running
    /// numbers are the DocumentNo tails documented in
    /// .ai/specs/external-sim-documentno-format/HANDOFF.md.</summary>
    // Neither is a real SaleCode: "7700" is a strict prefix of every master code (77001-77006), "7001" is a
    // strict substring (the tail of 77001) — shared by both sides since REQ-1 unified the roster.
    private const string SaleCodePrefixProbe = "7700";
    private const string SaleCodeSubstringProbe = "7001";

    private sealed record Side(
        string Catalog,
        string Procedure,
        string InsuranceType,
        string SaleCode,
        string PolicyYearBranch,
        string GroupA,
        string GroupB,
        string RejectedGroup,
        long TotalRows,
        long TotalPages,
        int LastPageRows,
        string SeqPrefix,
        string[] SeqPrefixHits,
        string[] RenewalDroppedSeqs,
        string RenewalKeptSeq,
        string ApplicationSeq,
        string PolicySeq,
        string LikeMetacharacterSeq,
        string[] PaidSeqs);

    // 42 of hippodb's 200 rows and 40 of mammothdb's 200 fall in the default search (own sale code,
    // UNPAID, inside the window — each side spreads its 200 rows across a 6-agent SaleCode roster, and
    // ShowName is grouped 7/7/7/6/6/7 across that roster so a given ShowName always sells through the
    // same agent, so the default search only sees its one agent's share) — the counts 02-external-sim.sql
    // pins in its own self-check.
    private static readonly Side MotorSide = new(
        Catalog: "hippodb",
        Procedure: "dbo.usp_Motor_SearchDocument",
        InsuranceType: "Motor",
        SaleCode: "77001",
        PolicyYearBranch: "69301",
        GroupA: "CMI",
        GroupB: "VMI",
        RejectedGroup: "FIRE",
        TotalRows: 42,
        TotalPages: 2,
        LastPageRows: 17,
        SeqPrefix: "91",
        SeqPrefixHits: ["910001", "9100002", "910004", "910009"],
        RenewalDroppedSeqs: ["9100005", "910006"],
        RenewalKeptSeq: "910004",
        ApplicationSeq: "910009",
        PolicySeq: "910001",
        LikeMetacharacterSeq: "8000011",
        PaidSeqs: ["910007", "9100008"]);

    private static readonly Side NonMotorSide = new(
        Catalog: "mammothdb",
        Procedure: "dbo.usp_NonMotor_SearchDocument",
        InsuranceType: "NonMotor",
        SaleCode: "77001",
        PolicyYearBranch: "26301",
        GroupA: "FIRE",
        GroupB: "MISC",
        RejectedGroup: "CMI",
        TotalRows: 40,
        TotalPages: 2,
        LastPageRows: 15,
        SeqPrefix: "00000",
        SeqPrefixHits: ["000001", "000002", "000004", "000006", "000009"],
        RenewalDroppedSeqs: ["000005"],
        RenewalKeptSeq: "000004",
        ApplicationSeq: "000006",
        PolicySeq: "000001",
        LikeMetacharacterSeq: "000010",
        PaidSeqs: ["000007", "000008"]);

    private static readonly string[] ExpectedMetadataColumns =
    [
        "TotalRows", "TotalPages", "PageNo", "PageSize",
        "HasNextPage", "HasPreviousPage", "CountMode", "SearchWindowMonths"
    ];

    private static readonly string[] ExpectedItemColumns =
    [
        "InsuranceType", "SourceSystem", "DocumentType", "DocumentNo", "PolicyYear", "ReferenceBranch",
        "ReferencePre", "PolicySequenceNo", "ReferenceYear", "ReferenceNo", "PolicyBranch", "PolicyType",
        "SaleCode", "SaleFullName", "BrokerCode", "BrokerName", "PolicyNumber", "ApplicationNumber",
        "PreviousPolicyNumber", "EndorsementNumber", "StartDate", "EndDate", "ShowName", "NetPremium",
        "Stamp", "TaxVat", "TotalPremium", "CommissionPercent", "CommissionAmount", "PaidDate",
        "LicensePlateNumber", "PaymentStatus"
    ];

    // ---------------------------------------------------------------- normalization + shape

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Omitting_every_optional_parameter_applies_the_documented_defaults(string key)
    {
        var side = SideOf(key);

        var result = await SearchAsync(side);

        // UNPAID | ALL | ALL | EXACT | page 1 | size 25 (REQ-2.2). The two PAID documents on each side are
        // absent, which is what makes "@PaymentStatus defaults to UNPAID" observable rather than assumed.
        Assert.Equal(side.TotalRows, Get<long>(result.Metadata, "TotalRows"));
        Assert.Equal(side.TotalPages, Get<long>(result.Metadata, "TotalPages"));
        Assert.Equal(1, Get<int>(result.Metadata, "PageNo"));
        Assert.Equal(25, Get<int>(result.Metadata, "PageSize"));
        Assert.True(Get<bool>(result.Metadata, "HasNextPage"));
        Assert.False(Get<bool>(result.Metadata, "HasPreviousPage"));
        Assert.Equal("EXACT", Get<string>(result.Metadata, "CountMode"));
        Assert.Equal(6, Get<int>(result.Metadata, "SearchWindowMonths"));   // REQ-2.10

        Assert.Equal(25, result.Items.Count);
        Assert.All(result.Items, row => Assert.Equal("UNPAID", row["PaymentStatus"]));
        // InsuranceType is a per-side constant, not a stored column (F6).
        Assert.All(result.Items, row => Assert.Equal(side.InsuranceType, row["InsuranceType"]));

        // ALL on both axes leaves the WHOLE result unfiltered — checked across every row, not just page 1:
        // REQ-1.1 dropped the SaleCode prefix, so DocumentNo now sorts by its embedded abbreviation first
        // (REQ-2), and a single-SaleCode page can legitimately be one DocumentType by chance even when
        // nothing is filtered (e.g. Motor's page 1 is all POLICY, since POLICY/RENEWAL's shared 'กธ'
        // abbreviation sorts before APPLICATION's 'รย' and ENDORSEMENT's 'อท').
        var allItems = (await AllPagesAsync(side)).SelectMany(p => p.Items).ToArray();
        Assert.True(allItems.Select(row => row["DocumentType"]).Distinct().Count() > 1,
            "@DocumentType defaulting to ALL should leave more than one document type across the full result");
        Assert.True(allItems.Select(row => row["SourceSystem"]).Distinct().Count() > 1,
            "@ProductGroup defaulting to ALL should leave more than one source system across the full result");
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Two_result_sets_arrive_in_a_fixed_order_with_the_documented_columns(string key)
    {
        // SearchAsync itself asserts the shape: result set 1 holds exactly one row, result set 2 follows it,
        // and there is no third — the ordering the adapter's NextResultAsync() walk depends on (REQ-5.3).
        var result = await SearchAsync(SideOf(key));

        Assert.Equal(ExpectedMetadataColumns, result.MetadataColumns);   // §5.1
        Assert.Equal(ExpectedItemColumns, result.ItemColumns);           // §5.2
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Page_size_is_capped_at_twenty_five(string key)
    {
        var side = SideOf(key);

        var result = await SearchAsync(side, ("@PageSize", 100));

        Assert.Equal(25, Get<int>(result.Metadata, "PageSize"));
        Assert.Equal(25, result.Items.Count);
        Assert.Equal(side.TotalRows, Get<long>(result.Metadata, "TotalRows"));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Page_number_and_page_size_below_one_normalise_to_the_first_full_page(string key)
    {
        var result = await SearchAsync(SideOf(key), ("@PageNo", -5), ("@PageSize", 0));

        Assert.Equal(1, Get<int>(result.Metadata, "PageNo"));
        Assert.Equal(25, Get<int>(result.Metadata, "PageSize"));
        Assert.Equal(25, result.Items.Count);
    }

    // ---------------------------------------------------------------- validation (§6)

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Every_documented_rejection_throws_its_own_error_number(string key)
    {
        var side = SideOf(key);

        Assert.Equal(50004, await RejectionAsync(side, ("@BranchCode", null)));
        Assert.Equal(50004, await RejectionAsync(side, ("@BranchCode", "   ")));   // trimmed, then blank
        Assert.Equal(50005, await RejectionAsync(side, ("@SaleCode", null)));
        Assert.Equal(50005, await RejectionAsync(side, ("@SaleCode", "   ")));
        Assert.Equal(50001, await RejectionAsync(side, ("@DocumentType", "CANCELLATION")));
        Assert.Equal(50002, await RejectionAsync(side, ("@ProductGroup", side.RejectedGroup)));
        Assert.Equal(50007, await RejectionAsync(side, ("@PaymentStatus", "PENDING")));
        Assert.Equal(50006, await RejectionAsync(side, ("@CountMode", "APPROX")));
        Assert.Equal(50003, await RejectionAsync(side,
            ("@PaidDateFrom", new DateTime(2026, 3, 1)), ("@PaidDateTo", new DateTime(2026, 2, 1))));
        Assert.Equal(50008, await RejectionAsync(side,
            ("@CoverageStartFrom", new DateTime(2026, 3, 1)), ("@CoverageStartTo", new DateTime(2026, 2, 1))));
        Assert.Equal(50009, await RejectionAsync(side,
            ("@CoverageEndFrom", new DateTime(2026, 3, 1)), ("@CoverageEndTo", new DateTime(2026, 2, 1))));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Several_invalid_inputs_are_reported_in_the_fixed_validation_order(string key)
    {
        var side = SideOf(key);

        // Identity/scope first, then the enums in parameter order, then the range inversions (design M2).
        // Each step drops the winner of the previous one, so the next number in the order must surface.
        (string, object?) badDocumentType = ("@DocumentType", "CANCELLATION");
        (string, object?) badGroup = ("@ProductGroup", side.RejectedGroup);
        (string, object?) badStatus = ("@PaymentStatus", "PENDING");
        (string, object?) badCountMode = ("@CountMode", "APPROX");
        (string, object?) invertedPaid =
            ("@PaidDateFrom", new DateTime(2026, 3, 1));
        (string, object?) invertedPaidTo = ("@PaidDateTo", new DateTime(2026, 2, 1));
        (string, object?) invertedStart =
            ("@CoverageStartFrom", new DateTime(2026, 3, 1));
        (string, object?) invertedStartTo = ("@CoverageStartTo", new DateTime(2026, 2, 1));
        (string, object?) invertedEnd = ("@CoverageEndFrom", new DateTime(2026, 3, 1));
        (string, object?) invertedEndTo = ("@CoverageEndTo", new DateTime(2026, 2, 1));

        Assert.Equal(50004, await RejectionAsync(side, ("@BranchCode", "  "), ("@SaleCode", "  "),
            badDocumentType, badGroup, badStatus, badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50005, await RejectionAsync(side, ("@SaleCode", "  "),
            badDocumentType, badGroup, badStatus, badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50001, await RejectionAsync(side,
            badDocumentType, badGroup, badStatus, badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50002, await RejectionAsync(side,
            badGroup, badStatus, badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50007, await RejectionAsync(side,
            badStatus, badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50006, await RejectionAsync(side,
            badCountMode,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50003, await RejectionAsync(side,
            invertedPaid, invertedPaidTo, invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50008, await RejectionAsync(side,
            invertedStart, invertedStartTo, invertedEnd, invertedEndTo));
        Assert.Equal(50009, await RejectionAsync(side, invertedEnd, invertedEndTo));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task An_invalid_payment_status_is_rejected_even_when_a_paid_date_would_override_it(string key)
    {
        // M1: forcing PAID before validating would swallow 50007 for every caller who also passes a paid-date
        // bound — the SP validates the raw value first and only then rewrites it.
        var number = await RejectionAsync(SideOf(key),
            ("@PaymentStatus", "XXX"), ("@PaidDateFrom", new DateTime(2000, 1, 1)));

        Assert.Equal(50007, number);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Enum_parameters_are_compared_case_sensitively(string key)
    {
        // The SP compares under COLLATE Latin1_General_BIN2 (M5), so it can never be laxer than the HTTP
        // boundary that fronts it — a lower-cased value is a rejection, not a silent normalisation.
        var side = SideOf(key);

        Assert.Equal(50007, await RejectionAsync(side, ("@PaymentStatus", "unpaid")));
        Assert.Equal(50006, await RejectionAsync(side, ("@CountMode", "exact")));
        Assert.Equal(50001, await RejectionAsync(side, ("@DocumentType", "policy")));
        Assert.Equal(50002, await RejectionAsync(side, ("@ProductGroup", side.GroupA.ToLowerInvariant())));
    }

    // ---------------------------------------------------------------- counting + paging

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Fast_count_mode_drops_the_totals_but_keeps_the_page_and_the_next_page_flag(string key)
    {
        var side = SideOf(key);

        var first = await SearchAsync(side, ("@CountMode", "FAST"));

        Assert.Null(first.Metadata["TotalRows"]);     // arrives as DBNull on the wire (REQ-2.9)
        Assert.Null(first.Metadata["TotalPages"]);
        Assert.Equal("FAST", Get<string>(first.Metadata, "CountMode"));
        Assert.True(Get<bool>(first.Metadata, "HasNextPage"));
        Assert.Equal(25, first.Items.Count);

        // The extra row fetched to compute HasNextPage must never leak into the result set: a small page
        // makes an off-by-one there visible immediately.
        var small = await SearchAsync(side, ("@CountMode", "FAST"), ("@PageSize", 3));

        Assert.Equal(3, small.Items.Count);
        Assert.True(Get<bool>(small.Metadata, "HasNextPage"));

        var last = await SearchAsync(side, ("@CountMode", "FAST"), ("@PageNo", (int)side.TotalPages));

        Assert.Equal(side.LastPageRows, last.Items.Count);
        Assert.False(Get<bool>(last.Metadata, "HasNextPage"));
        Assert.True(Get<bool>(last.Metadata, "HasPreviousPage"));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_page_past_the_end_still_returns_metadata_and_an_empty_item_set(string key)
    {
        var side = SideOf(key);

        var result = await SearchAsync(side, ("@PageNo", 99));

        Assert.Equal(side.TotalRows, Get<long>(result.Metadata, "TotalRows"));
        Assert.Equal(side.TotalPages, Get<long>(result.Metadata, "TotalPages"));
        Assert.Equal(99, Get<int>(result.Metadata, "PageNo"));
        Assert.Equal(25, Get<int>(result.Metadata, "PageSize"));
        Assert.False(Get<bool>(result.Metadata, "HasNextPage"));
        Assert.True(Get<bool>(result.Metadata, "HasPreviousPage"));
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task The_pages_cut_one_document_number_ordered_list(string key)
    {
        var side = SideOf(key);

        var pages = await AllPagesAsync(side);

        Assert.Equal(side.LastPageRows, pages[^1].Items.Count);
        Assert.False(Get<bool>(pages[^1].Metadata, "HasNextPage"));
        Assert.All(pages[..^1], p => Assert.True(Get<bool>(p.Metadata, "HasNextPage")));

        var walked = pages.SelectMany(DocumentNumbers).ToArray();

        Assert.Equal(side.TotalRows, walked.Distinct().Count());   // no row repeated or skipped at any seam
        Assert.Equal(walked.OrderBy(no => no, StringComparer.Ordinal), walked);
    }

    // ---------------------------------------------------------------- predicate (REQ-2.12)

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Sale_code_does_not_match_by_prefix_or_substring(string key)
    {
        var side = SideOf(key);

        // Neither probe is a real SaleCode (REQ-5.4 — no landmark row needed): a prefix or substring match
        // would return rows, an exact match returns none.
        Assert.Empty((await SearchAsync(side, ("@SaleCode", SaleCodePrefixProbe))).Items);
        Assert.Empty((await SearchAsync(side, ("@SaleCode", SaleCodeSubstringProbe))).Items);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Branch_code_is_validated_but_never_filters(string key)
    {
        // REQ-2.11: §2 makes @BranchCode required but never defines filter semantics — dbo.Documents has no
        // column to match it against, so any value, including one that matches no row at all, still returns
        // everything.
        var side = SideOf(key);

        foreach (var branch in new[] { "100", "400", "999" })
        {
            var result = await SearchAsync(side, ("@BranchCode", branch));

            Assert.Equal(side.TotalRows, Get<long>(result.Metadata, "TotalRows"));
            Assert.Equal(25, result.Items.Count);
        }
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Document_type_filters_exactly(string key)
    {
        var side = SideOf(key);

        var applications = await SearchAsync(side, ("@DocumentType", "APPLICATION"));

        Assert.Equal(new[] { side.ApplicationSeq }, Seqs(applications));
        Assert.All(applications.Items, row => Assert.Equal("APPLICATION", row["DocumentType"]));

        var renewals = await SearchAsync(side, ("@DocumentType", "RENEWAL"));

        Assert.Equal(new[] { side.RenewalKeptSeq }, Seqs(renewals));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Product_group_filters_on_the_source_system(string key)
    {
        var side = SideOf(key);

        var groupA = await SearchAsync(side, ("@ProductGroup", side.GroupA));
        var groupB = await SearchAsync(side, ("@ProductGroup", side.GroupB));

        Assert.All(groupA.Items, row => Assert.Equal(side.GroupA, row["SourceSystem"]));
        Assert.All(groupB.Items, row => Assert.Equal(side.GroupB, row["SourceSystem"]));
        // The two groups partition the default result exactly — nothing double-counted, nothing lost.
        Assert.Equal(side.TotalRows,
            Get<long>(groupA.Metadata, "TotalRows") + Get<long>(groupB.Metadata, "TotalRows"));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Policy_number_and_application_number_match_exactly(string key)
    {
        var side = SideOf(key);
        var policyNumber = $"{side.SaleCode}-{side.PolicyYearBranch}/{side.PolicySeq}";
        var applicationNumber = $"{side.SaleCode}-{side.PolicyYearBranch}/{side.ApplicationSeq}";

        Assert.Equal(new[] { side.PolicySeq }, Seqs(await SearchAsync(side, ("@PolicyNo", policyNumber))));
        Assert.Empty((await SearchAsync(side, ("@PolicyNo", policyNumber[..^1]))).Items);

        Assert.Equal(new[] { side.ApplicationSeq },
            Seqs(await SearchAsync(side, ("@ApplicationNo", applicationNumber))));
        Assert.Empty((await SearchAsync(side, ("@ApplicationNo", applicationNumber[..^1]))).Items);
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Coverage_bounds_are_inclusive_on_both_ends(string key)
    {
        var side = SideOf(key);
        var today = DateTime.Today;

        // The landmark row starts exactly 30 days ago and ends exactly 335 days out; collapsing a bound pair
        // onto that day must keep it. An exclusive comparison — or a date/datetime2 truncation bug on the
        // upper bound — drops it to zero rows.
        var byStart = await SearchAsync(side,
            ("@CoverageStartFrom", today.AddDays(-30)), ("@CoverageStartTo", today.AddDays(-30)));
        var byEnd = await SearchAsync(side,
            ("@CoverageEndFrom", today.AddDays(335)), ("@CoverageEndTo", today.AddDays(335)));

        Assert.Equal(new[] { side.PolicySeq }, Seqs(byStart));
        Assert.Equal(new[] { side.PolicySeq }, Seqs(byEnd));
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task A_paid_date_bound_forces_the_result_to_paid_documents(string key)
    {
        // REQ-2.3: no @PaymentStatus is passed, so the default UNPAID applies — and is then overridden.
        var side = SideOf(key);

        var result = await SearchAsync(side, ("@PaidDateFrom", new DateTime(2000, 1, 1)));

        Assert.Equal(side.PaidSeqs.Order(), Seqs(result).Order());
        Assert.All(result.Items, row =>
        {
            Assert.Equal("PAID", row["PaymentStatus"]);
            Assert.NotNull(row["PaidDate"]);
        });
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task Insured_name_escapes_like_metacharacters(string key)
    {
        // MINOR-5: one row per side carries a literal '%' and '_' in ShowName. Unescaped, '%' would match
        // every row on the page and '_' every non-empty name — matching exactly one row is the whole proof.
        var side = SideOf(key);

        foreach (var needle in new[] { "%", "_", "100%" })
        {
            var result = await SearchAsync(side, ("@InsuredName", needle));

            Assert.Equal(new[] { side.LikeMetacharacterSeq }, Seqs(result));
        }
    }

    [Theory]
    [InlineData(Motor)]
    [InlineData(NonMotor)]
    public async Task The_search_window_is_evaluated_per_row_when_the_document_type_is_ALL(string key)
    {
        // M3 — the reason the window cannot be chosen from @DocumentType at request level: 'ALL' mixes
        // RENEWAL and non-RENEWAL rows, and they obey different rules.
        //   Motor    keeps 910004 (StartDate 335 days back, EndDate +30) and drops 9100005 (EndDate +100) and
        //            910006 (EndDate -10) — so RENEWAL is judged on EndDate, inside [today, today+2 months).
        //   NonMotor keeps 000004 (StartDate -20, EndDate +345 — Motor's rule would have dropped it) and
        //            drops 000005 (StartDate -245, EndDate +30 — Motor's rule would have kept it), so this
        //            side judges every type on StartDate alone.
        // Every dropped row matches the search text, so their absence is the window and nothing else.
        var side = SideOf(key);

        var result = await SearchAsync(side, ("@SearchText", side.SeqPrefix));

        Assert.Equal(side.SeqPrefixHits.Order(), Seqs(result).Order());
        Assert.Contains(side.RenewalKeptSeq, Seqs(result));
        Assert.DoesNotContain(Seqs(result), seq => side.RenewalDroppedSeqs.Contains(seq));
    }

    // ---------------------------------------------------------------- side-specific rules

    [Fact]
    public async Task Motor_smart_search_includes_the_licence_plate()
    {
        // §3: the plate is a Motor search column. mammothdb seeds '8ฮฮ 8888' so the Non-Motor test below can
        // prove the same text is invisible there.
        var exact = await SearchAsync(MotorSide, ("@SearchText", "9ฮฮ 9999"));
        var partial = await SearchAsync(MotorSide, ("@SearchText", "9ฮฮ"));

        Assert.Equal(new[] { "800014" }, Seqs(exact));
        Assert.Equal(new[] { "800014" }, Seqs(partial));
        Assert.Equal("9ฮฮ 9999", exact.Items[0]["LicensePlateNumber"]);
    }

    [Fact]
    public async Task NonMotor_neither_searches_nor_returns_the_stored_licence_plate()
    {
        // §4 / §5.2: the column exists in mammothdb and one row has a value, so a leak here would be visible.
        var byPlate = await SearchAsync(NonMotorSide, ("@SearchText", "8ฮฮ 8888"));
        var thatRow = await SearchAsync(NonMotorSide, ("@InsuredName", "100%"));
        var page = await SearchAsync(NonMotorSide);

        Assert.Empty(byPlate.Items);
        Assert.Equal(new[] { NonMotorSide.LikeMetacharacterSeq }, Seqs(thatRow));
        Assert.Null(thatRow.Items[0]["LicensePlateNumber"]);
        Assert.All(page.Items, row => Assert.Null(row["LicensePlateNumber"]));
    }

    [Fact]
    public async Task Motor_coverage_start_window_includes_the_row_sitting_exactly_six_months_back()
    {
        // REQ-2.5 boundary: 8000013 starts on DATEADD(month, -6, today) to the day. It is inside the default
        // page, and asking for exactly that day returns it — an exclusive window would lose it silently.
        var pinned = await SearchAsync(MotorSide,
            ("@CoverageStartFrom", DateTime.Today.AddMonths(-6)),
            ("@CoverageStartTo", DateTime.Today.AddMonths(-6)));

        Assert.Equal(new[] { "8000013" }, Seqs(pinned));
        Assert.Contains("8000013", Seqs(await SearchAsync(MotorSide)));
    }

    [Fact]
    public async Task Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter()
    {
        // ORDER BY DocumentNo under Thai_CI_AS sorts on the abbreviation embedded right after the shared
        // PolicyYear+ReferenceBranch prefix (กธ < รย < อท): every ENDORSEMENT row — the 2 axis rows plus
        // roughly a third of the generated rows (REQ-2 fixed the old bug where generated rows always showed
        // 'กธ' regardless of DocumentType) — carries 'อท', so every one of them must sort after every row
        // that doesn't, whatever the numeric running number says.
        var walked = (await AllPagesAsync(MotorSide)).SelectMany(DocumentNumbers).ToArray();

        var endorsementPositions = walked
            .Select((documentNo, position) => (documentNo, position))
            .Where(x => x.documentNo.Contains("อท", StringComparison.Ordinal))
            .Select(x => x.position)
            .ToArray();
        var otherPositions = walked
            .Select((documentNo, position) => (documentNo, position))
            .Where(x => !x.documentNo.Contains("อท", StringComparison.Ordinal))
            .Select(x => x.position)
            .ToArray();

        Assert.NotEmpty(endorsementPositions);
        Assert.NotEmpty(otherPositions);
        Assert.True(endorsementPositions.Min() > otherPositions.Max(),
            "every 'อท' (ENDORSEMENT) DocumentNo must sort after every non-'อท' DocumentNo");
    }

    // ---------------------------------------------------------------- plumbing

    private static Side SideOf(string key) => key == Motor ? MotorSide : NonMotorSide;

    private sealed record SpResult(
        IReadOnlyDictionary<string, object?> Metadata,
        IReadOnlyList<string> MetadataColumns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Items,
        IReadOnlyList<string> ItemColumns);

    /// <summary>Executes the side's procedure with the two required parameters filled in, so a test only
    /// states the parameters it is actually about. DBNull is mapped to null on the way out — the adapter
    /// reads the same values through IsDBNull, and null reads better in an assertion.</summary>
    private static async Task<SpResult> SearchAsync(Side side, params (string Name, object? Value)[] arguments)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["@BranchCode"] = "100",
            ["@SaleCode"] = side.SaleCode,
        };
        foreach (var (name, value) in arguments) parameters[name] = value;

        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.ForCatalog(side.Catalog));
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = side.Procedure;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        var metadataColumns = ColumnsOf(reader);
        Assert.True(await reader.ReadAsync(), "result set 1 must contain the pagination metadata row");
        var metadata = RowOf(reader, metadataColumns);
        Assert.False(await reader.ReadAsync(), "result set 1 must contain exactly one row");

        Assert.True(await reader.NextResultAsync(), "result set 2 (the documents) must follow the metadata");
        var itemColumns = ColumnsOf(reader);
        var items = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync()) items.Add(RowOf(reader, itemColumns));

        Assert.False(await reader.NextResultAsync(), "the procedure must return exactly two result sets");

        return new SpResult(metadata, metadataColumns, items, itemColumns);
    }

    /// <summary>Walks every page of the side's default search, in order — the full-page-walk shared by
    /// <see cref="The_pages_cut_one_document_number_ordered_list"/> and
    /// <see cref="Motor_endorsement_rows_sort_after_every_other_row_by_thai_letter"/>.</summary>
    private static async Task<List<SpResult>> AllPagesAsync(Side side)
    {
        var pages = new List<SpResult>();
        for (var pageNo = 1; pageNo <= side.TotalPages; pageNo++)
            pages.Add(await SearchAsync(side, ("@PageNo", pageNo)));
        return pages;
    }

    private static async Task<int> RejectionAsync(Side side, params (string Name, object? Value)[] arguments)
    {
        var thrown = await Assert.ThrowsAsync<SqlException>(() => SearchAsync(side, arguments));
        return thrown.Number;
    }

    private static string[] ColumnsOf(SqlDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();

    private static Dictionary<string, object?> RowOf(SqlDataReader reader, IReadOnlyList<string> columns)
    {
        var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
            row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static T Get<T>(IReadOnlyDictionary<string, object?> row, string column) => (T)row[column]!;

    private static string[] DocumentNumbers(SpResult result) =>
        result.Items.Select(row => Get<string>(row, "DocumentNo")).ToArray();

    /// <summary>The running number at the tail of a DocumentNo ('69301/กธ/910001' -> '910001'). Motor
    /// ENDORSEMENT rows carry an extra un-delimited '1' after it (REQ-1.3); stripping that digit is branched
    /// on the row's own SourceSystem/DocumentType, never on string length — a 7-char tail is ambiguous on
    /// its own (a plain CMI running number and a VMI running number + flag look identical).</summary>
    private static string[] Seqs(SpResult result) =>
        result.Items.Select(row =>
        {
            var tail = Get<string>(row, "DocumentNo");
            tail = tail[(tail.LastIndexOf('/') + 1)..];
            var isMotorEndorsement = Get<string>(row, "SourceSystem") is "CMI" or "VMI"
                && Get<string>(row, "DocumentType") == "ENDORSEMENT";
            return isMotorEndorsement ? tail[..^1] : tail;
        }).ToArray();
}
