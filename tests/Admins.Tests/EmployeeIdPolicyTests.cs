using Admins.Domain.Users;

namespace Admins.Tests;

/// <summary>tier0-graph-employee-profile REQ-2.1-2.4, 2.16: pure normalisation of the Graph employeeId.</summary>
public sealed class EmployeeIdPolicyTests
{
    [Theory]
    [InlineData("  ab12  ", "AB12")]
    [InlineData("z9", "Z9")]
    [InlineData("1234567890123456", "1234567890123456")]
    public void Trims_then_uppercases_invariant(string raw, string expected)
    {
        Assert.Equal(EmployeeIdCheck.Ok, EmployeeIdPolicy.TryNormalize(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Empty_after_trim_is_missing(string? raw)
    {
        Assert.Equal(EmployeeIdCheck.Missing, EmployeeIdPolicy.TryNormalize(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("ab 12")]              // inner whitespace (REQ-2.3)
    [InlineData("ab 12")]         // non-breaking space is whitespace too
    [InlineData("abcd")]         // control character (REQ-2.3)
    [InlineData("abcd")]         // DEL is a control character
    [InlineData("12345678901234567")]  // 17 chars (REQ-2.4)
    public void Inner_whitespace_control_or_too_long_is_invalid(string raw)
    {
        Assert.Equal(EmployeeIdCheck.Invalid, EmployeeIdPolicy.TryNormalize(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void Max_length_is_sixteen()
    {
        Assert.Equal(16, EmployeeIdPolicy.MaxLength);
        Assert.Equal(EmployeeIdCheck.Ok, EmployeeIdPolicy.TryNormalize(new string('a', 16), out _));
        Assert.Equal(EmployeeIdCheck.Invalid, EmployeeIdPolicy.TryNormalize(new string('a', 17), out _));
    }

    [Fact]
    public void Trim_happens_before_length_check()
    {
        Assert.Equal(EmployeeIdCheck.Ok, EmployeeIdPolicy.TryNormalize("  " + new string('a', 16) + "  ", out var normalized));
        Assert.Equal(new string('A', 16), normalized);
    }
}
