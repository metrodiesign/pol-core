namespace Admins.Application.Users;

/// <summary>The HR-resolved Tier 0 employee profile (tier0-graph-employee-profile REQ-3/4/5). Office/Division are the
/// canonical <c>cfg.*</c> GUIDs resolved through <c>LegacyKey</c>; the Active flags let the handler apply the
/// "Inactive fails closed only when it differs from the current value" rule (REQ-4.11/4.17, 5.7/5.12).</summary>
public sealed record EmployeeProfile(
    string FirstName, string LastName, Guid OfficeId, bool OfficeActive, Guid DivisionId, bool DivisionActive);

public enum EmployeeProfileStatus { Found, Missing, Invalid, Unmapped, SourceUnavailable }

public sealed record EmployeeProfileLookup(EmployeeProfileStatus Status, EmployeeProfile? Profile)
{
    public static readonly EmployeeProfileLookup Missing = new(EmployeeProfileStatus.Missing, null);
    public static readonly EmployeeProfileLookup Invalid = new(EmployeeProfileStatus.Invalid, null);
    public static readonly EmployeeProfileLookup Unmapped = new(EmployeeProfileStatus.Unmapped, null);
    public static readonly EmployeeProfileLookup SourceUnavailable = new(EmployeeProfileStatus.SourceUnavailable, null);
    public static EmployeeProfileLookup Found(EmployeeProfile profile) => new(EmployeeProfileStatus.Found, profile);
}

/// <summary>Reads the employee profile for one normalised employeeId. Must be called INSIDE the keyed "admin"
/// unit-of-work transaction (REQ-7.16). Any SQL failure against the HR mirror tables (invalid object, permission
/// denied, timeout) is reported as <see cref="EmployeeProfileStatus.SourceUnavailable"/>, never thrown (REQ-3.18).</summary>
public interface IEmployeeProfileReader
{
    Task<EmployeeProfileLookup> LookupAsync(string normalizedEmployeeId, CancellationToken cancellationToken);
}

/// <summary>One <c>cfg.VibEmp</c> row projected to exactly the columns REQ-3.12 allows (EmpCode is the lookup key).</summary>
public sealed record HrEmployeeRow(string? FirstNameTh, string? LastNameTh, string? UndBrCode, string? DepartmentId);

/// <summary>One <c>cfg.Offices</c>/<c>cfg.Divisions</c> row matched by <c>LegacyKey</c>.</summary>
public sealed record LegacyMappedRow(Guid Id, bool IsActive);

/// <summary>The four parameterised lookups behind <see cref="EmployeeProfileResolver"/> (REQ-7.10). Each returns at most
/// TWO rows so the resolver can tell "one" from "more than one" without counting the whole table.</summary>
public interface IEmployeeProfileSource
{
    Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(string employeeId, CancellationToken cancellationToken);
    Task<int> CountBranchesAsync(string branchCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<LegacyMappedRow>> FindOfficesAsync(string legacyKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<LegacyMappedRow>> FindDivisionsAsync(string legacyKey, CancellationToken cancellationToken);
}

/// <summary>Pure status mapping of the design's table (tier0-graph-employee-profile design "status mapping ของ reader"):
/// 0 employees = Missing; 2 employees / blank or over-long name / 2 branches / 2 offices / 2 divisions = Invalid;
/// blank source key / 0 branches / 0 offices / 0 divisions = Unmapped; otherwise Found. Exceptions from the source
/// propagate — the persistence reader turns SQL failures into SourceUnavailable.</summary>
public static class EmployeeProfileResolver
{
    public const int MaxNameLength = 500;

    public static async Task<EmployeeProfileLookup> ResolveAsync(
        IEmployeeProfileSource source, string employeeId, CancellationToken cancellationToken)
    {
        var employees = await source.FindEmployeesAsync(employeeId, cancellationToken);
        if (employees.Count == 0)
            return EmployeeProfileLookup.Missing;                               // REQ-3.4
        if (employees.Count > 1)
            return EmployeeProfileLookup.Invalid;                               // REQ-3.5

        var employee = employees[0];
        var firstName = employee.FirstNameTh?.Trim() ?? string.Empty;          // REQ-3.6
        var lastName = employee.LastNameTh?.Trim() ?? string.Empty;            // REQ-3.7
        if (firstName.Length is 0 or > MaxNameLength || lastName.Length is 0 or > MaxNameLength)
            return EmployeeProfileLookup.Invalid;                               // REQ-3.9/3.10/3.15/3.16

        var branchCode = employee.UndBrCode?.Trim() ?? string.Empty;           // REQ-4.1/4.4
        if (branchCode.Length == 0)
            return EmployeeProfileLookup.Unmapped;                              // REQ-4.2
        var branches = await source.CountBranchesAsync(branchCode, cancellationToken);
        if (branches == 0)
            return EmployeeProfileLookup.Unmapped;                              // REQ-4.5
        if (branches > 1)
            return EmployeeProfileLookup.Invalid;                               // REQ-4.6

        var offices = await source.FindOfficesAsync(branchCode, cancellationToken);
        if (offices.Count == 0)
            return EmployeeProfileLookup.Unmapped;                              // REQ-4.9
        if (offices.Count > 1)
            return EmployeeProfileLookup.Invalid;                               // REQ-4.10

        var departmentId = employee.DepartmentId?.Trim() ?? string.Empty;      // REQ-5.1
        if (departmentId.Length == 0)
            return EmployeeProfileLookup.Unmapped;                              // REQ-5.2
        var divisions = await source.FindDivisionsAsync(departmentId, cancellationToken);
        if (divisions.Count == 0)
            return EmployeeProfileLookup.Unmapped;                              // REQ-5.5
        if (divisions.Count > 1)
            return EmployeeProfileLookup.Invalid;                               // REQ-5.6

        return EmployeeProfileLookup.Found(new EmployeeProfile(
            firstName, lastName, offices[0].Id, offices[0].IsActive, divisions[0].Id, divisions[0].IsActive));
    }
}
