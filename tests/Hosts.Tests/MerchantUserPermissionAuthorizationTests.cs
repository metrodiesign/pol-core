extern alias ApiHost;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Permissions;

namespace Hosts.Tests;

/// <summary>The merchant-user permission gate (REQ-17.2), fail-closed: a bound merchant-user whose effective permission set
/// holds the key is admitted; a missing key or an unbound scope (F10) is denied — never a 500.</summary>
public sealed class MerchantUserPermissionAuthorizationTests
{
    private static ApiHost::Api.Merchants.UserScope BoundScope(params string[] permissions)
    {
        var scope = new ApiHost::Api.Merchants.UserScope();
        scope.Set(new Resolution(Guid.NewGuid(), "p@org.com", Guid.NewGuid(), permissions.ToHashSet(StringComparer.Ordinal)));
        return scope;
    }

    [Fact]
    public void Admits_when_the_effective_set_holds_the_permission() =>
        Assert.True(ApiHost::Api.Merchants.UserPermissionAuthorization.IsAllowed(
            BoundScope(Keys.RolesManage, Keys.ProductCreate), Keys.RolesManage));

    [Fact]
    public void Denies_when_the_permission_is_missing() =>
        Assert.False(ApiHost::Api.Merchants.UserPermissionAuthorization.IsAllowed(
            BoundScope(Keys.ProductCreate), Keys.RolesManage));

    [Fact]
    public void Fails_closed_when_no_merchant_user_is_bound() =>
        Assert.False(ApiHost::Api.Merchants.UserPermissionAuthorization.IsAllowed(
            new ApiHost::Api.Merchants.UserScope(), Keys.RolesManage));
}

/// <summary>The boot parity guard (REQ-15.5): a gate key outside the code-canonical catalog is flagged (so the host
/// fails fast); every real catalog key passes.</summary>
public sealed class MerchantUserPermissionParityTests
{
    [Fact]
    public void Every_catalog_key_passes_parity() =>
        Assert.Empty(ApiHost::Api.Merchants.UserPermissionParity.FindUnknown(Keys.AllKeys));

    [Fact]
    public void A_gate_key_outside_the_catalog_is_flagged() =>
        Assert.Contains("bogus.key", ApiHost::Api.Merchants.UserPermissionParity.FindUnknown([Keys.RolesManage, "bogus.key"]));
}
