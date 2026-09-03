extern alias ApiHost;

using Admins.Application.Users;

namespace Hosts.Tests;

internal sealed class TestWorkforceTenantBindingStore : IWorkforceTenantBindingStore
{
    public Guid? EnsuredTenantId { get; private set; }
    public int CallCount { get; private set; }
    public Exception? Failure { get; set; }

    public Task EnsureAsync(Guid configuredTenantId, CancellationToken cancellationToken)
    {
        EnsuredTenantId = configuredTenantId;
        CallCount++;
        if (Failure is not null)
            throw Failure;
        return Task.CompletedTask;
    }

    public Task<Guid> GetRequiredTenantIdAsync(CancellationToken cancellationToken) =>
        Task.FromResult(EnsuredTenantId ?? throw new InvalidOperationException("Tenant binding is unavailable."));
}

public sealed class AdminMicrosoftTenantSnapshotTests
{
    private const string Tenant = "3F2504E0-4F89-41D3-9A0C-0305E82C3301";

    [Fact]
    public void Disabled_provider_ignores_authority_and_has_no_tenant()
    {
        var snapshot = ApiHost::Api.Admins.AdminMicrosoftTenantSnapshot.Parse(
            " ", "https://login.microsoftonline.com/REPLACE_WITH_TENANT_ID/v2.0");

        Assert.False(snapshot.IsEnabled);
        Assert.Null(snapshot.TenantId);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/3F2504E0-4F89-41D3-9A0C-0305E82C3301/v2.0")]
    [InlineData("HTTPS://LOGIN.MICROSOFTONLINE.COM:443/3F2504E0-4F89-41D3-9A0C-0305E82C3301/v2.0/")]
    public void Valid_public_cloud_authority_returns_canonical_tenant(string authority)
    {
        var snapshot = ApiHost::Api.Admins.AdminMicrosoftTenantSnapshot.Parse("client", authority);

        Assert.True(snapshot.IsEnabled);
        Assert.Equal(Guid.Parse(Tenant), snapshot.TenantId);
        Assert.Equal("3f2504e0-4f89-41d3-9a0c-0305e82c3301", snapshot.TenantId?.ToString("D"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("http://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com:444/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://user@login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.us/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://example.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://tenant.ciamlogin.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e04f8941d39a0c0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com/{3f2504e0-4f89-41d3-9a0c-0305e82c3301}/v2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0/extra")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/V2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0//")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0?x=1")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0#x")]
    [InlineData("https://login.microsoftonline.com/%33f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    public void Invalid_or_non_workforce_authority_fails_without_echoing_value(string authority)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ApiHost::Api.Admins.AdminMicrosoftTenantSnapshot.Parse("client", authority));

        Assert.Contains("AdminAuth:Providers:Microsoft:Authority", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3f2504e0-4f89-41d3-9a0c-0305e82c3301", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
