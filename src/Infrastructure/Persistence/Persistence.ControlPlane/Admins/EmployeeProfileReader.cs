using System.Data;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// Read-only lookup over the operator-managed <c>dbo.VibEmp</c> table. The exact parameterized query projects only
/// EmpCode and the two Thai name columns; the table is deliberately absent from every EF model and migration.
/// </summary>
internal sealed class EmployeeProfileReader(ControlPlaneDbContext db, ILogger<EmployeeProfileReader> logger)
    : IEmployeeProfileReader, IEmployeeProfileSource
{
    public async Task<EmployeeProfileLookup> LookupAsync(
        string normalizedEmployeeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await EmployeeProfileResolver.ResolveAsync(this, normalizedEmployeeId, cancellationToken)
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

    public async Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(
        string employeeId,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<VibEmpRow>(
                """
                SELECT TOP (2) EmpCode, FirstNameTh, LastNameTh
                FROM dbo.VibEmp
                WHERE EmpCode = @employeeId;
                """,
                new SqlParameter("@employeeId", SqlDbType.NVarChar, EmployeeIdPolicy.MaxLength)
                {
                    Value = employeeId
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(row => new HrEmployeeRow(row.EmpCode, row.FirstNameTh, row.LastNameTh)).ToList();
    }

    private sealed class VibEmpRow
    {
        public string? EmpCode { get; set; }
        public string? FirstNameTh { get; set; }
        public string? LastNameTh { get; set; }
    }
}
