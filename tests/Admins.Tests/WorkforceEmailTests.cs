using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class WorkforceEmailTests
{
    [Theory]
    [InlineData(" Employee@VIRIYAH.CO.TH ", "employee@viriyah.co.th")]
    [InlineData("\u00a0Employee@VIRIYAH.CO.TH\u00a0", "employee@viriyah.co.th")]
    [InlineData("employee+admin@viriyah.co.th", "employee+admin@viriyah.co.th")]
    public void Canonicalize_accepts_exact_corporate_ascii_mailboxes(string value, string expected)
    {
        Assert.True(WorkforceEmail.TryCanonicalize(value, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("employee@example.com")]
    [InlineData("employee@sub.viriyah.co.th")]
    [InlineData("Somchai <employee@viriyah.co.th>")]
    [InlineData("employee @viriyah.co.th")]
    [InlineData("employee@viriyah.co.th extra")]
    [InlineData("พนักงาน@viriyah.co.th")]
    [InlineData("employee@viriyah.co.th,other@viriyah.co.th")]
    public void Canonicalize_rejects_non_canonical_or_non_corporate_values(string? value)
    {
        Assert.False(WorkforceEmail.TryCanonicalize(value, out var canonical));
        Assert.Equal(string.Empty, canonical);
    }

    [Fact]
    public void Canonicalize_accepts_254_characters_and_rejects_255()
    {
        var suffix = "@viriyah.co.th";
        var max = new string('a', WorkforceEmail.MaxLength - suffix.Length) + suffix;
        var tooLong = new string('a', WorkforceEmail.MaxLength + 1 - suffix.Length) + suffix;

        Assert.True(WorkforceEmail.TryCanonicalize(max, out var canonical));
        Assert.Equal(WorkforceEmail.MaxLength, canonical.Length);
        Assert.False(WorkforceEmail.TryCanonicalize(tooLong, out _));
    }
}
