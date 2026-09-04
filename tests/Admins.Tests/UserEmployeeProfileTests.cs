using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class UserEmployeeProfileTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Position = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Office = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Level = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private static readonly Guid Division = Guid.Parse("40000000-0000-4000-8000-000000000001");

    private static User NewMicrosoft() => User.CreateScopedMicrosoft(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        "synthetic@example.test",
        Now,
        Position,
        Office,
        Level,
        Division);

    [Fact]
    public void First_apply_binds_three_fields_and_bumps_only_resource_version()
    {
        var user = NewMicrosoft();
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");

        Assert.Equal(new EmployeeProfileChange(true, true, true), change);
        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal("ชื่อทดสอบ", user.FirstName);
        Assert.Equal("นามสกุลทดสอบ", user.LastName);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
        AssertOrgFields(user);
    }

    [Fact]
    public void Identical_profile_is_a_no_op()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");
        var version = user.Version;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");

        Assert.Equal(new EmployeeProfileChange(false, false, false), change);
        Assert.Equal(version, user.Version);
        AssertOrgFields(user);
    }

    [Fact]
    public void Changed_name_refreshes_names_once_without_org_or_authorization_change()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อเดิม", "นามสกุลเดิม");
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อใหม่", "นามสกุลเดิม");

        Assert.Equal(new EmployeeProfileChange(true, false, true), change);
        Assert.Equal("ชื่อใหม่", user.FirstName);
        Assert.Equal("นามสกุลเดิม", user.LastName);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
        AssertOrgFields(user);
    }

    [Fact]
    public void Different_bound_employee_id_throws_and_keeps_everything()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อเดิม", "นามสกุลเดิม");
        var version = user.Version;

        Assert.Throws<InvalidOperationException>(() =>
            user.ApplyEmployeeProfile("E002", "ชื่ออื่น", "นามสกุลอื่น"));

        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal("ชื่อเดิม", user.FirstName);
        Assert.Equal("นามสกุลเดิม", user.LastName);
        Assert.Equal(version, user.Version);
        AssertOrgFields(user);
    }

    [Theory]
    [InlineData("", "a", "b")]
    [InlineData("E1", " ", "b")]
    [InlineData("E1", "a", "")]
    public void Blank_inputs_are_rejected(string employeeId, string firstName, string lastName)
    {
        var user = NewMicrosoft();
        Assert.Throws<ArgumentException>(() =>
            user.ApplyEmployeeProfile(employeeId, firstName, lastName));
    }

    private static void AssertOrgFields(User user)
    {
        Assert.Equal(Position, user.PositionId);
        Assert.Equal(Office, user.OfficeId);
        Assert.Equal(Level, user.LevelId);
        Assert.Equal(Division, user.DivisionId);
    }
}
