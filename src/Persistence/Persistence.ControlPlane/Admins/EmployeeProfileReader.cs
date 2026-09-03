using System.Data;
using Admins.Application.Users;
using BuildingBlocks.Application;
using Divisions.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Offices.Domain;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// tier0-graph-employee-profile task 2: the HR lookup port over the operator-loaded mirror tables
/// <c>dbo.VibEmp</c>/<c>dbo.branch</c> (raw, read-only, NOT EF entities — ModelDisjointnessTests forbids mapping
/// them) plus <c>cfg.Offices</c>/<c>cfg.Divisions</c> by <c>LegacyKey</c> (normal EF queries). Every statement is
/// parameterised (REQ-7.10); only the REQ-3.12 columns are read; nothing here writes. A <see cref="SqlException"/>
/// from the mirror tables (208 invalid object, 229 permission denied, timeout, ...) becomes
/// <see cref="EmployeeProfileStatus.SourceUnavailable"/> and is logged as error number + correlation id only
/// (REQ-3.18/3.19) — never the message, the key or the row.
/// </summary>
internal sealed class EmployeeProfileReader(ControlPlaneDbContext db, ILogger<EmployeeProfileReader> logger)
    : IEmployeeProfileReader, IEmployeeProfileSource
{
    public async Task<EmployeeProfileLookup> LookupAsync(string normalizedEmployeeId, CancellationToken cancellationToken)
    {
        try
        {
            return await EmployeeProfileResolver.ResolveAsync(this, normalizedEmployeeId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            logger.LogWarning(
                "HR employee lookup failed. SqlErrorNumber {SqlErrorNumber} CorrelationId {CorrelationId}",
                ex.Number, CorrelationId.Current);
            return EmployeeProfileLookup.SourceUnavailable;
        }
    }

    public async Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(string employeeId, CancellationToken cancellationToken)
    {
        // REQ-3.1-3.3: exact equality on EmpCode under the database's default collation, no LIKE/prefix/padding.
        var rows = await db.Database.SqlQueryRaw<VibEmpRow>(
                """
                SELECT TOP (2) FirstNameTh, LastNameTh, und_brcode, DepartmentID
                FROM dbo.VibEmp WHERE EmpCode = @employeeId;
                """,
                new SqlParameter("@employeeId", SqlDbType.NVarChar, 16) { Value = employeeId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new HrEmployeeRow(r.FirstNameTh, r.LastNameTh, r.und_brcode, r.DepartmentID)).ToList();
    }

    public async Task<int> CountBranchesAsync(string branchCode, CancellationToken cancellationToken)
    {
        // REQ-4.3/4.4/4.18: exact br_code equality (char(3) trailing-space insensitive), active_row ignored.
        var rows = await db.Database.SqlQueryRaw<string>(
                "SELECT TOP (2) br_code AS [Value] FROM dbo.branch WHERE br_code = @branchCode;",
                new SqlParameter("@branchCode", SqlDbType.NVarChar, 100) { Value = branchCode })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<IReadOnlyList<LegacyMappedRow>> FindOfficesAsync(string legacyKey, CancellationToken cancellationToken) =>
        await db.Offices.AsNoTracking()
            .Where(o => o.LegacyKey == legacyKey)
            .Select(o => new LegacyMappedRow(o.Id, o.Status == OfficeStatus.Active))
            .Take(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<LegacyMappedRow>> FindDivisionsAsync(string legacyKey, CancellationToken cancellationToken) =>
        await db.Divisions.AsNoTracking()
            .Where(d => d.LegacyKey == legacyKey)
            .Select(d => new LegacyMappedRow(d.Id, d.Status == DivisionStatus.Active))
            .Take(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    // Column-named projection for SqlQueryRaw (property names must match the SELECT list verbatim).
    private sealed class VibEmpRow
    {
        public string? FirstNameTh { get; set; }
        public string? LastNameTh { get; set; }
        public string? und_brcode { get; set; }
        public string? DepartmentID { get; set; }
    }
}
