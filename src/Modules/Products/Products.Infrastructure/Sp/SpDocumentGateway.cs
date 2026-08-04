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

    /// <summary>All 18 §2 parameters, typed and sized as the procedures declare them. The 18th,
    /// <c>@BranchCode</c>, comes from options rather than the request — it is server-side per §1.1.</summary>
    private void AddParameters(SqlCommand command, SpDocumentSearchRequest request)
    {
        Add(command, "@BranchCode", SqlDbType.VarChar, 3, _options.BranchCode);
        Add(command, "@SaleCode", SqlDbType.VarChar, 20, request.SaleCode);
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
