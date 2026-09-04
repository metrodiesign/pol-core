using Admins.Application.Users;

namespace Admins.Tests;

public sealed class EmployeeProfileReaderStatusTests
{
    [Fact]
    public async Task Single_row_is_found_with_trimmed_names()
    {
        var source = new FakeSource(new HrEmployeeRow("E1", " ชื่อทดสอบ ", " นามสกุลทดสอบ "));

        var lookup = await EmployeeProfileResolver.ResolveAsync(source, "E1", default);

        Assert.Equal(EmployeeProfileStatus.Found, lookup.Status);
        Assert.Equal(new EmployeeProfile("ชื่อทดสอบ", "นามสกุลทดสอบ"), lookup.Profile);
        Assert.Equal(["E1"], source.Queries);
    }

    [Fact]
    public async Task No_row_is_missing()
    {
        var lookup = await EmployeeProfileResolver.ResolveAsync(new FakeSource(), "E9", default);

        Assert.Equal(EmployeeProfileStatus.Missing, lookup.Status);
        Assert.Null(lookup.Profile);
    }

    [Fact]
    public async Task Two_rows_are_invalid()
    {
        var source = new FakeSource(
            new HrEmployeeRow("E1", "a", "b"),
            new HrEmployeeRow("E1", "c", "d"));

        Assert.Equal(
            EmployeeProfileStatus.Invalid,
            (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
    }

    [Theory]
    [InlineData(null, "นามสกุลทดสอบ")]
    [InlineData("   ", "นามสกุลทดสอบ")]
    [InlineData("ชื่อทดสอบ", null)]
    [InlineData("ชื่อทดสอบ", "")]
    public async Task Null_or_blank_name_is_invalid(string? first, string? last)
    {
        var source = new FakeSource(new HrEmployeeRow("E1", first, last));

        Assert.Equal(
            EmployeeProfileStatus.Invalid,
            (await EmployeeProfileResolver.ResolveAsync(source, "E1", default)).Status);
    }

    [Fact]
    public async Task Name_length_boundary_is_enforced_without_truncation()
    {
        var over = new FakeSource(new HrEmployeeRow("E1", new string('ก', 501), "x"));
        Assert.Equal(
            EmployeeProfileStatus.Invalid,
            (await EmployeeProfileResolver.ResolveAsync(over, "E1", default)).Status);

        var exact = new FakeSource(new HrEmployeeRow("E1", "x", " " + new string('ก', 500) + " "));
        var lookup = await EmployeeProfileResolver.ResolveAsync(exact, "E1", default);
        Assert.Equal(EmployeeProfileStatus.Found, lookup.Status);
        Assert.Equal(500, lookup.Profile!.LastName.Length);
    }

    [Fact]
    public async Task Source_exception_propagates_to_persistence_boundary()
    {
        var expected = new InvalidOperationException("synthetic failure");
        var source = new FakeSource { Failure = expected };

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EmployeeProfileResolver.ResolveAsync(source, "E1", default)));
    }

    private sealed class FakeSource(params HrEmployeeRow[] rows) : IEmployeeProfileSource
    {
        public readonly List<string> Queries = [];
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<HrEmployeeRow>> FindEmployeesAsync(string employeeId, CancellationToken ct)
        {
            if (Failure is not null)
                throw Failure;
            Queries.Add(employeeId);
            return Task.FromResult<IReadOnlyList<HrEmployeeRow>>(rows);
        }
    }
}
