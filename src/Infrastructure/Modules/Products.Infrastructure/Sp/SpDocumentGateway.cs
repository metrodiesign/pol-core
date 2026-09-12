using System.Data;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Infrastructure.Sp;

/// <summary>
/// The real VCentralPay document-search adapter: ADO.NET over
/// <c>usp_Motor_SearchDocument</c> / <c>usp_NonMotor_SearchDocument</c>. It is production code today even
/// though it currently talks to the simulated hippodb/mammothdb — the procedures, their parameters and
/// their two result sets are the upstream's contract, so cutover changes a connection string, not this file.
/// <para>EF is not used here on purpose: the procedures return TWO result sets from databases that are not
/// in any DbContext's model. The reader therefore walks them in the fixed order the contract promises —
/// one metadata row, <c>NextResult</c>, then the documents — and resolves every column by name through
/// <c>GetOrdinal</c> so a reordered SELECT upstream cannot silently shift values into the wrong fields.</para>
/// <para>The wire types (<see cref="SpDocumentItem"/> and friends) are left exactly as the upstream states
/// them, all-nullable and enum-free; deciding which rows are usable belongs to the mapper, not here.</para>
/// </summary>
public sealed class SpDocumentGateway(IOptions<SpDocumentOptions> options, ILogger<SpDocumentGateway> logger)
    : ISpDocumentGateway
{
    private const string MotorProcedure = "dbo.usp_Motor_SearchDocument";
    private const string NonMotorProcedure = "dbo.usp_NonMotor_SearchDocument";

    private readonly SpDocumentOptions _options = options.Value;

    public async Task<SpDocumentSearchResult> SearchAsync(
        SpDocumentSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        VerifySaleCodeBindsIntact(request.SaleCode);

        var motor = request.Target is InsuranceType.Motor;
        var connectionString = motor ? _options.MotorConnectionString : _options.NonMotorConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The connection string comes from config only now (no PostConfigure fallback) — unset is a
            // real, reachable state, not misconfiguration-only noise. This guard IS the REQ-5.7/AC-3
            // contract: the host still boots without SpDocument:* set, but a search request here gets
            // 503. Do not remove it — without it a null connection string reaches SqlConnection and
            // throws 500 instead.
            logger.LogError("SpDocument: no connection string configured for {Target}.", request.Target);
            throw new UpstreamUnavailableException($"No SpDocument connection string is configured for {request.Target}.");
        }

        try
        {
            await using var connection = CreateConnection(connectionString, request.Target);
            await using var command = new SqlCommand(motor ? MotorProcedure : NonMotorProcedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = _options.CommandTimeoutSeconds,
            };
            AddParameters(command, request);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            // Result set 1 is the §5.1 metadata and always has exactly one row. No row means we are not
            // talking to the contract we think we are — treat it as an outage, never as an empty page
            // (an empty page has a metadata row saying so).
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new UpstreamUnavailableException("Document search returned no pagination metadata row.");

            var page = ReadMetadata(reader);

            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                throw new UpstreamUnavailableException("Document search returned no document result set.");

            var items = new List<SpDocumentItem>(page.PageSize);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                items.Add(ReadItem(reader));

            return new SpDocumentSearchResult(page, items);
        }
        catch (SqlException ex)
        {
            // A cancelled command surfaces as a SqlException too; mapping that to 503 would fake an
            // upstream outage (and page someone) every time a user closes the tab.
            cancellationToken.ThrowIfCancellationRequested();

            if (ex.Number is >= 50001 and <= 50009)
                // The procedure refused the input (§6). That is the caller's fault, not the upstream's:
                // it becomes a 400 through the ArgumentException bucket in ProblemDetailsExceptionHandler.
                throw new SpDocumentSearchRejectedException(ex.Number, $"Document search rejected by the upstream procedure (error {ex.Number}).");

            logger.LogError(ex,
                "SpDocument: {Procedure} failed for {Target} (SQL error {Number}, state {State}, class {Class}).",
                motor ? MotorProcedure : NonMotorProcedure, request.Target, ex.Number, ex.State, ex.Class);
            throw new UpstreamUnavailableException("Document search failed against the upstream database.", ex);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidCastException)
        {
            // GetOrdinal not finding a column, or a column changing type: the upstream contract drifted.
            // 503 (with the detail logged) is honest about that; a 500 would blame us for their change.
            logger.LogError(ex, "SpDocument: result-set contract drift reading {Target} documents.", request.Target);
            throw new UpstreamUnavailableException("Document search returned an unexpected result-set shape.", ex);
        }
    }

    /// <summary><c>@SearchText</c> is declared <c>nvarchar(100)</c>; a longer value would be truncated silently.</summary>
    private const int SearchTextMaxLength = 100;

    /// <summary>The contract width of a document number on the wire and in every column (REQ-2.1).</summary>
    private const int DocumentNoMaxLength = 150;

    /// <summary>Safety ceiling on how many pages a single lookup will walk. A full document number bound to the
    /// LIKE <c>@SearchText</c> is highly selective, so a real lookup resolves on page 1; this bound only stops an
    /// upstream that reports <c>HasNextPage</c> forever from spinning here.</summary>
    private const int MaxLookupPages = 40;

    public async Task<SpDocumentItem?> LookupAsync(SpDocumentLookupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The boundary guard lives HERE, at the adapter, not only at the caller (design decision, §Data Models):
        // add-item, checkout and any future caller route through this one method, and @SearchText silently
        // truncates a value longer than its declared width. Trim once (REQ-2.6) and reject blank / over-length
        // as a 400 before the value ever binds, so a document that cannot be looked up fails loudly instead of
        // searching under a truncated number.
        var documentNo = request.DocumentNo?.Trim();
        if (string.IsNullOrEmpty(documentNo))
            throw new ArgumentException("documentNo is required.");
        if (documentNo.Length > DocumentNoMaxLength)
            throw new ArgumentException($"documentNo must be at most {DocumentNoMaxLength} characters.");
        if (documentNo.Length > SearchTextMaxLength)
            throw new ArgumentException(
                $"documentNo must be at most {SearchTextMaxLength} characters to be looked up (@SearchText limit).");

        // The group only routes to the Motor vs Non-Motor procedure (REQ-3.2). The search itself is unfiltered by
        // group and by payment status (ALL) so the row that comes back is the authoritative one and its own group
        // wins (REQ-3.5). @SearchText is a LIKE over several columns, so this can return prefix hits too; the
        // exact-match filtering and the 0/1/many decision happen in SelectExactlyOne below.
        var target = request.ProductGroup is ProductGroup.CMI or ProductGroup.VMI
            ? InsuranceType.Motor
            : InsuranceType.NonMotor;

        // Walk every page the LIKE returned, not just the first (REQ-3.4/3.7): an exact match sitting past row 25,
        // or a second exact match that straddles a page boundary, would otherwise read as "not found" or be picked
        // wrongly. Accumulate, then let SelectExactlyOne make the 0/1/many decision over the whole result.
        var all = new List<SpDocumentItem>();
        var pageNo = 1;
        while (true)
        {
            var result = await SearchAsync(
                new SpDocumentSearchRequest(
                    Target: target,
                    SaleCode: request.SaleCode,
                    SearchText: documentNo,
                    InsuredName: null,
                    CoverageStartFrom: null,
                    CoverageStartTo: null,
                    CoverageEndFrom: null,
                    CoverageEndTo: null,
                    PaymentStatus: "ALL",
                    DocumentType: "ALL",
                    ProductGroup: "ALL",
                    PolicyNo: null,
                    ApplicationNo: null,
                    PaidDateFrom: null,
                    PaidDateTo: null,
                    PageNo: pageNo,
                    PageSize: 25,
                    CountMode: "FAST"),
                cancellationToken).ConfigureAwait(false);

            all.AddRange(result.Items);

            if (!result.Page.HasNextPage)
                break;
            if (pageNo >= MaxLookupPages)
            {
                // A single-document lookup returning more than MaxLookupPages*25 LIKE hits is not a shape we can
                // safely scan for an exact match; treat it as an upstream problem (503), never a wrong pick.
                logger.LogError(
                    "SpDocument: lookup for {DocumentNo} on {Target} still reports more pages after {Pages} — refusing to scan further.",
                    documentNo, target, MaxLookupPages);
                throw new UpstreamUnavailableException("Document lookup returned an unexpectedly large result set.");
            }
            pageNo++;
        }

        try
        {
            return SpDocumentMatch.SelectExactlyOne(all, documentNo);
        }
        catch (SpDocumentAmbiguousException ex)
        {
            // REQ-3.7: log at error level here, where the logger lives, then let the typed exception carry to the
            // HTTP boundary (400) — the client never sees which document or how many rows.
            logger.LogError(ex,
                "SpDocument: lookup for {DocumentNo} matched {Count} rows on {Target}; refusing to pick one.",
                documentNo, ex.MatchCount, target);
            throw;
        }
    }

    /// <summary>A connection string that will not even parse is operator configuration, not caller input:
    /// letting the <see cref="ArgumentException"/> out would turn every product request into a misleading
    /// 400. It is the same "upstream unreachable from here" as a bad host, so it takes the same 503 path,
    /// with the parse failure kept server-side.</summary>
    private SqlConnection CreateConnection(string connectionString, InsuranceType target)
    {
        try
        {
            return new SqlConnection(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            logger.LogError(ex, "SpDocument: malformed connection string configured for {Target}.", target);
            throw new UpstreamUnavailableException(
                $"The SpDocument connection string configured for {target} is malformed.", ex);
        }
    }

    /// <summary>
    /// <c>@SaleCode</c> is declared <c>varchar(20)</c> and <see cref="Add"/> sizes the parameter to match, so a
    /// longer or non-ASCII value is truncated/mangled by SqlClient WITHOUT an error and the search silently runs
    /// under a different party's code. The value is already validated at registration (REQ-4.10); this is the
    /// last check before binding, so a value that slipped past that edge fails loudly instead of returning
    /// somebody else's documents (REQ-4.11).
    /// </summary>
    private const int SaleCodeParameterSize = 20;

    private static void VerifySaleCodeBindsIntact(string saleCode)
    {
        if (saleCode is null)
            return; // the request contract says non-null; DBNull is the procedure's own §6 rejection to make.
        if (saleCode.Length > SaleCodeParameterSize)
            throw new SaleCodeBindingException(
                $"Sale code is {saleCode.Length} characters; @SaleCode binds at most {SaleCodeParameterSize} and "
                + "would truncate it.");
        if (saleCode.Any(c => c is < ' ' or > '~'))
            throw new SaleCodeBindingException(
                "Sale code contains a character @SaleCode (varchar) cannot carry unchanged.");
    }

    /// <summary>All 18 §2 parameters, typed and sized as the procedures declare them. The 18th,
    /// <c>@BranchCode</c>, comes from options rather than the request — it is server-side per §1.1.</summary>
    private void AddParameters(SqlCommand command, SpDocumentSearchRequest request)
    {
        Add(command, "@BranchCode", SqlDbType.VarChar, 3, _options.BranchCode);
        Add(command, "@SaleCode", SqlDbType.VarChar, SaleCodeParameterSize, request.SaleCode);
        Add(command, "@SearchText", SqlDbType.NVarChar, 100, request.SearchText);
        Add(command, "@InsuredName", SqlDbType.NVarChar, 200, request.InsuredName);
        Add(command, "@CoverageStartFrom", SqlDbType.Date, 0, request.CoverageStartFrom);
        Add(command, "@CoverageStartTo", SqlDbType.Date, 0, request.CoverageStartTo);
        Add(command, "@CoverageEndFrom", SqlDbType.Date, 0, request.CoverageEndFrom);
        Add(command, "@CoverageEndTo", SqlDbType.Date, 0, request.CoverageEndTo);
        Add(command, "@PaymentStatus", SqlDbType.VarChar, 10, request.PaymentStatus);
        Add(command, "@DocumentType", SqlDbType.VarChar, 20, request.DocumentType);
        Add(command, "@ProductGroup", SqlDbType.VarChar, 10, request.ProductGroup);
        Add(command, "@PolicyNo", SqlDbType.VarChar, 30, request.PolicyNo);
        Add(command, "@ApplicationNo", SqlDbType.VarChar, 30, request.ApplicationNo);
        Add(command, "@PaidDateFrom", SqlDbType.DateTime2, 0, request.PaidDateFrom);
        Add(command, "@PaidDateTo", SqlDbType.DateTime2, 0, request.PaidDateTo);
        Add(command, "@PageNo", SqlDbType.Int, 0, request.PageNo);
        Add(command, "@PageSize", SqlDbType.Int, 0, request.PageSize);
        Add(command, "@CountMode", SqlDbType.VarChar, 10, request.CountMode);
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, int size, object? value)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private static SpPaginationMetadata ReadMetadata(SqlDataReader reader) => new(
        NullableInt64(reader, "TotalRows"),
        NullableInt64(reader, "TotalPages"),
        reader.GetInt32(reader.GetOrdinal("PageNo")),
        reader.GetInt32(reader.GetOrdinal("PageSize")),
        reader.GetBoolean(reader.GetOrdinal("HasNextPage")),
        reader.GetBoolean(reader.GetOrdinal("HasPreviousPage")),
        reader.GetString(reader.GetOrdinal("CountMode")),
        reader.GetInt32(reader.GetOrdinal("SearchWindowMonths")));

    private static SpDocumentItem ReadItem(SqlDataReader reader) => new(
        Text(reader, "InsuranceType"),
        Text(reader, "SourceSystem"),
        Text(reader, "DocumentType"),
        Text(reader, "DocumentNo"),
        Text(reader, "PolicyYear"),
        Text(reader, "ReferenceBranch"),
        Text(reader, "ReferencePre"),
        Text(reader, "PolicySequenceNo"),
        Text(reader, "ReferenceYear"),
        Text(reader, "ReferenceNo"),
        Text(reader, "PolicyBranch"),
        Text(reader, "PolicyType"),
        Text(reader, "SaleCode"),
        Text(reader, "SaleFullName"),
        Text(reader, "BrokerCode"),
        Text(reader, "BrokerName"),
        Text(reader, "PolicyNumber"),
        Text(reader, "ApplicationNumber"),
        Text(reader, "PreviousPolicyNumber"),
        Text(reader, "EndorsementNumber"),
        Timestamp(reader, "StartDate"),
        Timestamp(reader, "EndDate"),
        Text(reader, "ShowName"),
        Amount(reader, "NetPremium"),
        Amount(reader, "Stamp"),
        Amount(reader, "TaxVat"),
        Amount(reader, "TotalPremium"),
        Amount(reader, "CommissionPercent"),
        Amount(reader, "CommissionAmount"),
        Timestamp(reader, "PaidDate"),
        Text(reader, "LicensePlateNumber"),
        Text(reader, "PaymentStatus"));

    private static string? Text(SqlDataReader reader, string column) =>
        Read(reader, column, out var ordinal) ? reader.GetString(ordinal) : null;

    private static decimal? Amount(SqlDataReader reader, string column) =>
        Read(reader, column, out var ordinal) ? reader.GetDecimal(ordinal) : null;

    /// <summary>Datetimes cross naively: the upstream stores Thailand local time in <c>datetime2(0)</c> with
    /// no offset, and this spec keeps that all the way to the wire rather than inventing a UTC conversion.</summary>
    private static DateTime? Timestamp(SqlDataReader reader, string column) =>
        Read(reader, column, out var ordinal) ? reader.GetDateTime(ordinal) : null;

    private static long? NullableInt64(SqlDataReader reader, string column) =>
        Read(reader, column, out var ordinal) ? reader.GetInt64(ordinal) : null;

    private static bool Read(SqlDataReader reader, string column, out int ordinal)
    {
        ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal);
    }
}
