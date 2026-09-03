using Admins.Domain.Users;

namespace Admins.Tests;

/// <summary>tier0-graph-employee-profile: <see cref="User.ApplyEmployeeProfile"/> is the single writer of
/// EmployeeId/FirstName/LastName and refreshes OfficeId/DivisionId (REQ-2.6-2.9, 2.18, 3.13-3.14, 4.16, 5.11,
/// 7.11-7.12, 10.7).</summary>
public sealed class UserEmployeeProfileTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Division = Guid.NewGuid();

    private static User NewMicrosoft() => User.JitProvisionMicrosoft(
        Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.NewGuid(), "employee@viriyah.co.th", Now);

    [Fact]
    public void First_apply_binds_employee_id_and_bumps_version_only()
    {
        var user = NewMicrosoft();
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var changed = user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division);

        Assert.True(changed);
        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal("สมชาย", user.FirstName);
        Assert.Equal("ใจดี", user.LastName);
        Assert.Equal(Office, user.OfficeId);
        Assert.Equal(Division, user.DivisionId);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
    }

    [Fact]
    public void Identical_profile_does_not_bump_version()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division);
        var version = user.Version;

        Assert.False(user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division));
        Assert.Equal(version, user.Version);
    }

    [Fact]
    public void Changed_name_or_office_refreshes_and_bumps_version()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division);
        var version = user.Version;
        var otherOffice = Guid.NewGuid();

        Assert.True(user.ApplyEmployeeProfile("E001", "สมหญิง", "ใจดี", otherOffice, Division));
        Assert.Equal("สมหญิง", user.FirstName);
        Assert.Equal(otherOffice, user.OfficeId);
        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal(version + 1, user.Version);
    }

    [Fact]
    public void Different_bound_employee_id_throws_and_keeps_everything()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division);
        var version = user.Version;

        Assert.Throws<InvalidOperationException>(() =>
            user.ApplyEmployeeProfile("E002", "อื่น", "อื่น", Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal("สมชาย", user.FirstName);
        Assert.Equal(Office, user.OfficeId);
        Assert.Equal(version, user.Version);
    }

    [Fact]
    public void Position_and_level_are_untouched()
    {
        var position = Guid.NewGuid();
        var level = Guid.NewGuid();
        var user = User.CreateScoped("employee@viriyah.co.th", Now, position, Guid.NewGuid(), level, Guid.NewGuid());

        user.ApplyEmployeeProfile("E001", "สมชาย", "ใจดี", Office, Division);

        Assert.Equal(position, user.PositionId);
        Assert.Equal(level, user.LevelId);
        Assert.Equal(Office, user.OfficeId);
        Assert.Equal(Division, user.DivisionId);
    }

    [Theory]
    [InlineData("", "a", "b")]
    [InlineData("E1", " ", "b")]
    [InlineData("E1", "a", "")]
    public void Blank_inputs_are_rejected(string employeeId, string firstName, string lastName)
    {
        var user = NewMicrosoft();
        Assert.Throws<ArgumentException>(() =>
            user.ApplyEmployeeProfile(employeeId, firstName, lastName, Office, Division));
    }
}
