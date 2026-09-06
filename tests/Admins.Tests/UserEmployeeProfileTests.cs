using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class UserEmployeeProfileTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
    private static User NewMicrosoft() => User.CreateScopedMicrosoft(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        "synthetic@example.test",
        Now);

    [Fact]
    public void First_apply_binds_three_fields_and_bumps_only_resource_version()
    {
        var user = NewMicrosoft();
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");

        Assert.Equal(new EmployeeProfileChange(true, true, true, true), change);
        Assert.Equal("E001", user.EmployeeId);
        Assert.Equal("ชื่อทดสอบ", user.FirstName);
        Assert.Equal("นามสกุลทดสอบ", user.LastName);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
    }

    [Fact]
    public void Identical_profile_is_a_no_op()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");
        var version = user.Version;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อทดสอบ", "นามสกุลทดสอบ");

        Assert.Equal(new EmployeeProfileChange(false, false, false, false), change);
        Assert.Equal(version, user.Version);
    }

    [Fact]
    public void Changed_name_refreshes_names_once_without_org_or_authorization_change()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อเดิม", "นามสกุลเดิม");
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var change = user.ApplyEmployeeProfile("E001", "ชื่อใหม่", "นามสกุลเดิม");

        Assert.Equal(new EmployeeProfileChange(true, false, false, true), change);
        Assert.Equal("ชื่อใหม่", user.FirstName);
        Assert.Equal("นามสกุลเดิม", user.LastName);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
    }

    [Fact]
    public void Changed_employee_id_replaces_three_fields_with_one_resource_version_bump()
    {
        var user = NewMicrosoft();
        user.ApplyEmployeeProfile("E001", "ชื่อเดิม", "นามสกุลเดิม");
        var version = user.Version;
        var authz = user.AuthorizationVersion;

        var change = user.ApplyEmployeeProfile("E002", "ชื่ออื่น", "นามสกุลอื่น");

        Assert.Equal(new EmployeeProfileChange(true, false, true, true), change);
        Assert.Equal("E002", user.EmployeeId);
        Assert.Equal("ชื่ออื่น", user.FirstName);
        Assert.Equal("นามสกุลอื่น", user.LastName);
        Assert.Equal(version + 1, user.Version);
        Assert.Equal(authz, user.AuthorizationVersion);
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

}
