using Admins.Domain.Users;

namespace Admins.Tests;

public sealed class MicrosoftWorkforceIdentityPolicyTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    [Fact]
    public void Exact_microsoft_tuple_is_the_only_microsoft_final_state()
    {
        Assert.True(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "microsoft", TenantId, ObjectId.ToString("D"), TenantId, out var state));
        Assert.Equal(MicrosoftWorkforceIdentityState.BoundMicrosoft, state);

        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "Microsoft", TenantId, ObjectId.ToString("D"), TenantId, out _));
        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "microsoft", null, ObjectId.ToString("D"), TenantId, out _));
        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "microsoft", TenantId, ObjectId.ToString("D").ToUpperInvariant(), TenantId, out _));
        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "microsoft", TenantId, Guid.Empty.ToString("D"), TenantId, out _));
    }

    [Fact]
    public void Bound_non_microsoft_state_requires_null_tenant_and_non_null_subject()
    {
        Assert.True(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "google", null, "subject", TenantId, out var state));
        Assert.Equal(MicrosoftWorkforceIdentityState.BoundNonMicrosoft, state);
        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "google", TenantId, "subject", TenantId, out _));
        Assert.False(MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
            "google", null, null, TenantId, out _));
    }
}

public sealed class AdminContactEmailTests
{
    [Theory]
    [InlineData(" User@Example.COM ", "User@Example.COM")]
    [InlineData("not-an-email", "not-an-email")]
    public void Normalize_trims_and_preserves_contact_value_without_domain_or_identity_rules(
        string input, string expected)
    {
        Assert.True(AdminContactEmail.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_contact_is_absent(string? input)
    {
        Assert.False(AdminContactEmail.TryNormalize(input, out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public void Overlength_contact_is_absent()
    {
        Assert.False(AdminContactEmail.TryNormalize(new string('a', 321), out var actual));
        Assert.Null(actual);
    }
}
