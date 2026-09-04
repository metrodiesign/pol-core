namespace Admins.Application.Users;

/// <summary>The HR-resolved Tier 0 employee profile from <c>dbo.VibEmp</c>.</summary>
public sealed record EmployeeProfile(string FirstName, string LastName);

public enum EmployeeProfileStatus { Found, Missing, Invalid, SourceUnavailable }

public sealed record EmployeeProfileLookup(EmployeeProfileStatus Status, EmployeeProfile? Profile)
{
    public static readonly EmployeeProfileLookup Missing = new(EmployeeProfileStatus.Missing, null);
    public static readonly EmployeeProfileLookup Invalid = new(EmployeeProfileStatus.Invalid, null);
    public static readonly EmployeeProfileLookup SourceUnavailable = new(EmployeeProfileStatus.SourceUnavailable, null);
    public static EmployeeProfileLookup Found(EmployeeProfile profile) => new(EmployeeProfileStatus.Found, profile);
}

/// <summary>Reads one employee profile for a normalized employeeId inside the keyed Admin transaction.</summary>
public interface IEmployeeProfileReader
{
    Task<EmployeeProfileLookup> LookupAsync(string normalizedEmployeeId, CancellationToken cancellationToken);
}

/// <summary>One <c>dbo.VibEmp</c> row projected to the only source columns allowed by the profile contract.</summary>
public sealed record HrEmployeeRow(string? EmpCode, string? FirstNameTh, string? LastNameTh);

/// <summary>The single parameterized external-HR lookup behind <see cref="EmployeeProfileResolver"/>.</summary>
public interface IEmployeeProfileSource
{
    Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(
        string employeeId,
        CancellationToken cancellationToken);
}

/// <summary>Maps exact VibEmp cardinality and validates names without truncation or fallback matching.</summary>
public static class EmployeeProfileResolver
{
    public const int MaxNameLength = 500;

    public static async Task<EmployeeProfileLookup> ResolveAsync(
        IEmployeeProfileSource source,
        string employeeId,
        CancellationToken cancellationToken)
    {
        var employees = await source.FindEmployeesAsync(employeeId, cancellationToken);
        if (employees.Count == 0)
            return EmployeeProfileLookup.Missing;
        if (employees.Count > 1)
            return EmployeeProfileLookup.Invalid;

        var employee = employees[0];
        var firstName = employee.FirstNameTh?.Trim() ?? string.Empty;
        var lastName = employee.LastNameTh?.Trim() ?? string.Empty;
        if (firstName.Length is 0 or > MaxNameLength || lastName.Length is 0 or > MaxNameLength)
            return EmployeeProfileLookup.Invalid;

        return EmployeeProfileLookup.Found(new EmployeeProfile(firstName, lastName));
    }
}
