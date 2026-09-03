using Admins.Application.Users;

namespace Admins.Tests;

/// <summary>tier0-graph-employee-profile task 2: the pure status mapping of <see cref="EmployeeProfileResolver"/> driven
/// by a fake source — every row of the design's "status mapping ของ reader" table (REQ-3.4-3.10, 3.15-3.16, 4.2-4.10,
/// 5.2-5.6), including the defence-in-depth duplicate LegacyKey cases the unique index prevents in a real database.</summary>
public sealed class EmployeeProfileReaderStatusTests
{
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Division = Guid.NewGuid();

    [Fact]
    public async Task Single_row_at_every_layer_is_found_with_trimmed_names_and_active_flags()
    {
        var source = Source();
        source.Employees["E1"] = [new HrEmployeeRow(" สมชาย ", " ใจดี ", "Z01", " ZD1 ")];

        var lookup = await EmployeeProfileResolver.ResolveAsync(source, "E1", default);

        Assert.Equal(EmployeeProfileStatus.Found, lookup.Status);
        Assert.Equal(new EmployeeProfile("สมชาย", "ใจดี", Office, true, Division, false), lookup.Profile);
        Assert.Equal(["Z01"], source.BranchQueries);
        Assert.Equal(["Z01"], source.OfficeQueries);
        Assert.Equal(["ZD1"], source.DivisionQueries);
    }

    [Fact]
    public async Task No_employee_row_is_missing_and_stops_before_any_mapping_lookup()
    {
        var source = Source();

        var lookup = await EmployeeProfileResolver.ResolveAsync(source, "E9", default);

        Assert.Equal(EmployeeProfileStatus.Missing, lookup.Status);
        Assert.Null(lookup.Profile);
        Assert.Empty(source.BranchQueries);
        Assert.Empty(source.OfficeQueries);
        Assert.Empty(source.DivisionQueries);
    }

    [Fact]
    public async Task Two_employee_rows_are_invalid()
    {
        var source = Source();
        source.Employees["E1"] = [Row(), Row()];

        Assert.Equal(EmployeeProfileStatus.Invalid, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
        Assert.Empty(source.BranchQueries);
    }

    [Theory]
    [InlineData(null, "ใจดี")]
    [InlineData("   ", "ใจดี")]
    [InlineData("สมชาย", null)]
    [InlineData("สมชาย", "")]
    public async Task Blank_name_is_invalid(string? first, string? last)
    {
        var source = Source();
        source.Employees["E1"] = [new HrEmployeeRow(first, last, "Z01", "ZD1")];

        Assert.Equal(EmployeeProfileStatus.Invalid, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
    }

    [Fact]
    public async Task Name_longer_than_500_after_trim_is_invalid_but_exactly_500_is_fine()
    {
        var source = Source();
        source.Employees["E1"] = [new HrEmployeeRow(new string('ก', 501), "ใจดี", "Z01", "ZD1")];
        Assert.Equal(EmployeeProfileStatus.Invalid, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);

        source.Employees["E1"] = [new HrEmployeeRow("สมชาย", " " + new string('ก', 500) + " ", "Z01", "ZD1")];
        var lookup = await EmployeeProfileResolver.ResolveAsync(source, "E1", default);
        Assert.Equal(EmployeeProfileStatus.Found, lookup.Status);
        Assert.Equal(500, lookup.Profile!.LastName.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_branch_code_is_unmapped_without_querying_branch(string? undBrCode)
    {
        var source = Source();
        source.Employees["E1"] = [new HrEmployeeRow("สมชาย", "ใจดี", undBrCode, "ZD1")];

        Assert.Equal(EmployeeProfileStatus.Unmapped, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
        Assert.Empty(source.BranchQueries);
    }

    [Theory]
    [InlineData(0, EmployeeProfileStatus.Unmapped)]
    [InlineData(2, EmployeeProfileStatus.Invalid)]
    public async Task Branch_count_other_than_one_stops_before_office_lookup(int branches, EmployeeProfileStatus expected)
    {
        var source = Source();
        source.Employees["E1"] = [Row()];
        source.Branches["Z01"] = branches;

        Assert.Equal(expected, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
        Assert.Empty(source.OfficeQueries);
    }

    [Fact]
    public async Task Office_mapping_absent_is_unmapped_and_duplicate_legacy_key_is_invalid()
    {
        var source = Source();
        source.Employees["E1"] = [Row()];

        source.Offices["Z01"] = [];
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);

        source.Offices["Z01"] = [new LegacyMappedRow(Office, true), new LegacyMappedRow(Guid.NewGuid(), true)];
        Assert.Equal(EmployeeProfileStatus.Invalid, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
        Assert.Empty(source.DivisionQueries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Blank_department_id_is_unmapped_without_querying_divisions(string? departmentId)
    {
        var source = Source();
        source.Employees["E1"] = [new HrEmployeeRow("สมชาย", "ใจดี", "Z01", departmentId)];

        Assert.Equal(EmployeeProfileStatus.Unmapped, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
        Assert.Empty(source.DivisionQueries);
    }

    [Fact]
    public async Task Division_mapping_absent_is_unmapped_and_duplicate_legacy_key_is_invalid()
    {
        var source = Source();
        source.Employees["E1"] = [Row()];

        source.Divisions["ZD1"] = [];
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);

        source.Divisions["ZD1"] = [new LegacyMappedRow(Division, true), new LegacyMappedRow(Guid.NewGuid(), false)];
        Assert.Equal(EmployeeProfileStatus.Invalid, (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
    }

    [Fact]
    public async Task Source_exceptions_propagate_to_the_caller()
    {
        var source = Source();
        source.Employees["E1"] = [Row()];
        source.Throw = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EmployeeProfileResolver.ResolveAsync(source, "E1", default));
    }

    private static HrEmployeeRow Row() => new("สมชาย", "ใจดี", "Z01", "ZD1");

    private static FakeSource Source() => new()
    {
        Branches = { ["Z01"] = 1 },
        Offices = { ["Z01"] = [new LegacyMappedRow(Office, true)] },
        Divisions = { ["ZD1"] = [new LegacyMappedRow(Division, false)] },
    };

    private sealed class FakeSource : IEmployeeProfileSource
    {
        public readonly Dictionary<string, List<HrEmployeeRow>> Employees = [];
        public readonly Dictionary<string, int> Branches = [];
        public readonly Dictionary<string, List<LegacyMappedRow>> Offices = [];
        public readonly Dictionary<string, List<LegacyMappedRow>> Divisions = [];
        public readonly List<string> BranchQueries = [];
        public readonly List<string> OfficeQueries = [];
        public readonly List<string> DivisionQueries = [];
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(string employeeId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HrEmployeeRow>>(Employees.GetValueOrDefault(employeeId) ?? []);

        public Task<int> CountBranchesAsync(string branchCode, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            BranchQueries.Add(branchCode);
            return Task.FromResult(Branches.GetValueOrDefault(branchCode));
        }

        public Task<IReadOnlyList<LegacyMappedRow>> FindOfficesAsync(string legacyKey, CancellationToken ct)
        {
            OfficeQueries.Add(legacyKey);
            return Task.FromResult<IReadOnlyList<LegacyMappedRow>>(Offices.GetValueOrDefault(legacyKey) ?? []);
        }

        public Task<IReadOnlyList<LegacyMappedRow>> FindDivisionsAsync(string legacyKey, CancellationToken ct)
        {
            DivisionQueries.Add(legacyKey);
            return Task.FromResult<IReadOnlyList<LegacyMappedRow>>(Divisions.GetValueOrDefault(legacyKey) ?? []);
        }
    }
}
